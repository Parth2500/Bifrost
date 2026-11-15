namespace Bifrost.Server.Hosting;

/// <param name="Port">First loopback port to try; the next 19 are tried when it is busy. 0 means any free port (tests).</param>
public sealed record BifrostHostOptions(int Port = BifrostHostOptions.DefaultPort)
{
    /// <summary>Sleipnir alone uses 7766, so the two can run on one machine.</summary>
    public const int DefaultPort = 7780;
}
