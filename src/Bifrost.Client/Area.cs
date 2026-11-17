using System.Text.RegularExpressions;

namespace Bifrost.Client;

/// <summary>
/// Which part of the home a web view may reach: a folder, and any further folders its pages call. The token
/// is sent to nothing outside it.
/// </summary>
/// <param name="Root">The folder a page lives in, from the home's root: <c>/x/sleipnir/</c>.</param>
/// <param name="AlsoUnder">Folders its pages may also call: Bifröst's own pages call the API.</param>
public sealed partial record Area(string Root, IReadOnlyList<string> AlsoUnder)
{
    /// <summary>An extension's screens, and nothing else.</summary>
    /// <exception cref="ArgumentException">Not a lowercase name of letters, digits and dashes.</exception>
    public static Area Extension(string name)
    {
        if (name is null || !ValidName().IsMatch(name)) throw new ArgumentException("An extension's name is lowercase letters, digits and dashes.", nameof(name));
        return new Area($"/x/{name}/", []);
    }

    /// <summary>Bifröst's own pages (who is connected, devices, extensions) and the API they call.</summary>
    public static Area HomePages { get; } = new("/ui/", ["/api/v1/"]);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ValidName();
}
