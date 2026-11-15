using Bifrost.Server.People;
using CsCheck;

namespace Bifrost.Tests.Server;

public class PersonNamesTests
{
    private static bool Acceptable(string trimmed) =>
        trimmed.Length is >= 1 and <= PersonNames.MaxLength && !trimmed.Any(c => char.IsControl(c) || c is '/' or '\\');

    [Fact]
    public void Validate_accepts_exactly_trimmed_names_of_1_to_40_without_slashes_or_controls()
    {
        Gen.OneOf(Gen.String, Gen.String[Gen.Char["ab /\\\t\0é"], 0, 45]).Sample(s =>
        {
            try
            {
                var result = PersonNames.Validate(s);
                return result == s.Trim() && Acceptable(result);
            }
            catch (PeopleException e) when (e.Code == "invalid_name")
            {
                return !Acceptable(s.Trim());
            }
        });
    }

    [Fact]
    public void Null_is_an_invalid_name()
    {
        Assert.Equal("invalid_name", Assert.Throws<PeopleException>(() => PersonNames.Validate(null)).Code);
    }
}
