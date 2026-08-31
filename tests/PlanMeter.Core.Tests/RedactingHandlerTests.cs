using System.Net.Http;
using FluentAssertions;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GATE test (D-07) — SEC-03. The build FAILS if any of the token shapes the project
/// cares about (JWT eyJ..., OpenAI sk-..., sk-admin-..., sk-cp-..., xAI xai-...) survive
/// <see cref="TokenRedactor.Redact"/> or <see cref="RedactingHandler.DescribeRequest"/>.
/// </summary>
/// <remarks>
/// This test is the safety net for SEC-03 — it is NOT advisory. If it fails, a token
/// is leaking into a logger / error-message representation.
/// </remarks>
public sealed class RedactingHandlerTests
{
    [Theory]
    [InlineData("Bearer eyJabc.eyJdef.ghi", true)]
    [InlineData("sk-admin-0123456789abcdef", true)]
    [InlineData("sk-cp-0123456789abcdef", true)]
    [InlineData("sk-0123456789abcdef", true)]
    [InlineData("xai-0123456789abcdef", true)]
    [InlineData("plain-text-without-tokens", false)]
    public void Redact_replaces_every_documented_token_shape(string input, bool shouldRedact)
    {
        string redacted = TokenRedactor.Redact(input);

        if (shouldRedact)
        {
            redacted.Should().Contain("[REDACTED]", $"because '{input}' matches a token shape");
            redacted.Should().NotContain("eyJabc", "JWT prefix must be stripped");
            redacted.Should().NotContain("sk-admin-0123456789abcdef", "OpenAI admin key must be stripped");
            redacted.Should().NotContain("sk-cp-0123456789abcdef", "MiniMax key must be stripped");
            redacted.Should().NotContain("sk-0123456789abcdef", "OpenAI key must be stripped");
            redacted.Should().NotContain("xai-0123456789abcdef", "xAI key must be stripped");
        }
        else
        {
            redacted.Should().Be(input, "no token shape was present");
        }
    }

    // OpenCode GO keys were verified (out of band) to use the existing sk- TokenShape
    // (sk-[A-Za-z0-9_-]{8,}). Do not invent a proprietary prefix — pin the existing shape.
    [Fact]
    public void Redact_opencode_key_uses_existing_sk_shape()
    {
        string fakeKey = "sk-0123456789abcdef";
        TokenRedactor.Redact(fakeKey).Should().Be("[REDACTED]");
    }

    [Fact]
    public void Redact_leaves_non_token_content_intact()
    {
        string input = "GET https://api.z.ai/api/monitor/usage/quota/limit\nAccept: application/json";
        string redacted = TokenRedactor.Redact(input);

        redacted.Should().Be(input, "URL with no token is left untouched");
        redacted.Should().Contain("api.z.ai");
        redacted.Should().Contain("application/json");
    }

    [Fact]
    public void DescribeRequest_strips_Authorization_Cookie_and_x_api_key_headers()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/api/monitor/usage/quota/limit");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer eyJabc.eyJdef.ghi");
        request.Headers.TryAddWithoutValidation("Cookie", "session=abc");
        request.Headers.TryAddWithoutValidation("x-api-key", "sk-admin-0123456789abcdef");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        string description = RedactingHandler.DescribeRequest(request);

        description.Should().NotContain("Bearer eyJabc", "Authorization header value is redacted");
        description.Should().NotContain("session=abc", "Cookie header value is redacted");
        description.Should().NotContain("sk-admin-0123456789abcdef", "x-api-key header value is redacted");
        description.Should().NotContain("eyJ", "JWT token shape must not appear");
        description.Should().Contain("Authorization: [REDACTED]");
        description.Should().Contain("Cookie: [REDACTED]");
        description.Should().Contain("x-api-key: [REDACTED]");
        description.Should().Contain("Accept: application/json");
    }

    [Fact]
    public void IsSensitiveHeader_covers_Authorization_Cookie_and_x_api_key()
    {
        RedactingHandler.IsSensitiveHeader("Authorization").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("authorization").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("Cookie").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("cookie").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("x-api-key").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("Set-Cookie").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("Proxy-Authorization").Should().BeTrue();
        RedactingHandler.IsSensitiveHeader("Accept").Should().BeFalse();
        RedactingHandler.IsSensitiveHeader("User-Agent").Should().BeFalse();
    }

    [Fact]
    public void TokenShapes_regex_set_is_immutable_and_documented()
    {
        // SEC-03/concurrency truth — the regex set is static readonly (immutable, thread-safe).
        TokenRedactor.TokenShapes.Should().NotBeEmpty();
        TokenRedactor.TokenShapes.Length.Should().BeGreaterOrEqualTo(5,
            "the documented set covers JWT, sk-admin-, sk-cp-, sk-, xai-");
    }
}
