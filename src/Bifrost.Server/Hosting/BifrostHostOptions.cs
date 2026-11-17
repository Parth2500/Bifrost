namespace Bifrost.Server.Hosting;

/// <param name="Port">First loopback port to try; the next 19 are tried when it is busy. 0 means any free port (tests).</param>
/// <param name="Admin">The admin socket; none when null.</param>
/// <param name="RotateHomeKey">Makes a new home key on disk and returns its fingerprint; takes effect at the next start.</param>
/// <param name="Lan">The home-network HTTPS listener; none (off) when null.</param>
public sealed record BifrostHostOptions(int Port = BifrostHostOptions.DefaultPort, AdminSocketOptions? Admin = null, LanOptions? Lan = null, Func<string>? RotateHomeKey = null)
{
    /// <summary>Sleipnir alone uses 7766, so the two can run on one machine.</summary>
    public const int DefaultPort = 7780;
}

/// <param name="Path">The socket file. Its folder is made <c>0755</c> and the socket <c>0666</c>: the peer check is the gate.</param>
/// <param name="Uid">The only operating-system account accepted on it.</param>
public sealed record AdminSocketOptions(string Path, uint Uid);

/// <param name="Certificate">The home key's certificate.</param>
/// <param name="HomeName">What the home is called in links and on the network; the machine name when null.</param>
/// <param name="FallbackPort">The plain-HTTP, view-only browser fallback (design §6.1); off when null.</param>
/// <param name="Port">Fixed, so a pinned client can reconnect (design §6.1).</param>
public sealed record LanOptions(System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate, int Port = LanOptions.DefaultPort, int? FallbackPort = null, string? HomeName = null)
{
    public const int DefaultPort = 7781;

    public string Name => string.IsNullOrWhiteSpace(HomeName) ? Environment.MachineName : HomeName;
}
