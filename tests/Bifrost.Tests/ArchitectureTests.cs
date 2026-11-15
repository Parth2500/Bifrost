using System.Reflection;
using Bifrost.Core.Models;

namespace Bifrost.Tests;

/// <summary>Pins the layering: Core stays reusable (the client app shares it), and Bifröst shares no code with Sleipnir.</summary>
public class ArchitectureTests
{
    private static readonly string[] OwnAssemblies = ["Bifrost.Core", "Bifrost.Server", "bifrost"];

    [Fact]
    public void Core_references_only_the_framework()
    {
        var core = typeof(PersonRole).Assembly;

        Assert.Equal("Bifrost.Core", core.GetName().Name);
        Assert.All(core.GetReferencedAssemblies(), r =>
            Assert.True(r.Name is "netstandard" or "mscorlib" || r.Name!.StartsWith("System.", StringComparison.Ordinal),
                $"Core must not reference {r.Name}"));
    }

    [Fact]
    public void Nothing_references_Sleipnir()
    {
        foreach (var name in OwnAssemblies)
        {
            var assembly = Assembly.Load(new AssemblyName(name));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                r => r.Name!.StartsWith("Sleipnir", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Device_kind_has_exactly_the_two_kinds_of_the_design()
    {
        Assert.Equal(["App", "BrowserFallback"], Enum.GetNames<DeviceKind>());
    }
}
