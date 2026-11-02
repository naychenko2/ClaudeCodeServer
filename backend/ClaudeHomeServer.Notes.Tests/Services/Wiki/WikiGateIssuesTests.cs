using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services.Wiki;

// SV1-Г: разбор нарушений гейта в коды для редактора (чистая функция, без хоста).
public class WikiGateIssuesTests
{
    private const string Guid1 = "11111111-2222-3333-4444-555555555555";

    private static string? Names(string key) => key == "projects/" + Guid1 ? "Соседний" : null;

    [Fact]
    public void Вбок_ПрефиксныйИГолый_ОдинКод_ИмяРазделаЖивое()
    {
        var issues = WikiGateIssues.Classify([
            $"Проекты/a/X.md: ссылка '[[projects/{Guid1}/Чужая]]' в раздел 'projects/{Guid1}' — вбок или вниз, допустимы только projects/a",
            $"Проекты/a/X.md: ссылка '[[Чужая]]' резолвится только в чужом разделе 'projects/{Guid1}' — вбок или вниз",
        ], "", Names);
        issues.Select(i => i.Code).Should().OnlyContain(c => c == WikiGateIssues.LinkSideways);
        issues[0].Link.Should().Be($"projects/{Guid1}/Чужая");
        issues[1].Link.Should().Be("Чужая");
        issues.Should().OnlyContain(i => i.Message.Contains("Соседний") && !i.Message.Contains(Guid1));
        issues.Should().OnlyContain(i => i.Page == "Проекты/a/X.md");
    }

    [Fact]
    public void ИмениНет_ПодставляетсяДругойРаздел_БезGuid()
    {
        var issue = WikiGateIssues.Classify(
            [$"Проекты/a/X.md: ссылка '[[Чужая]]' резолвится только в чужом разделе 'projects/{Guid1}' — вбок или вниз"],
            "", _ => null).Single();
        issue.Message.Should().Contain("другой раздел").And.NotContain(Guid1);
    }

    [Fact]
    public void Нераспознанное_wiki_other_СтраницаПоУмолчанию()
    {
        var issue = WikiGateIssues.Classify(["что-то новое, чего разбор не знает"], "P/Q.md", Names).Single();
        issue.Code.Should().Be(WikiGateIssues.Other);
        issue.Page.Should().Be("P/Q.md");
        issue.Link.Should().BeNull();
    }

    [Fact]
    public void КаждыйКодИзПеречняДостижим()
    {
        var samples = new Dictionary<string, string>
        {
            [WikiGateIssues.LinkUnknown] = "p.md: ссылка '[[A]]' не найдена в разделе и вверх по цепочке",
            [WikiGateIssues.LinkPrefix] = "p.md: ссылка '[[a/b]]' с неразобранным префиксом раздела",
            [WikiGateIssues.HeaderField] = "поля шапки status человек не правит: доступны только summary",
            [WikiGateIssues.TitleChange] = "заголовок страницы не правится: переименование — отдельное действие",
            [WikiGateIssues.NotesFootnote] = "p.md: в секции «Заметки» нет сносок: x [^s1]",
            [WikiGateIssues.Parse] = "новый текст страницы не разобран: нет шапки",
            [WikiGateIssues.Other] = "p.md: дубль заголовка 'x'",
        };
        foreach (var (code, text) in samples)
            WikiGateIssues.Classify([text], "d.md", Names).Single().Code.Should().Be(code);
        WikiGateIssues.Codes.Should().Contain(samples.Keys).And.Contain(WikiGateIssues.LinkSideways);
    }
}
