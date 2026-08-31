using System;
using System.Globalization;
using System.IO;
using FluentAssertions;
using PlanMeter.Core.Http;
using PlanMeter.Core.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GATE test (CR-01) — SEC-03 sink-path. The build FAILS if a token-shape string carried
/// inside an <see cref="Exception.Message"/> (or any inner-exception message in the chain)
/// survives the <see cref="RedactingSink"/> wrapper and reaches the rendered log output.
/// </summary>
/// <remarks>
/// <see cref="RedactingHandlerTests"/> covers SEC-03 at the HTTP DelegatingHandler layer.
/// This file covers the OTHER surface — the Serilog sink path. The output template's
/// <c>{Exception}</c> formatter calls <see cref="Exception.ToString"/>, which prints the
/// outer message + every inner exception's <see cref="Exception.Message"/> verbatim. Without
/// <see cref="RedactingSink"/> wrapping the inner file sink, any unhandled exception whose
/// message (or inner chain) carries a token-shape string (e.g. a rewrapped
/// <c>HttpRequestException</c> whose inner message echoes a URL or response body containing
/// a Bearer token) leaks that token straight to the rolling log file.
/// <para>
/// All tokens used here are SYNTHETIC dogfood strings crafted to match the
/// <see cref="TokenRedactor.TokenShapes"/> regex set. No real credential is read, loaded,
/// or constructed — the test never touches <c>DpapiKeyStore</c>, the network, or any
/// provider response. The dogfood prefix <c>eyJ.fake.dogfood.not-a-real-key.xxx</c> matches
/// the JWT shape while being obviously non-functional.
/// </para>
/// </remarks>
public sealed class RedactingSinkTests
{
    /// <summary>
    /// The exact output template <c>App.xaml.cs</c> wires into the real file sink. Asserting
    /// against this template (not a hand-rolled formatter) makes the test faithful to what
    /// actually lands on disk in production.
    /// </summary>
    private const string ProductionOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Capture sink — records every <see cref="LogEvent"/> the wrapping
    /// <see cref="RedactingSink"/> forwards, so the test can render it through the production
    /// output template and assert on the final string.
    /// </summary>
    private sealed class CaptureSink : ILogEventSink
    {
        public LogEvent? LastEmitted { get; private set; }

        public void Emit(LogEvent logEvent) => LastEmitted = logEvent;
    }

