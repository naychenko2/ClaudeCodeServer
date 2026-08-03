using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ClaudeHomeServer.Services.Reader;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Reader;

public class HtmlToMarkdownConverterTests
{
    private static string Convert(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var root = (IElement?)doc.Body ?? doc.DocumentElement;
        return HtmlToMarkdownConverter.Convert(root);
    }

    [Fact]
    public void Заголовки_ИАбзацы()
    {
        var md = Convert("<h1>Title</h1><p>Hello <strong>world</strong></p>");
        md.Should().Contain("# Title");
        md.Should().Contain("Hello **world**");
    }

    [Fact]
    public void Список_Ненумерованный()
    {
        var md = Convert("<ul><li>one</li><li>two</li></ul>");
        md.Should().Contain("- one");
        md.Should().Contain("- two");
    }

    [Fact]
    public void Список_Вложенный()
    {
        var md = Convert("<ul><li>a<ul><li>a1</li></ul></li></ul>");
        md.Should().Contain("- a");
        md.Should().Contain("  - a1");
    }

    [Fact]
    public void Таблица_GfmСинтаксис()
    {
        var md = Convert("<table><tr><th>H1</th><th>H2</th></tr><tr><td>c1</td><td>c2</td></tr></table>");
        md.Should().Contain("| H1 | H2 |");
        md.Should().Contain("| --- | --- |");
        md.Should().Contain("| c1 | c2 |");
    }

    [Fact]
    public void КодовыйБлок_СБэктикамиВнутри_ФенсДлиннее()
    {
        var md = Convert("<pre><code>a ``` b</code></pre>");
        md.Should().StartWith("````");
        md.Should().Contain("a ``` b");
    }

    [Fact]
    public void Blockquote_КаждаяСтрокаСПрефиксом()
    {
        var md = Convert("<blockquote><p>line one</p><p>line two</p></blockquote>");
        foreach (var line in md.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line))
                line.Should().StartWith(">");
    }

    [Fact]
    public void Script_Style_Iframe_Form_НеПереживаютКонвертацию()
    {
        var md = Convert("""
            <p>text</p>
            <script>alert('x')</script>
            <style>.a{color:red}</style>
            <iframe src="https://evil.example/"></iframe>
            <form action="/submit"><input name="x"></form>
            """);
        md.Should().NotContain("alert");
        md.Should().NotContain("color:red");
        md.Should().NotContain("evil.example");
        md.Should().NotContain("submit");
        md.Should().Contain("text");
    }

    [Fact]
    public void ПрозрачныйКонтейнер_DivБезОбёртки_НоСодержимоеОстаётся()
    {
        var md = Convert("<div class=\"wrapper\"><p>inside div</p></div>");
        md.Should().Contain("inside div");
        md.Should().NotContain("wrapper");
    }

    [Fact]
    public void Ссылка_HttpsСхема_СтановитсяMarkdownСсылкой()
    {
        var md = Convert("<p><a href=\"https://example.com/x\">click</a></p>");
        md.Should().Contain("[click](https://example.com/x)");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("vbscript:msgbox(1)")]
    public void Ссылка_ОпаснаяСхема_РазворачиваетсяВТекст(string href)
    {
        var md = Convert($"<p><a href=\"{href}\">click</a></p>");
        md.Should().NotContain("](");
        md.Should().Contain("click");
    }

    [Fact]
    public void Картинка_JavascriptСхема_НеСтановитсяКартинкой()
    {
        var md = Convert("<p><img src=\"javascript:alert(1)\" alt=\"pic\"></p>");
        md.Should().NotContain("![");
    }

    [Fact]
    public void Картинка_HttpsСхема_РазрешаетсяКакАбсолютнаяСсылка()
    {
        var md = Convert("<p><img src=\"https://example.com/pic.png\" alt=\"pic\"></p>");
        md.Should().Contain("![pic](https://example.com/pic.png)");
    }

    [Fact]
    public void ТекстСоЗвёздочкамиИРешёткой_Экранируется()
    {
        var md = Convert("<p>**not bold** and #not-a-heading</p>");
        md.Should().Contain("\\*\\*not bold\\*\\*");
        md.Should().Contain("\\#not-a-heading");
    }

    [Fact]
    public void Hr_СтановитсяРазделителем()
    {
        Convert("<hr>").Should().Be("---");
    }

    [Fact]
    public void ОбработчикOnclick_НеПопадаетНаВыход()
    {
        var md = Convert("<p><a href=\"https://example.com/\" onclick=\"steal()\">click</a></p>");
        md.Should().NotContain("onclick");
        md.Should().NotContain("steal");
    }
}
