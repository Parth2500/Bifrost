using Bifrost.Server.Extensions;

namespace Bifrost.App;

/// <summary>
/// <c>bifrost release keygen | pack | sign | verify</c>: for the owner, on their own machine, to publish an extension
/// (design §8.3). Nothing here talks to Bifröst or the network. The private key it makes stays in the folder you name.
/// </summary>
internal static class ReleaseCommand
{
    private const string Usage = """
        Usage: bifrost release keygen <name> [--dir <folder>]
               bifrost release pack <extension> <version> --program <platform>=<file> [--program …] [--emits <event>]… [--wants <event>]… [--folder <name>[:write]]… [--out <file>]
               bifrost release sign <package> --key <file.key>        writes <package>.sig
               bifrost release verify <package> --pub <file.pub>

        Platforms: linux-x64, linux-arm64.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["keygen", var name]:
                    return KeyGen(name, ".");
                case ["keygen", var named, "--dir", var dir]:
                    return KeyGen(named, dir);
                case ["pack", var extension, var version, .. var rest]:
                    return Pack(extension, version, rest);
                case ["sign", var package, "--key", var keyFile]:
                {
                    var signature = ReleaseBuilder.Sign(await File.ReadAllTextAsync(keyFile), package);
                    await File.WriteAllBytesAsync(package + ".sig", signature);
                    Console.WriteLine($"Wrote {package}.sig");
                    return 0;
                }
                case ["verify", var package, "--pub", var pubFile]:
                {
                    var ok = ReleaseBuilder.Verify((await File.ReadAllTextAsync(pubFile)).Trim(), package, await File.ReadAllBytesAsync(package + ".sig"));
                    Console.WriteLine(ok ? "The signature is good." : "The signature does NOT match this package and key.");
                    return ok ? 0 : 1;
                }
                default:
                    await Console.Error.WriteLineAsync(Usage);
                    return 64;
            }
        }
        catch (Exception e) when (e is IOException or ReleaseManifest.InvalidManifestException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 1;
        }
    }

    private static int KeyGen(string name, string folder)
    {
        var pub = ReleaseBuilder.WriteKeyPair(folder, name);
        Console.WriteLine($"Wrote {Path.Combine(folder, name + ".key")} (private: keep it offline, make a backup somewhere separate) and {name}.pub.");
        Console.WriteLine("The public key, to carry in ReleaseKeys.BuiltIn:");
        Console.WriteLine(pub);
        return 0;
    }

    private static int Pack(string extension, string version, string[] options)
    {
        var programs = new Dictionary<string, string>(StringComparer.Ordinal);
        var emits = new List<string>();
        var wants = new List<string>();
        var folders = new Dictionary<string, bool>(StringComparer.Ordinal);
        var output = $"{extension}-{version}.bifrost";
        for (var i = 0; i < options.Length; i += 2)
        {
            if (i + 1 >= options.Length)
            {
                Console.Error.WriteLine(Usage);
                return 64;
            }
            var value = options[i + 1];
            switch (options[i])
            {
                case "--program" when value.Split('=', 2) is [var platform, var file]:
                    programs[platform] = file;
                    break;
                case "--emits": emits.Add(value); break;
                case "--wants": wants.Add(value); break;
                case "--folder":
                    folders[value.Split(':')[0]] = value.EndsWith(":write", StringComparison.Ordinal);
                    break;
                case "--out": output = value; break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 64;
            }
        }
        ReleaseBuilder.Pack(output, extension, version, programs, emits, wants, folders);
        Console.WriteLine($"Wrote {output}. Next: bifrost release sign {output} --key <file.key>");
        return 0;
    }
}
