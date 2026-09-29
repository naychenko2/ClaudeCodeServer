using ClaudeHomeServer.HandsBridge.Browser.Dom;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>Текст поддерева из разметки на нашей стороне (режим text у browser_query).</summary>
public class HtmlTextTests
{
    [Theory]
    [InlineData("<p>One</p><p>Two</p>", "One\nTwo")]
    [InlineData("<div>a<br>b<br/>c</div>", "a\nb\nc")]
    [InlineData("<span>inline</span> <b>words</b>", "inline words")]
    [InlineData("<ul><li>x</li><li>y</li></ul>", "x\ny")]
    [InlineData("<table><tr><th>Name</th><th>Price</th></tr><tr><td>Tea</td><td>5</td></tr></table>", "Name\tPrice\nTea\t5")]
    [InlineData("Fish &amp; chips&nbsp;&lt;3 &#x41;", "Fish & chips <3 A")]
    [InlineData("a   \n\n   b", "a\nb")]
    [InlineData("", "")]
    public void Html_becomes_readable_text(string html, string expected)
    {
        Assert.Equal(expected, HtmlText.ToText(html));
    }

    [Theory]
    [InlineData("<p>ok</p><script>var s = '<p>no</p>';</script>")]
    [InlineData("<p>ok</p><STYLE type=\"text/css\">p { color: red }</STYLE>")]
    [InlineData("<p>ok</p><!-- <p>no</p> -->")]
    [InlineData("<p>ok</p><noscript><p>no</p></noscript>")]
    [InlineData("<p>ok</p><template><p>no</p></template>")]
    [InlineData("<p>ok</p><svg><text>no</text></svg>")]
    public void Scripts_styles_comments_and_templates_are_not_text(string html)
    {
        Assert.Equal("ok", HtmlText.ToText(html));
    }

    [Fact]
    public void Huge_or_hostile_markup_does_not_hang()
    {
        var hostile = string.Concat(Enumerable.Repeat("<script <!-- <p ", 50_000));
        var huge = string.Concat(Enumerable.Repeat("<p>x</p>", 400_000));

        var started = DateTime.UtcNow;
        HtmlText.ToText(hostile);
        var text = HtmlText.ToText(huge);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.True(text.Length <= HtmlText.MaxHtmlChars);
    }
}
