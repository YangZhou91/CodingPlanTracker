using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// SEC-01 + SEC-04 — DPAPI-backed manual key store, generalized per-provider (D-07).
/// Keys live ONLY in per-provider blobs (<c>{provider}.key.bin</c>); the Z.ai blob is the
/// frozen default and MUST keep decrypting (the phase's one-way door).
///
/// SEC-04: the key is encrypted with <c>ProtectedData.Protect</c>,
/// <c>DataProtectionScope.CurrentUser</c> + app-specific entropy bytes. The blob is
/// written to a NON-ROAMED path (<c>%LOCALAPPDATA%\PlanMeter\credentials\...</c>),
/// never <c>%APPDATA%</c>/Documents — OneDrive sync cannot leak it (Pitfall 8).
///
/// SEC-01: <see cref="ReadFresh"/> opens the blob with <c>FileShare.Read</c> and closes
/// the handle immediately — read-only / read-fresh, never held. Phase 1 reads only the
/// app's own blob (no upstream <c>auth.json</c>); the same pattern serves Phase 2's
/// read-only access to upstream files.
///
/// Threat model (honest per Pitfall 8): DPAPI stops casual disk access and other Windows
/// users; it does NOT stop determined same-user malware. TPM-backed (<c>NCrypt</c>) is v2.
/// </summary>
/// <remarks>
/// DPAPI is Windows-only by design. The PlanMeter.Core classlib targets plain net8.0 so
/// provider-adapter code can later port to a cross-platform v2; the DPAPI calls here are
/// guarded with <c>[SupportedOSPlatform("windows")]</c> and the consuming WPF shell
/// (PlanMeter.App, TFM <c>net8.0-windows</c>) is Windows-only by construction.
/// </remarks>
/// <remarks>
/// Phase 2: implements <see cref="ICredentialSource"/> so the generalized
/// <c>ProviderPoller</c> can bind this DPAPI store as the Z.ai provider's key source.
/// The existing <see cref="ReadFreshAsync"/> already matches the interface exactly —
/// no body change, only the declaration.
/// </remarks>
public class DpapiKeyStore : ICredentialSource
{
    /// <summary>
    /// App-specific DPAPI entropy. Changing this value invalidates every previously-stored
    /// blob (a mini-migration); the literal is referenced in the project's SKELETON.md.
    /// </summary>
    /// <remarks>
    /// WR-06 — exposed as <see cref="ImmutableArray{T}"/> (not <c>byte[]</c>) so the
    /// array contents cannot be mutated by callers. The prior <c>static readonly byte[]</c>
    /// prevented reassignment of the field but NOT mutation of the array contents — any
    /// caller in any assembly that references Core could do <c>DpapiKeyStore.Entropy[0] = 0;</c>
    /// and corrupt the entropy for every subsequent Protect/Unprotect call, causing
    /// newly-protected blobs to fail to round-trip against legacy blobs (DoS on the
    /// credential store). <see cref="ImmutableArray{T}"/> is a value type whose contents
    /// are baked at construction and expose no mutators.
    /// </remarks>
    public static readonly ImmutableArray<byte> Entropy =
        Encoding.UTF8.GetBytes("PlanMeter::Zai::v1").ToImmutableArray();

