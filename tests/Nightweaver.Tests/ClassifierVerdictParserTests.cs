using System.Text;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class ClassifierVerdictParserTests
{
    [Fact]
    public void AcceptsOnlyOneExactVerdictPropertyWithOptionalWhitespace()
    {
        AssertParsed(" \r\n{\"verdict\":\"duplicate\"}\t", DuplicateFailureVerdict.Duplicate);
        AssertParsed("\n{\"verdict\":\"different\"} ", DuplicateFailureVerdict.Different);

        foreach (var invalid in new[]
        {
            "", "null", "[]", "{}", "{\"verdict\":null}", "{\"verdict\":7}",
            "{\"verdict\":\"Duplicate\"}", "{\"verdict\":\"same\"}",
            "{\"verdict\":\"duplicate\",\"other\":1}",
            "{\"verdict\":\"duplicate\",\"verdict\":\"different\"}",
            "{\"Verdict\":\"duplicate\"}", "{\"verdict\":\"duplicate\"}{\"verdict\":\"different\"}",
            "{\"verdict\":\"duplicate\"} trailing", "```json\n{\"verdict\":\"duplicate\"}\n```",
            "// comment\n{\"verdict\":\"duplicate\"}", "{\"verdict\":\"duplicate\",}"
        })
        {
            Assert.False(ClassifierVerdictParser.TryParse(Encoding.UTF8.GetBytes(invalid), out _), invalid);
        }

        Assert.False(ClassifierVerdictParser.TryParse(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }, out _));
        Assert.False(ClassifierVerdictParser.TryParse(new byte[] { 0x7B, 0x22, 0x76, 0xFF, 0x7D }, out _));
    }

    [Fact]
    public void BoundsUtf8TailsWithoutSplittingUnicodeScalars()
    {
        var source = new string('x', 7) + "🌃";
        var bounded = BoundedUtf8Tail.Create(source, 4);

        Assert.Equal("🌃", bounded.Text);
        Assert.True(bounded.Truncated);
        Assert.Equal(11, bounded.OriginalByteLength);
    }

    private static void AssertParsed(string json, DuplicateFailureVerdict expected)
    {
        Assert.True(ClassifierVerdictParser.TryParse(Encoding.UTF8.GetBytes(json), out var actual), json);
        Assert.Equal(expected, actual);
    }
}
