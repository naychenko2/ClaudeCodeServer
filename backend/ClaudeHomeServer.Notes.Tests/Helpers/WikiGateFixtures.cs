using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Helpers;

// Каркас валидной страницы и карточки источника для тестов гейта правил раздела (S2a,
// ADR-035 §3, ADR-036 §4): один общий источник для WikiGateTests и WikiPackageClaimTests
// вместо прежних копий в каждом файле. Ничего здесь правилом гейта не является — это
// только вход для него; конкретные утверждения и цитаты остаются у каждого теста своим.
internal static class WikiGateFixtures
{
    /// <summary>Проектный раздел p1: его идентификатор для гейта и его каталог в вике.</summary>
    public const string P1 = "projects/p1";
    public const string P1Dir = "Проекты/p1/";

    // Карточка источника той же страницы: сноска обязана указывать на type: source.
    public static string Card(string title, string origin = "note", string dir = P1Dir) =>
        Fm("source", $"origin: {origin}\nref: note:1\nhash: h1\ncaptured: 2026-10-28\n") + $"\n\n# {title}\n";

    public static string Fm(string type, string extra = "") =>
        "---\ntype: " + type + "\nsummary: тестовая страница\n" + extra + "status: active\n---";

    // Обычная страница: четыре фиксированных секции; содержание — в «Сводке», сноски — в конце.
    public static string Page(
        string title = "Тет",
        string swodka = "",
        string footnotes = "",
        string fmExtra = "",
        string dir = P1Dir,
        string type = "entity")
    {
        var body = $"\n\n# {title}\n\n## Сводка\n{swodka}\n\n## Хронология\n\n## Противоречия\n\n## История\n";
        if (footnotes.Length > 0)
            body += "\n" + footnotes + "\n";
        return Fm(type, fmExtra) + body;
    }

    /// <summary>Карточка «Карточка» в «Источниках» p1 — готовый Draft для состояния гейта.</summary>
    public static readonly WikiGate.Draft CardDraft = new(P1Dir + "Источники/Карточка.md", Card("Карточка"));
}
