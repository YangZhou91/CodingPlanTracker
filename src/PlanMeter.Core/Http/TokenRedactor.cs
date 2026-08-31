using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlanMeter.Core.Http;

/// <summary>
/// SEC-03 — redacts Authorization / Cookie / x-api-key headers and token-shaped
/// strings from any representation that may flow to a logger or error-reporter.
/// Centralised so a future adapter that "forgot" redaction is still redacted —
/// the handler runs on every request through the named "zai" HttpClient.
/// </summary>
/// <remarks>
/// The token-shape regex set is the canonical list pinned by the project's threat model
/// (T-01-01): JWT <c>eyJ...</c>, OpenAI <c>sk-...</c>, OpenAI Admin <c>sk-admin-...</c>,
/// MiniMax Subscription <c>sk-cp-...</c>, xAI <c>xai-...</c>. Add a new prefix only when
/// a new provider's documented key format lands.
/// </remarks>
public static class TokenRedactor
{
    /// <summary>
    /// Token-shape regexes (SEC-03). Order matters only for clarity — each is independently replaced.
    /// Suffix length floor of 8 chars avoids clobbering short legitimate strings like "sk-test".
    /// </summary>
    public static readonly Regex[] TokenShapes =
    {
        // JWT (header starts with eyJ...). Match the JWT prefix + first segment so the
        // rest of the token becomes unrecognisable even if the regex can't span dots.
        new(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // OpenAI Admin keys (sk-admin-...) — must come before the generic sk- regex.
        new(@"sk-admin-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // MiniMax Subscription keys (sk-cp-...) — must come before the generic sk- regex.
        new(@"sk-cp-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Generic OpenAI-style keys (sk-...). Lower bound 8 chars after the prefix.
        new(@"sk-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // xAI keys (xai-...).
        new(@"xai-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    };

    private const string RedactedLiteral = "[REDACTED]";

    /// <summary>
    /// Apply every token-shape regex replacement to <paramref name="input"/>. Returns
    /// the unchanged string if input is null/empty. Each token-shape match is replaced
    /// with the literal "[REDACTED]".
    /// </summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string current = input;
        foreach (var regex in TokenShapes)
        {
            current = regex.Replace(current, RedactedLiteral);
        }

        return current;
    }
}
