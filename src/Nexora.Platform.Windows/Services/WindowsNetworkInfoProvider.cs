using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Nexora.Core.Network;
using Nexora.Platform.Windows.Interop;

namespace Nexora.Platform.Windows.Services;

public sealed class WindowsNetworkInfoProvider(ILogger<WindowsNetworkInfoProvider> logger) : INetworkInfoProvider
{
    public NetworkAdapterInfo? GetActiveAdapter()
    {
        var bestIndex = GetBestInterfaceIndex();
        NetworkInterface? chosen = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            int? index = null;
            try
            {
                index = props.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
                // IPv4 not bound.
            }

            if (bestIndex is not null && index == bestIndex)
            {
                chosen = nic;
                break;
            }

            if (chosen is null && props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            {
                chosen = nic;
            }
        }

        if (chosen is null)
        {
            return null;
        }

        var ip = chosen.GetIPProperties();
        var gateway = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                      ?? ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => !a.Equals(IPAddress.IPv6Any));
        var kind = chosen.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Cellular,
            _ => AdapterKind.Other,
        };

        (int? Signal, double? Rate, string? Standard) wifi = (null, null, null);
        if (kind == AdapterKind.WiFi)
        {
            try
            {
                wifi = WlanInterop.QueryCurrentConnection();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                logger.LogDebug(ex, "Wi-Fi details unavailable");
            }
        }

        long? speed = chosen.Speed > 0 ? chosen.Speed : null;
        return new NetworkAdapterInfo(
            chosen.Name,
            chosen.Description,
            kind,
            speed,
            chosen.OperationalStatus == OperationalStatus.Up,
            gateway,
            ip.DnsAddresses.Where(d => !d.IsIPv6SiteLocal || d.AddressFamily == AddressFamily.InterNetwork).ToList(),
            ip.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork),
            ip.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal),
            wifi.Signal,
            wifi.Rate,
            wifi.Standard);
    }

    private static int? GetBestInterfaceIndex()
    {
        var address = new NativeMethods.SOCKADDR_IN
        {
            sin_family = (short)AddressFamily.InterNetwork,
            sin_addr = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0),
        };
        return NativeMethods.GetBestInterfaceEx(ref address, out var index) == 0 ? (int)index : null;
    }
}

/// <summary>Reads signal quality and link rate of the connected Wi-Fi network (no SSID/BSSID is read).</summary>
internal static unsafe class WlanInterop
{
    private const int wlan_intf_opcode_current_connection = 7;

    public static (int? Signal, double? RateMbps, string? Standard) QueryCurrentConnection()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0)
        {
            return (null, null, null);
        }

        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero)
            {
                return (null, null, null);
            }

            try
            {
                // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then WLAN_INTERFACE_INFO[] (532 bytes each: GUID + WCHAR[256] + state).
                var count = *(uint*)list;
                for (var i = 0; i < count; i++)
                {
                    var info = (byte*)list + 8 + (i * 532);
                    var guid = *(Guid*)info;
                    var state = *(int*)(info + 16 + 512);
                    if (state != 1)
                    {
                        continue; // not connected
                    }

                    if (WlanQueryInterface(client, ref guid, wlan_intf_opcode_current_connection, IntPtr.Zero, out _, out var data, out _) != 0 || data == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile WCHAR[256](512) then association attributes at 520.
                        var assoc = (byte*)data + 520;
                        var phy = *(int*)(assoc + 48);
                        var signal = (int)*(uint*)(assoc + 56);
                        var rx = *(uint*)(assoc + 60);
                        return (Math.Clamp(signal, 0, 100), rx > 0 ? rx / 1000.0 : null, PhyName(phy));
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            _ = WlanCloseHandle(client, IntPtr.Zero);
        }

        return (null, null, null);
    }

    private static string? PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => null,
    };

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out uint dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
