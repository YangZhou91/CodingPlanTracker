using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PlanMeter.Core.Http;

/// <summary>
/// Live <see cref="IWebProxy"/> singleton the proxied named clients (Codex / Grok /
/// OpenCode) pin as <c>SocketsHttpHandler.Proxy</c>. Settings mutates
/// <see cref="Enabled"/> + <see cref="Address"/> on this instance so the next CONNECT
/// uses the new hop without rebuilding <c>HttpClient</c>. Default is Clash mixed-port
/// <c>127.0.0.1:7897</c>. HTTP CONNECT only — no SOCKS, no proxy credentials.
///
/// Z.ai and MiniMax named clients stay <c>UseProxy=false</c> and do not reference this
/// object (Clash hung <c>api.z.ai</c>).
/// </summary>
public sealed class ProxySource : IWebProxy
{
    /// <summary>Clash mixed-port default. Display form (host:port), no scheme.</summary>
    public const string DefaultAddress = "127.0.0.1:7897";

    private static readonly Uri DefaultProxyUri = new("http://127.0.0.1:7897/");

    private readonly object _gate = new();
    private bool _enabled = true;
    private string _address = DefaultAddress;
    private Uri _proxyUri = DefaultProxyUri;

    /// <summary>When false, <see cref="IsBypassed"/> is true and SocketsHttpHandler skips the hop.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>Display form <c>host:port</c> (never a URI, never userinfo).</summary>
    public string Address
    {
        get { lock (_gate) return _address; }
    }

    /// <inheritdoc />
    /// <remarks>Always null — PlanMeter does not store proxy credentials.</remarks>
    public ICredentials? Credentials { get; set; }

    /// <inheritdoc />
    public Uri? GetProxy(Uri destination)
    {
        lock (_gate)
        {
            return _proxyUri;
        }
    }

    /// <inheritdoc />
    public bool IsBypassed(Uri host)
    {
        lock (_gate)
        {
            return !_enabled;
        }
    }

    /// <summary>
    /// Startup seed. Never throws. A null <paramref name="useProxy"/>, a null/whitespace
    /// address, or an invalid address degrades to enabled + <see cref="DefaultAddress"/>
    /// (zero migration for old config.json files).
    /// </summary>
    public void Seed(bool? useProxy, string? address)
    {
        lock (_gate)
        {
            if (useProxy is null || !TryParse(address, out string display, out Uri uri))
            {
                _enabled = true;
                _address = DefaultAddress;
                _proxyUri = DefaultProxyUri;
                return;
            }

            _enabled = useProxy.Value;
            _address = display;
            _proxyUri = uri;
        }
    }

    /// <summary>
    /// Live-apply from Settings. Parses when <paramref name="enabled"/> is true or
    /// <paramref name="address"/> is non-empty; throws <see cref="ArgumentException"/>
    /// on invalid input and leaves prior state untouched. Disabled + empty address
    /// keeps the last valid address (the Settings box stays populated while dimmed).
    /// </summary>
    public void Set(bool enabled, string address)
    {
        if (enabled || !string.IsNullOrWhiteSpace(address))
        {
            if (!TryParse(address, out string display, out Uri uri))
            {
                throw new ArgumentException(
                    "Proxy address must be host:port or http://host:port with a port in 1–65535 (IPv4/hostname only).",
                    nameof(address));
            }

            lock (_gate)
            {
                _enabled = enabled;
                _address = display;
                _proxyUri = uri;
            }

            return;
        }

        lock (_gate)
        {
            _enabled = false;
        }
    }

    /// <summary>
    /// Accepts <c>host:port</c> and <c>http://host:port</c>. Rejects missing port, port
    /// 0 or greater than 65535, empty host, IPv6 (ConnectCallback is IPv4-only),
    /// non-http schemes, and userinfo.
    /// On success <paramref name="display"/> is <c>host:port</c> and
    /// <paramref name="proxyUri"/> is <c>http://host:port/</c>.
    /// </summary>
    public static bool TryParse(string? address, out string display, out Uri proxyUri)
    {
        display = DefaultAddress;
        proxyUri = DefaultProxyUri;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string trimmed = address.Trim();
        string hostPort;

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed))
            {
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                return false;
            }

            int schemeSep = trimmed.IndexOf("://", StringComparison.Ordinal);
            string rest = trimmed[(schemeSep + 3)..];
            int cut = rest.IndexOfAny(['/', '?', '#']);
            if (cut >= 0)
            {
                rest = rest[..cut];
            }

            hostPort = rest;
        }
        else
        {
            hostPort = trimmed;
        }

        return TryParseHostPort(hostPort, out display, out proxyUri);
    }

    private static bool TryParseHostPort(string hostPort, out string display, out Uri proxyUri)
    {
        display = DefaultAddress;
        proxyUri = DefaultProxyUri;

        // Bracket IPv6 (http://[::1]:7897 or [::1]:7897) — ConnectCallback is IPv4-only.
        if (hostPort.StartsWith('[') || hostPort.Contains(']'))
        {
            return false;
        }

        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || colon == hostPort.Length - 1)
        {
            return false;
        }

        // A second colon is IPv6 or junk (userinfo already rejected above).
        if (hostPort.IndexOf(':') != colon)
        {
            return false;
        }

        string host = hostPort[..colon];
        string portText = hostPort[(colon + 1)..];
        if (string.IsNullOrWhiteSpace(host)
            || host.Contains('/')
            || host.Contains('@')
            || host.Contains('\\'))
        {
            return false;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip)
            && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return false;
        }

        display = $"{host}:{port}";
        if (!Uri.TryCreate($"http://{host}:{port}/", UriKind.Absolute, out Uri? uri) || uri is null)
        {
            display = DefaultAddress;
            return false;
        }

        proxyUri = uri;
        return true;
    }
}
