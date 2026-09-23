using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace Abacus.Dashboard;

public sealed record DashboardOptions(string Bind, int Port, string Actor, TimeSpan PollInterval)
{
    public bool ActorWasProvided { get; init; }

    public static DashboardOptions Parse(string? bind, string? port, string? actor, string? interval)
    {
        bind ??= "127.0.0.1";
        var actorWasProvided = actor is not null;
        actor ??= "abacus-web";
        var parsedPort = 8080;
        if (port is not null && (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort) || parsedPort is < 1 or > 65535))
            throw new OptionsException("dashboard port must be an integer from 1 through 65535");
        if (Uri.CheckHostName(bind) == UriHostNameType.Unknown || bind.Any(char.IsWhiteSpace) || bind.Contains('%'))
            throw new OptionsException("dashboard bind must be an IP address or hostname, not a URL or wildcard token");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 100 || actor.Any(char.IsControl))
            throw new OptionsException("dashboard actor must contain 1–100 printable characters");
        var duration = TimeSpan.FromSeconds(5);
        if (interval is not null)
        {
            var multiplier = interval.LastOrDefault() switch { 's' => 1, 'm' => 60, 'h' => 3600, _ => 0 };
            if (multiplier == 0 || !double.TryParse(interval[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value * multiplier < 1 || value * multiplier >= TimeSpan.MaxValue.TotalSeconds)
                throw new OptionsException("dashboard poll interval must be a finite positive duration of at least 1s");
            duration = TimeSpan.FromSeconds(value * multiplier);
        }
        return new(bind, parsedPort, actor, duration) { ActorWasProvided = actorWasProvided };
    }

    internal async Task<DashboardBinding> ResolveAsync(CancellationToken token)
    {
        var addresses = IPAddress.TryParse(Bind, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(Bind, token);
        addresses = addresses.Distinct().ToArray();
        if (addresses.Length == 0) throw new OptionsException("dashboard hostname resolved to no addresses");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Bind.Trim('[', ']') };
        foreach (var address in addresses)
        {
            allowed.Add(address.ToString());
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                foreach (var local in NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses))
                    allowed.Add(local.Address.ToString());
                allowed.Add("localhost");
            }
        }
        return new(addresses, allowed, Port);
    }
}

internal sealed record DashboardBinding(IPAddress[] Addresses, HashSet<string> AllowedHosts, int Port)
{
    public bool Allows(string host, int? port) => (port ?? 80) == Port && AllowedHosts.Contains(host.Trim('[', ']'));
    public IEnumerable<string> Urls => Addresses.Select(a => $"http://{(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString())}:{Port}/");
}
