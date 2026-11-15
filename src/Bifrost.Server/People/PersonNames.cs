namespace Bifrost.Server.People;

public static class PersonNames
{
    public const int MaxLength = 40;

    /// <summary>Returns the trimmed name, or throws <c>invalid_name</c>.</summary>
    public static string Validate(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxLength || trimmed.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw new PeopleException("invalid_name");
        }
        return trimmed;
    }
}
