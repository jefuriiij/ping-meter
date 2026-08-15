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

internal sealed record DnsStatus(
    string AdapterName,
    string AdapterId,
    int InterfaceIndex,
    IReadOnlyList<string> Servers,
    bool IsManual)
{
    /// <summary>One-line form for the widget tooltip.</summary>
    public string Summary => Servers.Count == 0
        ? "DNS: unknown"
        : $"DNS: {string.Join(", ", Servers)} ({(IsManual ? "manual" : "automatic")})";
}

/// <summary>Reads the IPv4 DNS configuration of the adapter carrying internet traffic. No elevation needed.</summary>
internal static class DnsInfo
{
    /// <summary>Per-adapter DoH settings live under this key, one subkey per server address.</summary>
    internal static string DohKeyPath(string adapterId, string server) =>
        $@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{adapterId}\DohInterfaceSettings\Doh\{server}";

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

                return new DnsStatus(nic.Name, nic.Id, index, servers, IsManualDns(nic.Id));
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
    private static bool IsManualDns(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            return !string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch
        {
            return false;
        }
    }
}
