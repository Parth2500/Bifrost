using Bifrost.Core.Models;

namespace Bifrost.Core.Api;

/// <summary>What an extension may learn about a person: enough to show a name.</summary>
public sealed record ExtensionPersonDto(Guid Id, string DisplayName, PersonRole Role);

/// <summary>What an extension may learn about a device. Never its token or hash.</summary>
public sealed record ExtensionDeviceDto(Guid Id, Guid PersonId, string Name, DeviceKind Kind);
