using Bifrost.Core.Models;
using Bifrost.Server.Persistence;

namespace Bifrost.Server.People;

/// <summary>Who is calling: the person, their role, and the device whose token was presented.</summary>
public sealed record Principal(
    Guid PersonId, string PersonName, PersonRole Role, Guid DeviceId, string DeviceName, DeviceKind Kind, bool IsLocal)
{
    public bool IsAdmin => Role == PersonRole.Admin;
}

public sealed record IssuedDevice(DeviceEntity Device, string Token);

/// <summary>A refused operation; <see cref="Code"/> is the stable API error code.</summary>
public sealed class PeopleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
