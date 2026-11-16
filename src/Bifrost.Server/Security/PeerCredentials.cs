using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Bifrost.Server.Security;

/// <summary>
/// Who is on the other end of a Unix socket, as the kernel says (<c>SO_PEERCRED</c>). The caller
/// cannot forge it, which is why the admin socket needs no token (design §5).
/// </summary>
public static class PeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>The peer's process, account and group; null when the socket cannot say.</summary>
    public static (int Pid, uint Uid, uint Gid)? Of(Socket? socket)
    {
        if (socket is null) return null;
        Span<byte> ucred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        try
        {
            if (socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred) != ucred.Length) return null;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return null;
        }
        return (BinaryPrimitives.ReadInt32LittleEndian(ucred),
            BinaryPrimitives.ReadUInt32LittleEndian(ucred[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(ucred[8..]));
    }

    /// <summary>The account this process runs as.</summary>
    public static uint CurrentUid() => NativeMethods.GetEffectiveUid();

    private static class NativeMethods
    {
        // No arguments and a plain integer back, so no marshalling.
        [DllImport("libc", EntryPoint = "geteuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern uint GetEffectiveUid();
    }
}
