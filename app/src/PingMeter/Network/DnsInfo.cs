using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace PingMeter.Network;

/// <summary>How Windows encrypts queries to a given DNS server.</summary>
internal enum DohMode
{
    Off,
    Automatic,
    Manual,
}

/// <summary>The DNS-over-HTTPS setting for one server on one adapter.</summary>
internal sealed record DohSetting(DohMode Mode, string? Template);

/// <param name="Ipv6InterfaceIndex">Null when IPv6 is turned off for the adapter.</param>
internal sealed record DnsStatus(
    string AdapterName,
    string AdapterId,
    int InterfaceIndex,
    IReadOnlyList<string> Servers,
    bool IsManual,
    int? Ipv6InterfaceIndex,
    IReadOnlyList<string> Ipv6Servers,
    bool Ipv6IsManual)
{
    /// <summary>One-line form for the widget tooltip; IPv6 only when it has servers.</summary>
    public string Summary =>
        (Servers.Count == 0
            ? "DNS: unknown"
            : $"DNS: {string.Join(", ", Servers)} ({(IsManual ? "manual" : "automatic")})")
        + (Ipv6Servers.Count == 0
            ? ""
            : $"\nIPv6 DNS: {string.Join(", ", Ipv6Servers)} ({(Ipv6IsManual ? "manual" : "automatic")})");
}

/// <summary>Reads the DNS configuration of the adapter carrying internet traffic. No elevation needed.</summary>
internal static class DnsInfo
{
    /// <summary>
    /// Per-adapter DoH settings live under this key, one subkey per server address:
    /// "Doh" for IPv4 servers, "Doh6" for IPv6 ones.
    /// </summary>
    internal static string DohKeyPath(string adapterId, string server) =>
        $@"{DohFamilyKeyPath(adapterId, server.Contains(':'))}\{server}";

    internal static string DohFamilyKeyPath(string adapterId, bool ipv6) =>
        $@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{adapterId}\DohInterfaceSettings\{(ipv6 ? "Doh6" : "Doh")}";

    /// <summary>The active adapter's DNS status, or null when offline / nothing found.</summary>
    public static DnsStatus? GetActive()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                // "Active" = has an IPv4 default gateway (i.e. actually routes to the internet).
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    continue;

                int index;
                try
                {
                    index = props.GetIPv4Properties().Index;
                }
                catch
                {
                    continue;
                }

                var servers = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();

                int? v6Index = null;
                try
                {
                    v6Index = props.GetIPv6Properties().Index;
                }
                catch
                {
                    // IPv6 is unbound from this adapter
                }

                // fec0:0:0:ffff::1-3 are Windows' placeholders when no IPv6 DNS is known. A router
                // often appears twice as "fe80::1%10", so drop the zone and the repeats.
                var v6Servers = v6Index is null
                    ? []
                    : props.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal)
                        .Select(a => a.ToString().Split('%')[0])
                        .Distinct()
                        .ToList();

                return new DnsStatus(nic.Name, nic.Id, index, servers, IsManualDns(nic.Id, ipv6: false),
                    v6Index, v6Servers, v6Index != null && IsManualDns(nic.Id, ipv6: true));
            }
        }
        catch
        {
            // adapter enumeration hiccup — report unknown
        }
        return null;
    }

    /// <summary>Current DNS-over-HTTPS setting for one server (reading needs no elevation).</summary>
    public static DohSetting GetDoh(string adapterId, string server)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DohKeyPath(adapterId, server));
            if (key is null)
                return new DohSetting(DohMode.Off, null);
            // Windows writes DohFlags 1 for its built-in template, 2 alongside a custom one.
            long flags = Convert.ToInt64(key.GetValue("DohFlags", 0L));
            string? template = key.GetValue("DohTemplate") as string;
            return flags switch
            {
                1 => new DohSetting(DohMode.Automatic, template),
                2 => new DohSetting(DohMode.Manual, template),
                _ => new DohSetting(DohMode.Off, template),
            };
        }
        catch
        {
            return new DohSetting(DohMode.Off, null);
        }
    }

    /// <summary>A non-empty static NameServer value means DNS was set manually (not DHCP-supplied).</summary>
    private static bool IsManualDns(string adapterId, bool ipv6)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{(ipv6 ? "Tcpip6" : "Tcpip")}\Parameters\Interfaces\{adapterId}");
            return !string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch
        {
            return false;
        }
    }
}