    private static string RenderThroughProductionTemplate(LogEvent logEvent)
    {
        var formatter = new MessageTemplateTextFormatter(
            ProductionOutputTemplate,
            CultureInfo.InvariantCulture);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static (RedactingSink sink, CaptureSink capture) BuildPipeline()
    {
        var capture = new CaptureSink();
        return (new RedactingSink(capture), capture);
    }

    private static LogEvent BuildEvent(Exception ex, string messageTemplate = "PlanMeter: test exception.")
    {
        // Hand-build a minimal LogEvent the way Serilog would — timestamp, level, the exception,
        // a trivial message template, no properties. This is the shape Serilog produces for
        // Log.Error(ex, "...") / Log.Fatal(ex, "...").
        return new LogEvent(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            LogEventLevel.Error,
            ex,
            new Serilog.Parsing.MessageTemplateParser().Parse(messageTemplate),
            properties: Array.Empty<LogEventProperty>());
    }

    [Fact]
    public void Redacts_JWT_token_in_outer_exception_message_before_it_reaches_the_sink()
    {
        // Dogfood JWT — matches the eyJ<head>.eyJ<payload>.<sig> regex but decodes to a
        // clearly-non-functional "fake" payload. No real credential.
        var ex = new Exception("failed: Bearer eyJfake.eyJfake.dogfoodkey");
        var (sink, capture) = BuildPipeline();

        sink.Emit(BuildEvent(ex));

        var rendered = RenderThroughProductionTemplate(capture.LastEmitted!);
        rendered.Should().Contain("[REDACTED]", "the JWT in the exception message must be redacted before rendering");
        rendered.Should().NotContain("eyJfake", "the raw JWT prefix must not survive redaction");
        rendered.Should().NotContain("dogfoodkey", "the JWT body must not survive redaction");
    }

    [Fact]
    public void Redacts_token_in_inner_exception_chain_not_just_the_outer_message()
    {
        // The CR-01 threat model: the OUTER message is benign, but the INNER exception's
        // message echoes a token (the documented failure mode is a rewrapped HttpRequestException
        // whose inner message echoes a URL/body containing a Bearer token). ex.ToString()
        // prints both — without RedactingSink, the inner token leaks.
        var innerWithToken = new Exception("GET https://api.z.ai/... Authorization: Bearer eyJfake.eyJfake.innerdogfood");
        var outer = new Exception("The request failed.", innerWithToken);
        var (sink, capture) = BuildPipeline();

        sink.Emit(BuildEvent(outer));

        var rendered = RenderThroughProductionTemplate(capture.LastEmitted!);
        rendered.Should().Contain("[REDACTED]", "the inner-exception JWT must be redacted");
        rendered.Should().NotContain("eyJfake", "the raw inner JWT prefix must not survive");
        rendered.Should().NotContain("innerdogfood", "the inner JWT body must not survive");
        rendered.Should().Contain("The request failed.", "the benign outer message is preserved");
    }

    [Theory]
    [InlineData("sk-admin-0123456789abcdef", "OpenAI admin key")]
    [InlineData("sk-cp-0123456789abcdef", "MiniMax subscription key")]
    [InlineData("sk-0123456789abcdef", "OpenAI key")]
    [InlineData("xai-0123456789abcdef", "xAI key")]
    public void Redacts_every_documented_non_JWT_token_shape(string token, string label)
    {
        var ex = new Exception($"boom: {token}");
        var (sink, capture) = BuildPipeline();

        sink.Emit(BuildEvent(ex));

        var rendered = RenderThroughProductionTemplate(capture.LastEmitted!);
        rendered.Should().Contain("[REDACTED]", $"the {label} must be redacted");
        rendered.Should().NotContain(token, $"the raw {label} must not survive redaction");
    }

    [Fact]
    public void Redacts_token_in_a_deeply_nested_inner_chain()
    {
        // Three-deep inner chain — verifies RedactException recurses through every level,
        // not just the first inner. A future refactor that "only redacts the top message"
        // would pass a single-level test but fail this one.
        var deepest = new Exception("innermost: xai-0123456789abcdef");
        var middle = new Exception("middle: benign", deepest);
        var outer = new Exception("outer: also benign", middle);
        var (sink, capture) = BuildPipeline();

        sink.Emit(BuildEvent(outer));

        var rendered = RenderThroughProductionTemplate(capture.LastEmitted!);
        rendered.Should().Contain("[REDACTED]", "the deeply-nested xAI token must be redacted");
        rendered.Should().NotContain("xai-0123456789abcdef", "the raw xAI key buried two levels deep must not survive");
        rendered.Should().Contain("outer: also benign", "benign outer text preserved");
        rendered.Should().Contain("middle: benign", "benign middle text preserved");
    }

    [Fact]
    public void Passes_event_through_unchanged_when_there_is_no_exception()
    {
        // A normal log line (no exception attached) must not be mutated — the sink is a
        // no-op wrapper for the common path, so message-template parameters that DON'T
        // match a token shape render verbatim. (Token-shape properties are the enricher's
        // job, tested separately.)
        var (sink, capture) = BuildPipeline();
        var noException = BuildEvent(ex: null!, messageTemplate: "Z.ai quota fetched in {ElapsedMs} ms");

        sink.Emit(noException);

        capture.LastEmitted.Should().NotBeNull("the event must be forwarded");
        capture.LastEmitted!.Exception.Should().BeNull("no exception is attached");
        // Rendered output carries the template literal.
        var rendered = RenderThroughProductionTemplate(capture.LastEmitted);
        rendered.Should().Contain("Z.ai quota fetched in");
    }

    [Fact]
    public void Does_not_over_redact_benign_text_that_is_not_token_shaped()
    {
        // The redactor must not mangle normal diagnostic text. A message containing "api.z.ai"
        // and ordinary words must pass through untouched — only the documented token prefixes
        // are replaced.
        var ex = new Exception("GET https://api.z.ai/api/monitor/usage/quota/limit timed out after 15s");
        var (sink, capture) = BuildPipeline();

        sink.Emit(BuildEvent(ex));

        var rendered = RenderThroughProductionTemplate(capture.LastEmitted!);
        rendered.Should().Contain("api.z.ai", "the allow-listed host is not a token and must not be redacted");
        rendered.Should().Contain("quota/limit", "the endpoint path is not a token");
        rendered.Should().NotContain("[REDACTED]", "no token shape was present, so nothing should be redacted");
    }
}
