using System;
using System.Collections.Generic;
using System.Linq;
using Serilog.Core;
using Serilog.Events;
using PlanMeter.Core.Http;

namespace PlanMeter.Core.Logging;

/// <summary>
/// SEC-03 defensive in depth — re-runs the same token-shape regex pass as
/// <see cref="RedactingHandler"/> on every Serilog event BEFORE it hits the file sink.
/// Both the handler and this enricher strip tokens, so a future code path that bypasses
/// the handler is still safe.
/// </summary>
/// <remarks>
/// Two layers compose to cover the Serilog surface:
/// <list type="bullet">
///   <item><see cref="RedactingEnricher"/> — walks <see cref="LogEvent.Properties"/>
///       and replaces any scalar-string property whose value matches a token shape with
///       the literal "[REDACTED]". Covers message-template parameters and named properties.</item>
///   <item><see cref="RedactingSink"/> — wraps the inner sink and rebuilds the
///       <see cref="LogEvent"/> with a redacted exception tree before emission. This is
///       load-bearing for SEC-03: the output template's <c>{Exception}</c> formatter
///       calls <see cref="Exception.ToString"/>, which prints the outer message, the
///       stack trace, AND every inner exception's <see cref="Exception.Message"/>
///       verbatim. Enrichers cannot rewrite the exception object — only a sink wrapper
///       can. Without this, any <c>Log.Error(ex, ...)</c> / <c>Log.Fatal(ex, ...)</c>
///       whose <c>ex.Message</c> or inner chain carries a token-shape string (e.g. a
///       rewrapped <see cref="System.Net.Http.HttpRequestException"/> whose inner
///       message echoes a URL or response body containing a Bearer token) leaks the
///       token straight to the rolling log file under
///       <c>%LOCALAPPDATA%\PlanMeter\logs</c>.</item>
/// </list>
///
/// Lives in PlanMeter.Core (not PlanMeter.App) deliberately: it depends only on Serilog's
/// portable abstractions (<c>Serilog.Core</c> / <c>Serilog.Events</c>) and
/// <see cref="TokenRedactor"/> — zero WPF coupling — so it stays testable from the
/// cross-platform <c>PlanMeter.Core.Tests</c> project. The security/logging layer must
/// not live inside the UI assembly (see CLAUDE.md "keep provider/security logic out of
/// the WPF layer").
/// </remarks>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent is null)
        {
            return;
        }

        // Walk the properties collection; redact any scalar string that matches a token shape.
        var keysToRedact = new List<string>();
        foreach (var property in logEvent.Properties)
        {
            if (property.Value is ScalarValue scalar && scalar.Value is string s)
            {
                string redacted = TokenRedactor.Redact(s);
                if (redacted != s)
                {
                    keysToRedact.Add(property.Key);
                }
            }
        }

        foreach (var key in keysToRedact)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(key, new ScalarValue("[REDACTED]")));
        }
    }
}

/// <summary>
/// SEC-03 sink wrapper. Rebuilds the <see cref="LogEvent"/> with a redacted exception
/// tree before forwarding to <paramref name="_inner"/>. Load-bearing because the output
/// template's <c>{Exception}</c> formatter calls <see cref="Exception.ToString"/>, which
/// emits the outer message, the stack trace, AND every inner exception's
/// <see cref="Exception.Message"/> — none of which is touched by the enricher (an
/// <see cref="ILogEventEnricher"/> cannot rewrite the exception object — it can only
/// touch properties). Without this wrapper, any unhandled exception whose message or
/// inner chain carries a token-shape string (e.g. a rewrapped
/// <see cref="System.Net.Http.HttpRequestException"/> whose inner message echoes a URL
/// or response body) leaks that token straight to the rolling log file.
/// </summary>
/// <remarks>
/// Defence in depth, not load-bearing on its own: <see cref="RedactingHandler"/> wraps
/// HTTP exceptions with a redacted outer message at the DelegatingHandler layer, and
/// <see cref="RedactingEnricher"/> strips token-shape from scalar properties. This sink
/// is the LAST line of defence before the file writer sees the rendered exception text.
/// </remarks>
public sealed class RedactingSink : ILogEventSink
{
    private readonly ILogEventSink _inner;

    public RedactingSink(ILogEventSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null)
        {
            _inner.Emit(logEvent!);
            return;
        }

        if (logEvent.Exception is null)
        {
            _inner.Emit(logEvent);
            return;
        }

        // Rebuild the LogEvent with a redacted exception tree. Serilog's LogEvent is
        // immutable; the only mutation path is constructing a new one. We preserve the
        // timestamp, level, message template, and properties verbatim — only the
        // Exception reference is swapped for its redacted counterpart.
        var redactedException = RedactException(logEvent.Exception);
        var rebuilt = new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            redactedException,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(kv => new LogEventProperty(kv.Key, kv.Value)).ToList());

        _inner.Emit(rebuilt);
    }

    /// <summary>
    /// Walk the exception tree and replace each <see cref="Exception.Message"/> with its
    /// redacted form, preserving the inner chain shape. The exception type is preserved
    /// only as a synthetic outer <see cref="Exception"/> — the original stack trace is
    /// intentionally dropped because <see cref="Exception.StackTrace"/> cannot be set on
    /// a re-thrown exception (and we do not need it for an always-on widget's local log
    /// file; the type name + message is enough to triage). The caller (the Serilog
    /// output template's <c>{Exception}</c> formatter) calls <see cref="Exception.ToString"/>
    /// on the rebuilt exception, which now emits only redacted text.
    /// </summary>
    /// <remarks>
    /// The original exception type is NOT preserved (it cannot be — <see cref="Exception"/>
    /// does not allow setting the Message on an existing instance, and the type-specific
    /// ctors like <see cref="System.Net.Http.HttpRequestException(string, Exception?, System.Net.HttpStatusCode?)"/>
    /// are not uniformly available across exception types). We deliberately choose the
    /// base <see cref="Exception"/> to keep this method O(1) and type-agnostic — a stack
    /// trace is unnecessary for a personal always-on widget's local rolling log.
    /// </remarks>
    private static Exception RedactException(Exception ex)
    {
        if (ex is null)
        {
            throw new ArgumentNullException(nameof(ex));
        }

        string redactedMessage = TokenRedactor.Redact(ex.Message);
        if (ex.InnerException is null)
        {
            return new Exception(redactedMessage);
        }

        return new Exception(redactedMessage, RedactException(ex.InnerException));
    }
}
