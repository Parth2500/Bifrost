using System.Globalization;
using System.Text;

namespace Bifrost.Server.People;

public static class PersonNames
{
    public const int MaxLength = 40;

    /// <summary>
    /// Returns the trimmed name, or throws <c>invalid_name</c>. Invisible format characters (zero-width
    /// spaces, bidi overrides) are refused, so two names that look the same are the same.
    /// </summary>
    public static string Validate(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxLength || trimmed.Any(c =>
                char.IsControl(c) || c is '/' or '\\' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            throw new PeopleException("invalid_name");
        }
        return trimmed;
    }

    /// <summary>What two names must not share: compatibility-normalised and upper-cased, so "Émile", "émile" and "e\u0301mile" collide.</summary>
    public static string Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }
}
