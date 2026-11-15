namespace Bifrost.Core.Api;

/// <summary>What anyone may learn about this home without a token.</summary>
public sealed record ServerInfoDto(string Name, string Version, string[] ApiVersions);
