using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// D-06/SEC-01 — read-only, read-fresh credential source for the Codex CLI's
/// <c>~/.codex/auth.json</c> OAuth session file. Returns the <c>access_token</c> string
/// when present and non-empty; returns null when the file is absent, malformed, locked,
/// or contains no access_token — NEVER throws (SEC-01 tolerant-DTO pattern).
///
/// The Codex poller reads this source on every startup fetch and every ~10-minute
/// scheduled tick (REFRESH-04). Access remains read-fresh FileShare.Read, never
/// written, never refreshed, and never issues an OAuth token-endpoint call.
///
/// <see cref="DefaultPath"/> resolves the <c>CODEX_HOME</c> env-var override (the
/// documented Codex CLI escape hatch per CLAUDE.md). When <c>CODEX_HOME</c> is set,
/// DefaultPath returns <c>{CODEX_HOME}\auth.json</c>; otherwise
/// <c>%USERPROFILE%\.codex\auth.json</c>.
///
/// SEC-01 read-fresh: opens with <see cref="FileAccess.Read"/> / <see cref="FileShare.Read"/>,
/// copies to a <see cref="MemoryStream"/>, closes the handle immediately — never holds the
/// file open, never writes, never refreshes, never issues an OAuth token-endpoint call.
/// </summary>
public sealed class CodexAuthJsonSource : ICredentialSource
{
    /// <summary>
    /// The default path for the Codex CLI auth file on Windows. Respects the
    /// <c>CODEX_HOME</c> env-var override (the documented Codex CLI escape hatch).
    /// When <c>CODEX_HOME</c> is non-empty, returns <c>{CODEX_HOME}\auth.json</c>;
    /// otherwise returns <c>%USERPROFILE%\.codex\auth.json</c>.
    /// </summary>
    public static string DefaultPath => ResolveDefaultPath();

    private static string ResolveDefaultPath()
    {
        string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrEmpty(codexHome))
        {
            return Path.Combine(codexHome, "auth.json");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "auth.json");
    }

    private readonly string _path;

    /// <summary>
    /// Construct with the documented default path (respects CODEX_HOME override).
    /// </summary>
    public CodexAuthJsonSource() : this(DefaultPath) { }

    /// <summary>
    /// Internal constructor for test injection — allows tests to point at a temp file.
    /// </summary>
    internal CodexAuthJsonSource(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>
    /// SEC-01 — read-fresh. Opens <c>auth.json</c> with <see cref="FileShare.Read"/>,
    /// copies to a <see cref="MemoryStream"/>, closes the handle immediately, then
    /// parses the JSON defensively. Returns the nested or root <c>access_token</c> when
    /// present and non-empty; null otherwise. NEVER throws for missing / malformed / locked files.
    /// </summary>
    public Task<string?> ReadFreshAsync(CancellationToken ct = default)
    {
        CodexAuthDto? dto = TryReadDto(ct);
        string? token = dto?.ResolvedAccessToken;
        return Task.FromResult(string.IsNullOrEmpty(token) ? null : token);
    }

    /// <summary>
    /// D-16 — same FileShare.Read / never-throw path as <see cref="ReadFreshAsync"/>,
    /// returning <c>tokens.account_id</c> when present. Not on <see cref="ICredentialSource"/>.
    /// </summary>
    internal Task<string?> ReadAccountIdFreshAsync(CancellationToken ct = default)
    {
        CodexAuthDto? dto = TryReadDto(ct);
        string? accountId = dto?.ResolvedAccountId;
        return Task.FromResult(string.IsNullOrEmpty(accountId) ? null : accountId);
    }

    private CodexAuthDto? TryReadDto(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            // SEC-01: read-fresh, FileShare.Read — close handle immediately.
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (IOException)
        {
            // File locked exclusively or vanished mid-read — treat as "no usable key".
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // NTFS permission denied on the file — not an IOException subclass;
            // same SEC-01 never-throws contract applies (CR-01).
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodexAuthDto>(bytes);
        }
        catch (JsonException)
        {
            // Malformed JSON — return null, never throw (SEC-01 tolerant-DTO).
            return null;
        }
    }

    /// <summary>
    /// Tolerant DTO for <c>~/.codex/auth.json</c>. Live files nest
    /// <c>tokens.access_token</c> + <c>tokens.account_id</c>; a flat root
    /// <c>access_token</c> is still accepted. <c>refresh_token</c> is absorbed via
    /// <see cref="JsonExtensionData"/> and is never a consumed field.
    /// </summary>
    internal sealed class CodexAuthDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("tokens")]
        public CodexTokensDto? Tokens { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }

        internal string? ResolvedAccessToken =>
            !string.IsNullOrEmpty(Tokens?.AccessToken) ? Tokens.AccessToken : AccessToken;

        internal string? ResolvedAccountId =>
            !string.IsNullOrEmpty(Tokens?.AccountId) ? Tokens.AccountId : null;
    }

    internal sealed class CodexTokensDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("account_id")]
        public string? AccountId { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
