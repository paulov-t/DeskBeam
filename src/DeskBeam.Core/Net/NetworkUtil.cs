using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskBeam.Core.Net;

public static class NetworkUtil
{
    /// <summary>Returns the IPv4 address that the default route would use for internet traffic.</summary>
    public static IPAddress? GetPrimaryIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 65530));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static IReadOnlyList<IPAddress> GetLocalIPv4()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(info.Address))
                        result.Add(info.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // ignore
        }

        return result;
    }

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                bytes[0] == 169 && bytes[1] == 254);
    }

    public static string FormatEndpoint(string host, int port) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? $"{host}:{port}"
            : $"{host}:{port}";
}