    /// <summary>
    /// The blob path under <c>%LOCALAPPDATA%</c> (non-roamed — NOT <c>%APPDATA%</c>).
    /// </summary>
    public static string DefaultBlobPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlanMeter",
            "credentials",
            "zai.key.bin");

    private readonly string _blobPath;
    private readonly ImmutableArray<byte> _entropy;

    /// <summary>
    /// Construct with the frozen Z.ai defaults (<c>%LOCALAPPDATA%\PlanMeter\credentials\zai.key.bin</c>
    /// + <see cref="Entropy"/>). Kept as a delegating compatibility shim (D-07): Phase 1/2 code
    /// and the DI binding in App.xaml.cs resolve this ctor and MUST keep producing the frozen
    /// values — promoting a different entropy derivation over this shim would orphan every
    /// already-stored Z.ai key (the phase's one-way door).
    /// </summary>
    public DpapiKeyStore() : this(DefaultBlobPath, Entropy) { }

    /// <summary>
    /// Construct with an explicit blob path and the frozen default entropy (test seam; production
    /// uses the default path). Delegates to the parameterized ctor so the entropy source is one
    /// place (<see cref="Entropy"/>).
    /// </summary>
    public DpapiKeyStore(string blobPath) : this(blobPath, Entropy) { }

    /// <summary>
    /// Construct with an explicit blob path and entropy (D-07). The general per-provider entry
    /// point is <see cref="ForProvider"/>; this ctor is the raw seam the factory and the tests
    /// build on. The entropy is immutable (WR-06) and stored per instance so one provider's blob
    /// can never be decrypted with another's derivation.
    /// </summary>
    public DpapiKeyStore(string blobPath, ImmutableArray<byte> entropy)
    {
        _blobPath = blobPath ?? throw new ArgumentNullException(nameof(blobPath));
        _entropy = entropy;
    }

    /// <summary>
    /// D-07 — the per-provider factory. Derives the blob path <c>{id}.key.bin</c> under
    /// <c>%LOCALAPPDATA%\PlanMeter\credentials</c> (non-roamed) and the entropy
    /// <c>PlanMeter::{CapitalizedId}::v1</c>, where the FIRST letter of the provider id is
    /// capitalized. The capitalization step is load-bearing: a naive lowercase derivation
    /// yields <c>PlanMeter::zai::v1</c> for <c>"zai"</c>, which does NOT equal the frozen
    /// entropy literal (see <see cref="Entropy"/>) — the round-trip would fail and every
    /// already-stored Z.ai key would be orphaned (Pitfall 1, D-07 one-way). <c>ForProvider("zai")</c>
    /// therefore reproduces the frozen <see cref="Entropy"/> / <see cref="DefaultBlobPath"/> exactly.
    /// </summary>
    public static DpapiKeyStore ForProvider(ProviderId id)
    {
        string provider = id.ToString();
        string label = char.ToUpperInvariant(provider[0]) + provider.Substring(1);
        ImmutableArray<byte> entropy =
            Encoding.UTF8.GetBytes($"PlanMeter::{label}::v1").ToImmutableArray();
        string blobPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlanMeter",
            "credentials",
            $"{provider}.key.bin");
        return new DpapiKeyStore(blobPath, entropy);
    }

    /// <summary>The blob path this instance reads from / writes to.</summary>
    public string BlobPath => _blobPath;

    /// <summary>
    /// True when a blob currently exists at <see cref="BlobPath"/>. Used by the
    /// D-04 Save handler's defensive assertion (401-rejected key → blob must NOT exist).
    /// </summary>
    public bool BlobPathExists() => File.Exists(_blobPath);

    /// <summary>
    /// DPAPI-encrypt the key and write it to <see cref="BlobPath"/>. Creates the parent
    /// directory if missing. Never writes the plaintext key to disk under any path.
    /// </summary>
    /// <exception cref="CryptographicException">if DPAPI cannot protect under the current principal.</exception>
    [SupportedOSPlatform("windows")]
    public void Protect(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("DpapiKeyStore.Protect: key must be non-empty.", nameof(key));
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(key);
        byte[] blob = ProtectedData.Protect(plaintext, _entropy.ToArray(), DataProtectionScope.CurrentUser);

        // Zero out the plaintext buffer ASAP (defence in depth).
        Array.Clear(plaintext, 0, plaintext.Length);

        string? directory = Path.GetDirectoryName(_blobPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_blobPath, blob);
    }

    /// <summary>
    /// SEC-01 — read-fresh. Open with <see cref="FileShare.Read"/> (read-only, no
    /// exclusive lock), read all bytes, close the handle immediately, then DPAPI-unprotect.
    /// </summary>
    /// <returns>
    /// The plaintext key, or <c>null</c> when the blob is absent, corrupted, or was
    /// encrypted by a different Windows user / app entropy. NEVER throws on missing-file
    /// or cross-user decode (SEC-01/empty + SEC-04/round-trip truths).
    /// </returns>
    [SupportedOSPlatform("windows")]
    public string? ReadFresh()
    {
        if (!File.Exists(_blobPath))
        {
            return null;
        }

        byte[] blob;
        try
        {
            // SEC-01: read-fresh, FileShare.Read (never hold the handle, never block the
            // official client's write — pattern established now for the Phase-2 auth.json case).
            using var stream = new FileStream(
                _blobPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            blob = memory.ToArray();
        }
        catch (IOException)
        {
            // File vanished mid-read or is locked exclusively in a way we can't share —
            // treat as "no usable key right now"; the next poll retries.
            return null;
        }

        try
        {
            byte[] plaintext = ProtectedData.Unprotect(blob, _entropy.ToArray(), DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
            }
        }
        catch (CryptographicException)
        {
            // Cross-user / corrupted / wrong entropy. Per SEC-04/round-trip truth:
            // return null (don't throw) so the row degrades to NO KEY instead of crashing.
            return null;
        }
    }

    /// <summary>
    /// Delete the blob if it exists. Idempotent — calling when no blob is present is a no-op.
    /// </summary>
    public void Clear()
    {
        if (File.Exists(_blobPath))
        {
            File.Delete(_blobPath);
        }
    }

    /// <summary>
    /// Async wrapper for <see cref="ReadFresh"/> so the poller can stay await-friendly.
    /// On non-Windows platforms returns null (the app's WPF shell is Windows-only anyway).
    /// </summary>
    public Task<string?> ReadFreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(ReadFresh());
    }
}
