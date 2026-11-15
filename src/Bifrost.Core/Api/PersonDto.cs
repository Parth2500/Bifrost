using Bifrost.Core.Models;

namespace Bifrost.Core.Api;

public sealed record PersonDto(Guid Id, string DisplayName, PersonRole Role, DateTime CreatedAt);

public sealed record DeviceDto(
    Guid Id, Guid PersonId, string Name, string Platform, DeviceKind Kind, DateTime CreatedAt, DateTime? LastSeenAt, bool Revoked, bool IsLocal);

public sealed record CreatePersonRequest(string? DisplayName, PersonRole? Role);

public sealed record UpdatePersonRequest(string? DisplayName, PersonRole? Role);
