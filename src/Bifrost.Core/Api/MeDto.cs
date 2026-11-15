using Bifrost.Core.Models;

namespace Bifrost.Core.Api;

public sealed record MeDto(Guid PersonId, string PersonName, PersonRole Role, Guid DeviceId, string DeviceName, DeviceKind Kind);
