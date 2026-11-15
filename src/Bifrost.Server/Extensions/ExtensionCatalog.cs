namespace Bifrost.Server.Extensions;

/// <summary>The extensions Bifröst knows (design §8.1). Only the owner's repositories; nothing else starts.</summary>
public static class ExtensionCatalog
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) { "sleipnir", "saga", "files", "norns" };
}
