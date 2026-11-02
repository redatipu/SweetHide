using System.Globalization;
using System.Reflection;
using System.Resources;

using SweetHide.Presentation.Services;

namespace SweetHide.Presentation.Tests;

public class StringsTests
{
    private static readonly PropertyInfo[] Properties = typeof(Strings)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(string))
        .ToArray();

    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void Every_string_exists_and_is_not_empty(string culture)
    {
        var previous = Strings.Culture;
        try
        {
            Strings.Culture = CultureInfo.GetCultureInfo(culture);
            var manager = new ResourceManager("SweetHide.Presentation.Resources.Strings", typeof(Strings).Assembly);
            foreach (var property in Properties)
            {
                var value = (string)property.GetValue(null)!;
                Assert.False(string.IsNullOrWhiteSpace(value), property.Name);
                Assert.NotNull(manager.GetString(property.Name, Strings.Culture));
            }
        }
        finally
        {
            Strings.Culture = previous;
        }
    }

    [Fact]
    public void Thai_translation_is_used_for_thai_culture()
    {
        var previous = Strings.Culture;
        try
        {
            Strings.ApplyLanguage(AppLanguage.Thai);
            Assert.Equal("ซ่อน", Strings.NavHide);
            Strings.ApplyLanguage(AppLanguage.English);
            Assert.Equal("Hide", Strings.NavHide);
        }
        finally
        {
            Strings.Culture = previous;
        }
    }

    [Fact]
    public void Format_strings_have_matching_placeholders_in_both_languages()
    {
        var manager = new ResourceManager("SweetHide.Presentation.Resources.Strings", typeof(Strings).Assembly);
        foreach (var property in Properties)
        {
            var en = manager.GetString(property.Name, CultureInfo.InvariantCulture)!;
            var th = manager.GetString(property.Name, CultureInfo.GetCultureInfo("th-TH"))!;
            Assert.True(Placeholders(en).SetEquals(Placeholders(th)), property.Name);
        }
    }

    private static HashSet<string> Placeholders(string s) =>
        [.. System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value)];
}
