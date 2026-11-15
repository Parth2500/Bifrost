using Bifrost.Core.Models;

namespace Bifrost.Server.Persistence;

public class PersonEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public PersonRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public DeviceKind Kind { get; set; }

    /// <summary>Hex SHA-256 of the device token (D3). The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>The built-in "This computer" device, whose token is the local admin token file.</summary>
    public bool IsLocal { get; set; }
}

/// <summary>
/// One audited event (design §11). No foreign keys: a record must outlive the person or device it names.
/// </summary>
public class AuditEntity
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Kind { get; set; } = "";
    public Guid? ActorPersonId { get; set; }
    public Guid? ActorDeviceId { get; set; }
    public Guid? SubjectId { get; set; }
    public string? Detail { get; set; }
}
