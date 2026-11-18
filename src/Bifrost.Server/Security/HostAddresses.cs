using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bifrost.Server.Security;

/// <summary>
/// This machine's own addresses, found without listing network interfaces. The service's sandbox allows only Unix and
/// IP sockets, and listing interfaces needs a netlink socket, which it refuses (it fails with "Address family not
/// supported by protocol"), so nothing here may depend on it.
/// </summary>
public static class HostAddresses
{
    /// <summary>
    /// The address other devices reach this machine on: the source address the system would use to reach the outside.
    /// A UDP socket "connected" to a documentation address (RFC 5737) only asks the routing table; no packet is sent.
    /// </summary>
    /// <returns>Null when the machine has no route out, as when it is not on a network.</returns>
    public static string? Lan()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            if (probe.LocalEndPoint is IPEndPoint { Address: var address } && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)) return address.ToString();
        }
        catch (SocketException)
        {
            // no route out: try the interface list, which works outside the service's sandbox
        }
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null; // the sandbox: say so, rather than guess
        }
    }

    /// <summary>
    /// Is this <c>Host</c> one of the machine's own addresses (design §6.4)? The address the connection arrived on is
    /// the machine's own by definition, so that needs no lookup; any other address is looked for in the interface list
    /// where the sandbox allows one, and is refused where it does not.
    /// </summary>
    public static bool IsOwn(IPAddress candidate, IPAddress? connectedTo)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IPAddress.IsLoopback(candidate)) return true;
        if (connectedTo is not null && Normal(connectedTo).Equals(Normal(candidate))) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => Normal(a.Address).Equals(Normal(candidate)));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static IPAddress Normal(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
