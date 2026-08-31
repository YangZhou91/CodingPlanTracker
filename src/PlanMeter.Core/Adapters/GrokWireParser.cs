using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// The plausible fields the heuristic scan extracted from a Grok billing body.
/// </summary>
/// <param name="UsedPercent">Clamped used percentage in [0, 100].</param>
/// <param name="ResetsAtUnixSeconds">Preferred future reset timestamp, null when none was found.</param>
public sealed record GrokWireSnapshot(double UsedPercent, long? ResetsAtUnixSeconds);

/// <summary>
/// Heuristic gRPC-web / protobuf wire scanner for the Grok billing response
/// (GROK-02/GROK-04). A pure translation of the reference Rust heuristics:
/// no .proto, no schema — collect fixed32 floats and varints with their
/// field-number paths, then pick the plausible usage figure and reset time.
/// Never fabricates: nothing plausible yields null, never a number.
/// Pure function of bytes + now; zero I/O; never throws (hostile-input-safe:
/// bounded depth, length-checked frames and fields, resync-on-garbage).
/// </summary>
public static class GrokWireParser
{
    private const int MaxDepth = 4;
    private const long MinPlausibleUnixSeconds = 1_700_000_000L;
    private const long MaxPlausibleUnixSeconds = 2_100_000_000L;

    // D-12 tier bands — inclusive, on days rounded half-away-from-zero.
    internal const int WeeklyMinDays = 4;
    internal const int WeeklyMaxDays = 12;
    internal const int MonthlyMinDays = 20;
    internal const int MonthlyMaxDays = 45;

    /// <summary>
    /// Scan a billing response body for a plausible {used %, reset time} pair.
    /// Body may be gRPC-web framed (data + trailer frames) or bare protobuf.
    /// Returns null when nothing plausible is found.
    /// </summary>
    public static GrokWireSnapshot? TryParse(ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        try
        {
            byte[]? scanTarget = ExtractDataPayload(body);
            if (scanTarget is null || scanTarget.Length == 0)
            {
                return null;
            }

            var scan = new Scan();
            ScanLenient(scanTarget, scan);

            double? percent = PickPercent(scan);
            long? reset = PickReset(scan, now);

            if (percent is null)
            {
                // Zero-usage special case (proto3 omits zero-valued fields):
                // no percent AND no fixed32 anywhere AND a reset AND a usage-period marker.
                if (scan.Floats.Count == 0 && reset.HasValue && HasUsagePeriodMarker(scan))
                {
                    return new GrokWireSnapshot(0.0, reset);
                }

                return null;
            }

            return new GrokWireSnapshot(Math.Clamp(percent.Value, 0.0, 100.0), reset);
        }
        catch
        {
            // Hostile bytes must degrade to null, never crash the poller.
            return null;
        }
    }

    /// <summary>
    /// D-12 tier mapping: rounded days-to-reset in [4,12] -> Weekly, [20,45] -> Monthly,
    /// anything else (including no reset) -> Credits. Rounding is half-away-from-zero
    /// (matches the reference implementation's round(), not banker's rounding).
    /// </summary>
    public static WindowKind KindForReset(DateTimeOffset? resetsAtUtc, DateTimeOffset now)
    {
        if (resetsAtUtc is not DateTimeOffset reset)
        {
            return WindowKind.Credits;
        }

        double days = (reset - now).TotalDays;
        long rounded = (long)Math.Round(days, MidpointRounding.AwayFromZero);

        if (rounded >= WeeklyMinDays && rounded <= WeeklyMaxDays)
        {
            return WindowKind.Weekly;
        }

        if (rounded >= MonthlyMinDays && rounded <= MonthlyMaxDays)
        {
            return WindowKind.Monthly;
        }

        return WindowKind.Credits;
    }

    // ---- frame extraction (step 1) ----

    /// <summary>
    /// Split the body into gRPC-web data frames (flags byte with 0x80 clear) and
    /// concatenate their payloads. Trailer frames (0x80 set) are skipped. When no
    /// valid framing exists and the first byte looks like a protobuf tag, fall back
    /// to treating the whole body as bare protobuf.
    /// </summary>
    private static byte[]? ExtractDataPayload(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        int pos = 0;
        bool sawFrame = false;
        using var data = new MemoryStream();
        while (pos + 5 <= body.Length)
        {
            byte flags = body[pos];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(pos + 1, 4));
            if (length > (uint)(body.Length - pos - 5))
            {
                break; // invalid frame length
            }

            sawFrame = true;
            if ((flags & 0x80) == 0)
            {
                data.Write(body.Slice(pos + 5, (int)length));
            }

            pos += 5 + (int)length;
        }

