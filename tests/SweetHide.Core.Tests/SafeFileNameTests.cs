namespace SweetHide.Core.Tests;

public class SafeFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("/absolute/path/x.txt", "x.txt")]
    [InlineData("..", "file")]
    [InlineData(".", "file")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("dir/", "file")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("name\u0000with\u001Fcontrols", "name_with_controls")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1.tar.gz", "_LPT1.tar.gz")]
    [InlineData("COM¹", "_COM¹")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("เอกสารลับ.docx", "เอกสารลับ.docx")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Fact]
    public void Truncates_long_names_but_keeps_the_extension()
    {
        var result = SafeFileName.Sanitize(new string('a', 500) + ".jpeg");
        Assert.Equal(200, result.Length);
        Assert.EndsWith(".jpeg", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_splits_a_surrogate_pair_when_truncating()
    {
        var result = SafeFileName.Sanitize(new string('a', 199) + "😀😀😀");
        Assert.False(char.IsHighSurrogate(result[^1]));
    }
}
