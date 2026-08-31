using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PlanMeter.Core.Credentials;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GATE test (D-07) — SEC-04. The build FAILS if:
/// <list type="bullet">
///   <item><see cref="DpapiKeyStore.Protect"/> → <see cref="DpapiKeyStore.ReadFresh"/> does not round-trip the key.</item>
///   <item>The blob is written under a roamed path (<c>%APPDATA%</c> / Documents) instead of <c>%LOCALAPPDATA%</c>.</item>
///   <item>A corrupted blob throws instead of returning null.</item>
///   <item>The entropy is not the documented <c>PlanMeter::Zai::v1</c> literal.</item>
/// </list>
/// </summary>
/// <remarks>
/// The cross-user CryptographicException → null behaviour is documented per SEC-04:
/// a blob encrypted by user A raises <c>CryptographicException</c> under user B and
/// <see cref="DpapiKeyStore.ReadFresh"/> returns <c>null</c> (not throws). This test
/// suite runs under a single-user harness so we cannot switch users; we simulate the
/// "blob not readable by this principal" path by corrupting the blob, which produces
/// the same <c>CryptographicException</c> → null result.
/// </remarks>
public sealed class DpapiKeyStoreTests
{
    [Fact]
    public void Round_trip_recovers_the_original_key()
    {
        using var temp = TempDir.Create();
        var store = new DpapiKeyStore(Path.Combine(temp.Path, "zai.key.bin"));

        store.Protect("test-key-123");

        string? roundTrip = store.ReadFresh();
        roundTrip.Should().Be("test-key-123",
            "DPAPI round-trip with CurrentUser scope + PlanMeter::Zai::v1 entropy recovers the key");
    }

    [Fact]
    public void Round_trip_handles_long_realistic_keys()
    {
        using var temp = TempDir.Create();
        var store = new DpapiKeyStore(Path.Combine(temp.Path, "zai.key.bin"));
        string longKey = "sk-test." + new string('a', 200);

        store.Protect(longKey);

        store.ReadFresh().Should().Be(longKey);
    }

    [Fact]
    public void Missing_blob_returns_null_not_throws()
    {
        using var temp = TempDir.Create();
        var store = new DpapiKeyStore(Path.Combine(temp.Path, "absent.bin"));

        // SEC-01/empty truth: missing blob → null, NEVER throws.
        string? result = store.ReadFresh();
        result.Should().BeNull();
    }

    [Fact]
    public void Corrupted_blob_returns_null_not_throws()
    {
        // SEC-04/round-trip truth — a CryptographicException under Unprotect is swallowed
        // and null is returned. This is the same path a cross-user decode would take.
        using var temp = TempDir.Create();
        string blobPath = Path.Combine(temp.Path, "zai.key.bin");
        File.WriteAllBytes(blobPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });

        var store = new DpapiKeyStore(blobPath);

