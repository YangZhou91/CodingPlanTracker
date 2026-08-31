using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Credentials;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// D-06/SEC-01 — CodexAuthJsonSource tests. Covers: valid auth.json parsing, missing file,
/// malformed JSON, empty object, IOException (exclusive lock), empty access_token,
/// cancellation, DefaultPath resolution, and CODEX_HOME env-var override.
/// </summary>
public sealed class CodexAuthJsonSourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _authJsonPath;
    private readonly string? _originalCodexHome;

    public CodexAuthJsonSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codex-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _authJsonPath = Path.Combine(_tempDir, "auth.json");
        _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// (a) A valid auth.json with access_token, refresh_token, and id_token
    /// returns the access_token value.
    /// </summary>
    [Fact]
    public async Task Valid_auth_json_returns_access_token()
    {
        string json = JsonSerializer.Serialize(new
        {
            access_token = "abc",
            refresh_token = "xyz",
            id_token = "id-abc",
            expires_in = 3600,
        });
        File.WriteAllText(_authJsonPath, json);

        byte[] before = File.ReadAllBytes(_authJsonPath);

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? result = await source.ReadFreshAsync();

        result.Should().Be("abc", "the access_token field must be returned when present");
        byte[] after = File.ReadAllBytes(_authJsonPath);
        after.Should().BeEquivalentTo(before, "SEC-01: file must remain byte-identical after read");

        string? accountId = await source.ReadAccountIdFreshAsync();
        accountId.Should().BeNull("the flat-root fixture has no tokens.account_id");
    }

    /// <summary>
    /// Live nested shape: tokens.access_token + tokens.account_id. ReadFreshAsync
    /// returns the nested access_token (never refresh_token); ReadAccountIdFreshAsync
    /// returns the uuid; file bytes stay identical (09-01-01, D-16, SEC-01).
    /// </summary>
    [Fact]
    public async Task Nested_tokens_returns_access_token_and_account_id_without_writing()
    {
        string json = /*lang=json,strict*/ """
        {
          "auth_mode": "chatgpt",
          "tokens": {
            "access_token": "nested-abc",
            "account_id": "00000000-0000-0000-0000-000000000001",
            "id_token": "id",
            "refresh_token": "refresh"
          }
        }
        """;
        File.WriteAllText(_authJsonPath, json);
        byte[] before = File.ReadAllBytes(_authJsonPath);

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? token = await source.ReadFreshAsync();
        string? accountId = await source.ReadAccountIdFreshAsync();

        token.Should().Be("nested-abc", "the nested tokens.access_token is the live shape");
        token.Should().NotBe("refresh", "refresh_token must never be the consumed return value");
        accountId.Should().Be("00000000-0000-0000-0000-000000000001");
        byte[] after = File.ReadAllBytes(_authJsonPath);
        after.Should().BeEquivalentTo(before, "SEC-01: file must remain byte-identical after both reads");
    }

    /// <summary>
    /// (b) A missing file returns null — never throws.
    /// </summary>
    [Fact]
    public async Task Missing_file_returns_null()
    {
        var source = new CodexAuthJsonSource(Path.Combine(_tempDir, "nonexistent-auth.json"));
        string? result = await source.ReadFreshAsync();

        result.Should().BeNull("missing auth.json must return null");
    }

    /// <summary>
    /// (c) Malformed JSON returns null — never throws (SEC-01 tolerant-DTO).
    /// </summary>
    [Fact]
    public async Task Malformed_JSON_returns_null()
    {
        File.WriteAllText(_authJsonPath, "{this is not valid json!!!");

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? result = await source.ReadFreshAsync();

        result.Should().BeNull("malformed JSON must return null, never throw");
    }

    /// <summary>
    /// (d) An empty JSON object {} returns null (no access_token field).
    /// </summary>
    [Fact]
    public async Task Empty_object_returns_null()
    {
        File.WriteAllText(_authJsonPath, "{}");

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? result = await source.ReadFreshAsync();

        result.Should().BeNull("empty {} has no access_token — must return null");
    }

    /// <summary>
    /// (e) IOException case: a file locked with FileShare.None returns null — never throws.
    /// </summary>
    [Fact]
    public async Task Locked_file_returns_null()
    {
        File.WriteAllText(_authJsonPath, "{\"access_token\":\"locked-token\"}");

        // Lock the file exclusively before reading.
        using var lockStream = new FileStream(
            _authJsonPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? result = await source.ReadFreshAsync();

        result.Should().BeNull(
            "an exclusively-locked file must return null (IOException caught), never throw");
    }

    /// <summary>
    /// (f) An auth.json with access_token set to empty string returns null.
    /// </summary>
    [Fact]
    public async Task Empty_access_token_returns_null()
    {
        File.WriteAllText(_authJsonPath, "{\"access_token\":\"\",\"refresh_token\":\"xyz\"}");

        var source = new CodexAuthJsonSource(_authJsonPath);
        string? result = await source.ReadFreshAsync();

        result.Should().BeNull("an empty access_token string must be treated as absent");
    }

    /// <summary>
    /// (g) Cancellation token is respected.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_respected()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var source = new CodexAuthJsonSource(_authJsonPath);
        Func<Task> act = () => source.ReadFreshAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// DefaultPath resolves to %USERPROFILE%\.codex\auth.json when CODEX_HOME is not set.
    /// </summary>
    [Fact]
    public void DefaultPath_is_userprofile_codex_auth_json()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "auth.json");

        CodexAuthJsonSource.DefaultPath.Should().Be(expected);
    }

    /// <summary>
    /// When CODEX_HOME is set, DefaultPath resolves to {CODEX_HOME}\auth.json.
    /// </summary>
    [Fact]
    public void DefaultPath_resolves_CODEX_HOME_override()
    {
        try
        {
            string tempPath = Path.Combine(_tempDir, "custom-codex-home");
            Environment.SetEnvironmentVariable("CODEX_HOME", tempPath);

            string expected = Path.Combine(tempPath, "auth.json");
            CodexAuthJsonSource.DefaultPath.Should().Be(expected,
                "DefaultPath must resolve to {CODEX_HOME}\\auth.json when CODEX_HOME is set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
        }
    }
}
