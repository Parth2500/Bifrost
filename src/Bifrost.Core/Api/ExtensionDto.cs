namespace Bifrost.Core.Api;

/// <param name="State"><c>stopped</c>, <c>starting</c>, <c>running</c>, <c>crashed</c> (a restart is scheduled) or <c>failed</c>.</param>
public sealed record ExtensionDto(string Name, string State, int? Pid, int Restarts);
