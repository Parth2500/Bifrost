namespace Bifrost.Tests.Extensions;

/// <summary>Where the fake extension (tests/Bifrost.FakeExtension) was built: beside this test assembly's folder.</summary>
public static class FakeExtension
{
    public static string Program { get; } = Find();

    private static string Find()
    {
        // .../tests/Bifrost.Tests/bin/<Configuration>/net10.0/ → .../tests/Bifrost.FakeExtension/bin/<Configuration>/net10.0/fake-extension
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(here);
        var configuration = Path.GetFileName(Path.GetDirectoryName(here)!);
        var tests = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!)!)!;
        var program = Path.Combine(tests, "Bifrost.FakeExtension", "bin", configuration, tfm, "fake-extension");
        return File.Exists(program) ? program : throw new FileNotFoundException("Build the fake extension first.", program);
    }
}
