using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Networking;

/// <summary>
/// Configuration-driven outbound HTTP proxy used for metadata scraping, image downloads and
/// plugin repository access. Reads values captured at client construction time;
/// requires a server restart to pick up changes.
/// </summary>
public sealed class ConfigurationProxy : IWebProxy
{
    private readonly Uri? _proxyUri;
    private readonly string[] _noProxyEntries;

    /// <summary>
    /// Gets or sets the credentials to submit to the proxy server for authentication. Not used by this implementation.
    /// </summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationProxy"/> class.
    /// </summary>
    /// <param name="proxyUri">The outbound proxy URI, e.g. http://host:port. Empty or null disables the proxy.</param>
    /// <param name="noProxyEntries">Hosts, IP addresses, CIDR subnets or wildcard domains that bypass the proxy.</param>
    public ConfigurationProxy(string? proxyUri, IEnumerable<string> noProxyEntries)
    {
        if (!string.IsNullOrWhiteSpace(proxyUri)
            && (Uri.TryCreate(proxyUri.Trim(), UriKind.Absolute, out var parsed)
                || Uri.TryCreate("http://" + proxyUri.Trim(), UriKind.Absolute, out parsed)))
        {
            _proxyUri = parsed;
        }

        _noProxyEntries = noProxyEntries
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().TrimStart('!'))
            .Where(e => e.Length > 0)
            .ToArray();
    }

    /// <inheritdoc />
    public Uri? GetProxy(Uri destination)
    {
        if (_proxyUri is null || IsBypassed(destination))
        {
            return null;
        }

        return _proxyUri;
    }

    /// <inheritdoc />
    public bool IsBypassed(Uri host)
    {
        if (_proxyUri is null)
        {
            return true;
        }

        if (host.IsLoopback)
        {
            return true;
        }

        if (host.HostNameType == UriHostNameType.IPv4 || host.HostNameType == UriHostNameType.IPv6)
        {
            if (IPAddress.TryParse(host.Host, out var address))
            {
                return MatchesEntry(address);
            }

            return false;
        }

        var hostname = host.Host;
        return _noProxyEntries.Any(entry => MatchesHostEntry(hostname, entry));
    }

    private bool MatchesEntry(IPAddress address)
    {
        foreach (var entry in _noProxyEntries)
        {
            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            if (slash != -1)
            {
                // CIDR subnet: prefix length is everything after the slash.
                if (IPAddress.TryParse(entry[..slash], out var network)
                    && byte.TryParse(entry[(slash + 1)..], out var prefix))
                {
                    var family = network.AddressFamily == AddressFamily.InterNetwork
                        ? AddressFamily.InterNetwork
                        : AddressFamily.InterNetworkV6;
                    if (network.AddressFamily != address.AddressFamily)
                    {
                        continue;
                    }

                    var maxPrefix = family == AddressFamily.InterNetwork ? 32 : 128;
                    if (prefix > maxPrefix)
                    {
                        continue;
                    }

                    if (new IPNetwork(network, prefix).Contains(address))
                    {
                        return true;
                    }
                }
            }
            else if (IPAddress.TryParse(entry, out var entryAddress))
            {
                if (entryAddress.Equals(address))
                {
                    return true;
                }
            }
            else
            {
                // Non-IP entry can still match a numeric destination via DNS later;
                // SocketsHttpHandler resolves DNS before consulting the proxy, but for
                // hostname destinations this is handled in MatchesHostEntry.
                if (MatchesHostEntry(entry, entry))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesHostEntry(string hostname, string entry)
    {
        if (string.Equals(hostname, entry, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Wildcard domain: *.example.com matches example.com and any subdomain.
        if (entry.StartsWith('*'))
        {
            var suffix = entry[1..]; // ".example.com"
            if (hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && hostname.Length > suffix.Length)
            {
                return true;
            }

            if (string.Equals(hostname, entry[2..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        // Suffix domain: ".example.com" matches example.com and any subdomain.
        if (entry.StartsWith('.'))
        {
            return hostname.EndsWith(entry, StringComparison.OrdinalIgnoreCase)
                && hostname.Length > entry.Length;
        }

        return false;
    }
}
