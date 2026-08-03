using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace PingMeter.Network;

internal sealed record DnsStatus(string AdapterName, int InterfaceIndex, IReadOnlyList<string> Servers, bool IsManual)
{
    /// <summary>One-line form for the widget tooltip.</summary>
    public string Summary => Servers.Count == 0
        ? "DNS: unknown"
        : $"DNS: {string.Join(", ", Servers)} ({(IsManual ? "manual" : "automatic")})";
}

/// <summary>Reads the IPv4 DNS configuration of the adapter carrying internet traffic. No elevation needed.</summary>
internal static class DnsInfo
{
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

                return new DnsStatus(nic.Name, index, servers, IsManualDns(nic.Id));
            }
        }
        catch
        {
            // adapter enumeration hiccup — report unknown
        }
        return null;
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
