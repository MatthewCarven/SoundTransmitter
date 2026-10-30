using System.Net;
using System.Net.Sockets;

namespace SoundTransmitter.Core;

public static class Net
{
    public const int ReceiverPort = 47800;
    public const int SenderPort = 47801;

    /// A UDP socket bound to <paramref name="localPort"/> on every interface.
    public static Socket OpenUdp(int localPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows turns an ICMP "port unreachable" (the other end isn't running
        // yet) into a ConnectionReset on the next receive. Switch that off.
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"UDP port {localPort} is already in use: is another copy of this program already running?", e);
        }
        return socket;
    }

    /// "host", "host:port" or "a.b.c.d:port" to an IPv4 endpoint.
    public static IPEndPoint Resolve(string hostPort, int defaultPort)
    {
        string host = hostPort;
        int port = defaultPort;
        int colon = hostPort.LastIndexOf(':');
        if (colon > 0 && int.TryParse(hostPort[(colon + 1)..], out int p))
        {
            host = hostPort[..colon];
            port = p;
        }
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        var address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"{host} has no IPv4 address");
        return new IPEndPoint(address, port);
    }
}