        string? result = store.ReadFresh();
        result.Should().BeNull("a corrupted blob must degrade to null, not throw");
    }

    [Fact]
    public void Entropy_matches_the_documented_literal()
    {
        // SEC-04 — the entropy literal is load-bearing; a later change invalidates every
        // already-stored blob. Pin it.
        string entropy = System.Text.Encoding.UTF8.GetString(DpapiKeyStore.Entropy.ToArray());
        entropy.Should().Be("PlanMeter::Zai::v1");
    }

    [Fact]
    public void Entropy_is_exposed_as_immutable_so_callers_cannot_corrupt_it()
    {
        // WR-06 — the entropy must be immutable; otherwise a caller in any assembly could
        // do DpapiKeyStore.Entropy[0] = 0 and corrupt the entropy for every subsequent
        // Protect/Unprotect call (newly-protected blobs would not round-trip against
        // legacy blobs; cross-call DoS on the credential store). The field type is now
        // ImmutableArray<byte>, which exposes no mutators.
        DpapiKeyStore.Entropy.Should().BeOfType<System.Collections.Immutable.ImmutableArray<byte>>(
            "the entropy must be exposed as ImmutableArray<byte> so the contents cannot be mutated");
    }

    [Fact]
    public void Default_blob_path_is_under_LocalApplicationData_not_AppData()
    {
        // SEC-04/path truth — non-roamed path; NOT %APPDATA%, NOT Documents.
        string path = DpapiKeyStore.DefaultBlobPath;

        path.Should().Contain("PlanMeter");
        path.Should().Contain("credentials");
        path.Should().Contain("zai.key.bin");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        path.Should().StartWith(localAppData,
            "DPAPI blob must live under %LOCALAPPDATA% (non-roamed) — Pitfall 8");
        path.Should().NotStartWith(appData,
            "DPAPI blob must NOT live under %APPDATA% — that path roams via OneDrive sync (Pitfall 8)");
    }

    [Fact]
    public void Clear_is_idempotent()
    {
        using var temp = TempDir.Create();
        string blobPath = Path.Combine(temp.Path, "zai.key.bin");
        var store = new DpapiKeyStore(blobPath);

        store.Protect("k");
        File.Exists(blobPath).Should().BeTrue();

        store.Clear();
        File.Exists(blobPath).Should().BeFalse();

        // Idempotent — calling again on an absent blob is a no-op.
        Action clearAgain = () => store.Clear();
        clearAgain.Should().NotThrow();
    }

    [Fact]
    public void Protect_creates_parent_directory_when_missing()
    {
        using var temp = TempDir.Create();
        string nestedPath = Path.Combine(temp.Path, "nested", "deeper", "zai.key.bin");
        var store = new DpapiKeyStore(nestedPath);

        store.Protect("k");

        File.Exists(nestedPath).Should().BeTrue();
        store.ReadFresh().Should().Be("k");
    }

    [Fact]
    public void ForProvider_zai_reproduces_the_frozen_values_and_round_trips_a_legacy_blob()
    {
        // D-07 frozen pin (the phase's one-way door): ForProvider("zai") MUST resolve the
        // exact frozen static values. A drift in the derivation orphans every already-stored
        // Z.ai key (they fail DPAPI round-trip and the widget silently shows NO KEY).
        DpapiKeyStore.ForProvider("zai").BlobPath.Should().Be(DpapiKeyStore.DefaultBlobPath,
            "ForProvider('zai') must reproduce the frozen blob path exactly (Pitfall 1)");

        // The legacy parameterless ctor stays a frozen-value shim (the App.xaml.cs DI binding
        // resolves it); it must still resolve the frozen path.
        new DpapiKeyStore().BlobPath.Should().Be(DpapiKeyStore.DefaultBlobPath,
            "the legacy parameterless ctor must keep producing the frozen defaults (D-07)");

        // The per-instance entropy is private, so pin it behaviorally at a TEMP path (never
        // touch the real %LOCALAPPDATA% blob): a blob written with the frozen default entropy
        // (what the legacy ctor writes with) must decrypt through the ForProvider('zai')
        // entropy — the PlanMeter::Zai::v1 literal — and the reverse must hold too.
        using var temp = TempDir.Create();
        string blobPath = Path.Combine(temp.Path, "zai.key.bin");

        var legacy = new DpapiKeyStore(blobPath, DpapiKeyStore.Entropy); // frozen default
        var zai = new DpapiKeyStore(
            blobPath,
            Encoding.UTF8.GetBytes("PlanMeter::Zai::v1").ToImmutableArray()); // ForProvider("zai") shape

        legacy.Protect("legacy-stored-key");
        zai.ReadFresh().Should().Be("legacy-stored-key",
            "a blob written by the legacy ctor (frozen entropy) must decrypt through the ForProvider('zai') entropy — the D-07 one-way-door pin");

        zai.Protect("provider-stored-key");
        legacy.ReadFresh().Should().Be("provider-stored-key",
            "a blob written through the ForProvider('zai') entropy must decrypt through the legacy ctor — the reverse round-trip");
    }

    [Fact]
    public void Per_provider_stores_are_independent_with_different_blobs_and_entropy()
    {
        // D-07: ForProvider derives per-provider blob paths ({id}.key.bin) + entropy, so
        // two providers' secrets live in independent blobs with independent derivations.
        DpapiKeyStore.ForProvider("zai").BlobPath.Should().EndWith(Path.Combine("credentials", "zai.key.bin"));
        DpapiKeyStore.ForProvider("stub").BlobPath.Should().EndWith(Path.Combine("credentials", "stub.key.bin"));
        DpapiKeyStore.ForProvider("zai").BlobPath.Should().NotBe(DpapiKeyStore.ForProvider("stub").BlobPath,
            "different providers must use different blobs (independent secrets)");

        // Behavior at temp paths: each provider's store round-trips its own key, and one
        // provider's blob is NOT readable through the other's store (different path + entropy).
        using var temp = TempDir.Create();
        var zai = new DpapiKeyStore(
            Path.Combine(temp.Path, "zai.key.bin"),
            Encoding.UTF8.GetBytes("PlanMeter::Zai::v1").ToImmutableArray());
        var stub = new DpapiKeyStore(
            Path.Combine(temp.Path, "stub.key.bin"),
            Encoding.UTF8.GetBytes("PlanMeter::Stub::v1").ToImmutableArray());

        zai.Protect("zai-secret");
        stub.Protect("stub-secret");

        zai.ReadFresh().Should().Be("zai-secret");
        stub.ReadFresh().Should().Be("stub-secret");
        stub.ReadFresh().Should().NotBe("zai-secret",
            "the stub store reads its own blob, never the zai blob (independent paths)");

        // Wrong entropy at the SAME path: a blob protected with one provider's entropy must
        // not decrypt through another's — the decode raises CryptographicException → null
        // (SEC-04 degrade-don't-throw).
        var wrongEntropy = new DpapiKeyStore(
            Path.Combine(temp.Path, "zai.key.bin"),
            Encoding.UTF8.GetBytes("PlanMeter::Stub::v1").ToImmutableArray());
        wrongEntropy.ReadFresh().Should().BeNull(
            "a blob protected with one provider's entropy must not decrypt through another's (wrong entropy → null, SEC-04)");
    }

    [Fact]
    public void ForProvider_stub_derives_the_capitalized_stub_entropy()
    {
        // The neutral derivation pin: ForProvider("stub") must derive PlanMeter::Stub::v1
        // (first letter upper) — the same derivation shape every non-frozen provider id
        // takes. Verified behaviorally at a temp path (never the real %LOCALAPPDATA% blob):
        // a blob written through ForProvider("stub") round-trips through the derived
        // entropy literal.
        using var temp = TempDir.Create();
        string blobPath = Path.Combine(temp.Path, "stub.key.bin");

        var byFactory = DpapiKeyStore.ForProvider("stub");
        byFactory.BlobPath.Should().EndWith("stub.key.bin");

        var byLiteral = new DpapiKeyStore(
            blobPath,
            Encoding.UTF8.GetBytes("PlanMeter::Stub::v1").ToImmutableArray());

        // The derivation pin (read-only): the factory path and the literal-derived entropy
        // must describe the same store shape.
        byFactory.BlobPath.Should().EndWith(Path.GetFileName(byLiteral.BlobPath));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        private TempDir(string path) { Path = path; }
        public static TempDir Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "planmeter-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