        if (sawFrame)
        {
            return data.ToArray();
        }

        return IsPlausibleTag(body[0]) ? body.ToArray() : null;
    }

    private static bool IsPlausibleTag(byte tag)
    {
        int field = tag >> 3;
        int wire = tag & 0x7;
        return field > 0 && wire is 0 or 1 or 2 or 5;
    }

    // ---- protobuf field scan (step 2) ----

    private sealed class Scan
    {
        public List<(int[] Path, double Value)> Floats { get; } = new();

        public List<(int[] Path, ulong Value)> Varints { get; } = new();
    }

    /// <summary>
    /// Top-level scan: tolerant — on any malformed field, resync at field_start + 1
    /// and keep going (hostile-byte safety).
    /// </summary>
    private static void ScanLenient(ReadOnlySpan<byte> span, Scan scan)
    {
        int pos = 0;
        while (pos < span.Length)
        {
            int fieldStart = pos;
            if (!TryReadVarint(span, ref pos, out ulong tag) || (tag >> 3) == 0)
            {
                pos = fieldStart + 1;
                continue;
            }

            int field = (int)(tag >> 3);
            switch (tag & 0x7)
            {
                case 0: // varint
                    if (!TryReadVarint(span, ref pos, out ulong varintValue))
                    {
                        pos = fieldStart + 1;
                        continue;
                    }

                    scan.Varints.Add((new[] { field }, varintValue));
                    break;

                case 1: // fixed64 — opaque here, skip
                    if (pos + 8 > span.Length)
                    {
                        pos = fieldStart + 1;
                        continue;
                    }

                    pos += 8;
                    break;

                case 2: // length-delimited — try as a nested message
                    if (!TryReadVarint(span, ref pos, out ulong length) || length > (ulong)(span.Length - pos))
                    {
                        pos = fieldStart + 1;
                        continue;
                    }

                    var slice = span.Slice(pos, (int)length);
                    var child = new Scan();
                    if (TryScanStrict(slice, 1, new[] { field }, child))
                    {
                        scan.Floats.AddRange(child.Floats);
                        scan.Varints.AddRange(child.Varints);
                    }
                    else
                    {
                        pos = fieldStart + 1;
                        continue;
                    }

                    pos += (int)length;
                    break;

                case 5: // fixed32 float
                    if (pos + 4 > span.Length)
                    {
                        pos = fieldStart + 1;
                        continue;
                    }

                    scan.Floats.Add((new[] { field }, ReadFixed32(span.Slice(pos, 4))));
                    pos += 4;
                    break;

                default: // wire 3/4 (groups) and 6/7 — not plausible here
                    pos = fieldStart + 1;
                    continue;
            }
        }
    }

    /// <summary>
    /// Nested-message attempt: strict — the slice must parse cleanly as a complete
    /// field sequence, otherwise the caller resyncs. Depth-bounded at <see cref="MaxDepth"/>.
    /// </summary>
    private static bool TryScanStrict(ReadOnlySpan<byte> span, int parentPathLength, int[] parentPath, Scan scan)
    {
        int pos = 0;
        while (pos < span.Length)
        {
            if (!TryReadVarint(span, ref pos, out ulong tag) || (tag >> 3) == 0)
            {
                return false;
            }

            int field = (int)(tag >> 3);
            if (parentPathLength + 1 > MaxDepth)
            {
                return false;
            }

            var path = Append(parentPath, field);
            switch (tag & 0x7)
            {
                case 0:
                    if (!TryReadVarint(span, ref pos, out ulong varintValue))
                    {
                        return false;
                    }

                    scan.Varints.Add((path, varintValue));
                    break;

                case 1:
                    if (pos + 8 > span.Length)
                    {
                        return false;
                    }

                    pos += 8;
                    break;

                case 2:
                    if (!TryReadVarint(span, ref pos, out ulong length) || length > (ulong)(span.Length - pos))
                    {
                        return false;
                    }

                    var slice = span.Slice(pos, (int)length);
                    if (parentPathLength + 2 <= MaxDepth)
                    {
                        var child = new Scan();
                        if (!TryScanStrict(slice, parentPathLength + 1, path, child))
                        {
                            return false;
                        }

                        scan.Floats.AddRange(child.Floats);
                        scan.Varints.AddRange(child.Varints);
                    }

                    pos += (int)length;
                    break;

                case 5:
                    if (pos + 4 > span.Length)
                    {
                        return false;
                    }

                    scan.Floats.Add((path, ReadFixed32(span.Slice(pos, 4))));
                    pos += 4;
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> span, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < span.Length)
        {
            byte b = span[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
            if (shift >= 64)
            {
                return false; // truncated or over-long varint
            }
        }

        return false;
    }

    private static double ReadFixed32(ReadOnlySpan<byte> bytes)
    {
        // Protobuf fixed32 is little-endian on the wire; widening float->double is lossless.
        int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static int[] Append(int[] parentPath, int field)
    {
        var path = new int[parentPath.Length + 1];
        parentPath.AsSpan().CopyTo(path);
        path[^1] = field;
        return path;
    }

    // ---- selection heuristics (steps 3-5) ----

    /// <summary>
    /// Qualifying floats: path ends in field number 1, finite, value in [0, 100].
    /// Shallowest path first, then earliest occurrence.
    /// </summary>
    private static double? PickPercent(Scan scan)
    {
        double? best = null;
        int bestDepth = int.MaxValue;
        foreach (var (path, value) in scan.Floats)
        {
            if (path[^1] != 1 || !double.IsFinite(value) || value < 0.0 || value > 100.0)
            {
                continue;
            }

            if (path.Length < bestDepth)
            {
                best = value;
                bestDepth = path.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Future varints in the plausible unix-seconds range; exact path [1,5,1] wins,
    /// else the nearest future timestamp. Past timestamps never win.
    /// </summary>
    private static long? PickReset(Scan scan, DateTimeOffset now)
    {
        long nowSeconds = now.ToUnixTimeSeconds();
        long? exactPath = null;
        long? nearest = null;
        foreach (var (path, value) in scan.Varints)
        {
            if (value < (ulong)MinPlausibleUnixSeconds || value > (ulong)MaxPlausibleUnixSeconds)
            {
                continue;
            }

            long seconds = (long)value;
            if (seconds <= nowSeconds)
            {
                continue; // past timestamps never win
            }

            if (path.Length == 3 && path[0] == 1 && path[1] == 5 && path[2] == 1)
            {
                exactPath ??= seconds;
            }

            if (nearest is null || seconds < nearest)
            {
                nearest = seconds;
            }
        }

        return exactPath ?? nearest;
    }

    /// <summary>
    /// Usage-period marker for the zero-usage special case: a field under [1,6],
    /// or a [1,8,1] varint with value 1 or 2.
    /// </summary>
    private static bool HasUsagePeriodMarker(Scan scan)
    {
        foreach (var (path, value) in scan.Varints)
        {
            if (path.Length >= 2 && path[0] == 1 && path[1] == 6)
            {
                return true;
            }

            if (path.Length == 3 && path[0] == 1 && path[1] == 8 && path[2] == 1 && (value == 1 || value == 2))
            {
                return true;
            }
        }

        return false;
    }

    // ---- construction helpers (ported reference test module; test use) ----

    /// <summary>Unsigned base-128 varint encoding.</summary>
    internal static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>(10);
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    /// <summary>Varint field (wire type 0).</summary>
    internal static byte[] FieldVarint(int field, ulong value)
        => Concat(Varint(((ulong)field << 3) | 0), Varint(value));

    /// <summary>Fixed32 float field (wire type 5), little-endian IEEE-754.</summary>
    internal static byte[] FieldFloat(int field, float value)
    {
        byte[] payload = BitConverter.GetBytes(BitConverter.SingleToInt32Bits(value));
        return Concat(Varint(((ulong)field << 3) | 5), payload);
    }

    /// <summary>Length-delimited field (wire type 2).</summary>
    internal static byte[] FieldMessage(int field, byte[] payload)
        => Concat(Varint(((ulong)field << 3) | 2), Varint((ulong)payload.Length), payload);

    /// <summary>gRPC-web data frame: flags 0x00 + 4-byte big-endian length + payload.</summary>
    internal static byte[] GrpcWebFrame(byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = 0x00;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            ms.Write(part, 0, part.Length);
        }

        return ms.ToArray();
    }
}
