using System;
using System.IO;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// Wire-parser suite for the Grok gRPC-web billing body (GROK-02/GROK-04).
/// Cases + construction helpers are a port of the reference Rust test module
/// (subscription_grok.rs:732-960): frames are constructed in-test, never stored as
/// recorded binaries. The live tracer fixture supplements as a real-shape regression.
/// </summary>
public class GrokWireParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    private static long FutureUnix(int days) => Now.AddDays(days).ToUnixTimeSeconds();

    // ---- construction helpers (mirror the parser's internal builders) ----

    private static byte[] Varint(ulong value) => GrokWireParser.Varint(value);

    private static byte[] FieldVarint(int field, ulong value) => GrokWireParser.FieldVarint(field, value);

    private static byte[] FieldFloat(int field, float value) => GrokWireParser.FieldFloat(field, value);

    private static byte[] FieldMessage(int field, byte[] payload) => GrokWireParser.FieldMessage(field, payload);

    private static byte[] GrpcWebFrame(byte[] payload) => GrokWireParser.GrpcWebFrame(payload);

    private static byte[] Concat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts)
        {
            ms.Write(p, 0, p.Length);
        }
        return ms.ToArray();
    }

    // ---- TryParse: percent + reset ----

    [Fact]
    public void Parse_happy_path_extracts_percent_and_reset()
    {
        long reset = FutureUnix(20);
        byte[] message = Concat(
            FieldMessage(1, FieldFloat(1, 42.5f)),
            FieldMessage(5, FieldVarint(1, (ulong)reset)));

        var snapshot = GrokWireParser.TryParse(GrpcWebFrame(message), Now);

        snapshot.Should().NotBeNull();
        snapshot!.UsedPercent.Should().Be(42.5);
        snapshot.ResetsAtUnixSeconds.Should().Be(reset);
    }

    [Fact]
    public void Percent_picks_shallowest_path_then_earliest()
    {
        // Depth-2 float (30) beats depth-3 float (70).
        byte[] deeperBeaten = GrpcWebFrame(Concat(
            FieldMessage(2, FieldMessage(1, FieldFloat(1, 70f))),
            FieldMessage(1, FieldFloat(1, 30f))));

        var first = GrokWireParser.TryParse(deeperBeaten, Now);
        first!.UsedPercent.Should().Be(30.0);

        // Equal depth: first occurrence wins.
        byte[] equalDepth = GrpcWebFrame(Concat(
            FieldMessage(1, FieldFloat(1, 30f)),
            FieldMessage(2, FieldFloat(1, 70f))));

        var second = GrokWireParser.TryParse(equalDepth, Now);
        second!.UsedPercent.Should().Be(30.0);
    }

    [Fact]
    public void Percent_rejects_out_of_range_floats()
    {
        byte[] body = GrpcWebFrame(Concat(
            FieldMessage(1, FieldFloat(1, 100.5f)),
            FieldMessage(2, FieldFloat(1, -1.0f)),
            FieldMessage(3, FieldFloat(1, float.NaN)),
            FieldMessage(4, FieldFloat(1, float.PositiveInfinity)),
            FieldMessage(5, FieldVarint(1, (ulong)FutureUnix(10)))));

        GrokWireParser.TryParse(body, Now).Should().BeNull();
    }

    [Fact]
    public void Reset_prefers_path_1_5_1_then_nearest_future()
    {
        long nearer = FutureUnix(10);
        long farther = FutureUnix(20);
        long fallback = FutureUnix(40);

        // Exact path [1,5,1] wins over a nearer timestamp elsewhere.
        byte[] exactPathWins = GrpcWebFrame(FieldMessage(1, Concat(
            FieldFloat(1, 25f),
            FieldMessage(4, FieldVarint(1, (ulong)nearer)),
            FieldMessage(5, FieldVarint(1, (ulong)farther)))));

        var byPath = GrokWireParser.TryParse(exactPathWins, Now);
        byPath!.ResetsAtUnixSeconds.Should().Be(farther);

        // Without [1,5,1]: the NEAREST future timestamp wins.
        byte[] nearestWins = GrpcWebFrame(FieldMessage(1, Concat(
            FieldFloat(1, 25f),
            FieldMessage(2, FieldVarint(1, (ulong)FutureUnix(30))),
            FieldMessage(3, FieldVarint(1, (ulong)FutureUnix(5))))));

        var byNearest = GrokWireParser.TryParse(nearestWins, Now);
        byNearest!.ResetsAtUnixSeconds.Should().Be(FutureUnix(5));

        // Past timestamps never win, not even at [1,5,1].
        byte[] pastNeverWins = GrpcWebFrame(FieldMessage(1, Concat(
            FieldFloat(1, 25f),
            FieldMessage(5, FieldVarint(1, (ulong)Now.AddDays(-5).ToUnixTimeSeconds())),
            FieldMessage(3, FieldVarint(1, (ulong)fallback)))));

        var noPast = GrokWireParser.TryParse(pastNeverWins, Now);
        noPast!.ResetsAtUnixSeconds.Should().Be(fallback);
    }

    [Fact]
    public void Zero_usage_special_case()
    {
        long reset = FutureUnix(7);

        // No percent, NO fixed32 anywhere, a future reset, and a usage-period
        // marker at path [1,6] -> 0% (proto3 omits zero-valued fields).
        byte[] withMarkerPrefix = GrpcWebFrame(FieldMessage(1, Concat(
            FieldVarint(6, 9),
            FieldMessage(5, FieldVarint(1, (ulong)reset)))));

        var first = GrokWireParser.TryParse(withMarkerPrefix, Now);
        first.Should().NotBeNull();
        first!.UsedPercent.Should().Be(0.0);
        first.ResetsAtUnixSeconds.Should().Be(reset);

        // Marker variant [1,8,1] in {1,2}.
        byte[] withMarker81 = GrpcWebFrame(FieldMessage(1, Concat(
            FieldMessage(8, FieldVarint(1, 2)),
            FieldMessage(5, FieldVarint(1, (ulong)reset)))));

        var second = GrokWireParser.TryParse(withMarker81, Now);
        second!.UsedPercent.Should().Be(0.0);
    }

    [Fact]
    public void Nothing_plausible_returns_null()
    {
        GrokWireParser.TryParse(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x13, 0x37 }, Now).Should().BeNull();
        GrokWireParser.TryParse(GrpcWebFrame(FieldVarint(2, 12345)), Now).Should().BeNull();
        GrokWireParser.TryParse(Array.Empty<byte>(), Now).Should().BeNull();
    }

    [Fact]
    public void Trailers_only_empty_body_returns_null()
    {
        GrokWireParser.TryParse(ReadOnlySpan<byte>.Empty, Now).Should().BeNull();
    }

    [Fact]
    public void Invalid_frame_lengths_fall_back_to_bare_protobuf()
    {
        // Raw protobuf with no gRPC-web framing: the frame-split fails and the
        // whole body is scanned as bare protobuf.
        byte[] bare = FieldMessage(1, FieldFloat(1, 55.5f));

        var snapshot = GrokWireParser.TryParse(bare, Now);

        snapshot.Should().NotBeNull();
        snapshot!.UsedPercent.Should().Be(55.5);
    }

    [Fact]
    public void Hostile_bytes_never_throw()
    {
        // Depth bomb: nested messages far deeper than the depth-4 bound.
        byte[] depthBomb = GrpcWebFrame(Nest(8, FieldFloat(1, 9f)));
        byte[] Nest(int depth, byte[] inner)
        {
            for (int i = 0; i < depth; i++)
            {
                inner = FieldMessage(1, inner);
            }
            return inner;
        }

        // Length-delimited field claiming ~1 GiB of payload that is not there.
        byte[] hugeLengthClaim = Concat(Varint((ulong)(2 << 3) | 2), Varint(0x4000_0000UL), new byte[] { 0x01 });

        // Varint that never terminates (truncated continuation bytes).
        byte[] truncatedVarint = Concat(new byte[] { 0x08 }, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 });

        var act = () =>
        {
            GrokWireParser.TryParse(depthBomb, Now);
            GrokWireParser.TryParse(hugeLengthClaim, Now);
            GrokWireParser.TryParse(truncatedVarint, Now);
            GrokWireParser.TryParse(ReadOnlySpan<byte>.Empty, Now);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Clamp_binds_final_percent()
    {
        byte[] at99 = GrpcWebFrame(FieldMessage(1, FieldFloat(1, 99.9f)));
        GrokWireParser.TryParse(at99, Now)!.UsedPercent.Should().Be(99.9f);

        byte[] at0 = GrpcWebFrame(FieldMessage(1, FieldFloat(1, 0.0f)));
        var zero = GrokWireParser.TryParse(at0, Now);
        zero.Should().NotBeNull();
        zero!.UsedPercent.Should().Be(0.0);

        byte[] at100 = GrpcWebFrame(FieldMessage(1, FieldFloat(1, 100.0f)));
        GrokWireParser.TryParse(at100, Now)!.UsedPercent.Should().Be(100.0);
    }

    // ---- KindForReset: tier bands (D-12) ----

    [Theory]
    [InlineData(4, WindowKind.Weekly)]
    [InlineData(12, WindowKind.Weekly)]
    [InlineData(13, WindowKind.Credits)]
    [InlineData(19, WindowKind.Credits)]
    [InlineData(20, WindowKind.Monthly)]
    [InlineData(45, WindowKind.Monthly)]
    [InlineData(46, WindowKind.Credits)]
    [InlineData(-3, WindowKind.Credits)]
    public void KindForReset_bands(int daysUntilReset, WindowKind expected)
    {
        DateTimeOffset? reset = Now.AddDays(daysUntilReset);

        GrokWireParser.KindForReset(reset, Now).Should().Be(expected);
    }

    [Fact]
    public void KindForReset_null_reset_is_credits()
    {
        GrokWireParser.KindForReset(null, Now).Should().Be(WindowKind.Credits);
    }

    [Fact]
    public void KindForReset_rounds_half_away_from_zero()
    {
        // 3.5 days rounds to 4 -> Weekly (Rust round(), not banker's rounding).
        DateTimeOffset reset = Now.AddDays(3.5);

        GrokWireParser.KindForReset(reset, Now).Should().Be(WindowKind.Weekly);
    }

    // ---- live fixture regression (conditional) ----

    [Fact]
    public void Live_fixture_regression()
    {
        string fixture = Path.Combine(FindTestProjectDir(), "Fixtures", "grok-credits-live.bin");
        if (!File.Exists(fixture))
        {
            return; // 10-01 tracer did not capture the fixture; skip gracefully
        }

        byte[] body = File.ReadAllBytes(fixture);

        var snapshot = GrokWireParser.TryParse(body, Now);

        snapshot.Should().NotBeNull("the live billing body must parse to a plausible snapshot");
        snapshot!.UsedPercent.Should().BeInRange(0.0, 100.0);
        snapshot.ResetsAtUnixSeconds.Should().HaveValue();
    }

    private static string FindTestProjectDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PlanMeter.Core.Tests.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Test project directory not found.");
    }
}
