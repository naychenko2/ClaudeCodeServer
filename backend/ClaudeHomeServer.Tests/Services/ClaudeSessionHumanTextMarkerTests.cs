using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 1 плана «сжатие с сохранением дословного»: маркер реплики человека в склейке хода.
// BuildTurnTextForCli вклеивает HumanTextMarkerFormat непосредственно перед text; N — длина
// text в кодовых точках Unicode (EnumerateRunes, не Length: прокси на Python считает кодовыми
// точками, а эмодзи по .Length разойдутся — surrogate pair = 2 символа, 1 кодовая точка).
// Без recall маркера нет. Чистая функция — тест без процесса CLI, платформонезависим (CI Linux).
public class ClaudeSessionHumanTextMarkerTests
{
    // 1. Recall + text: маркер стоит непосредственно перед text, после "---"; N = кодовые
    //    точки text. «привет 🚀» = 6 + 1 + 1 = 8 кодовых точек (🚀 — 1 точка, 2 UTF-16-символа).
    [Fact]
    public void Склейка_СRecall_МаркерСВернымN()
    {
        const string recall = "recall-секция";
        const string text = "привет 🚀";
        var result = ClaudeSession.BuildTurnTextForCli(recall, text);
        // N = 8 (кодовые точки), а не 9 (text.Length): эмодзи — surrogate pair
        const string expected = "recall-секция\n\n---\n<!-- ccs:human-text chars=8 -->\n\nпривет 🚀";
        Assert.Equal(expected, result);
    }

    // 2. Эмодзи: N считает кодовыми точками, а не UTF-16-символами.
    //    "🚀" = 1 кодовая точка, 2 UTF-16-символа → N = 1, а не 2.
    [Fact]
    public void Склейка_Эмодзи_NПоКодовымТочкам()
    {
        const string recall = "r";
        const string text = "🚀";
        var result = ClaudeSession.BuildTurnTextForCli(recall, text);
        Assert.Equal("r\n\n---\n<!-- ccs:human-text chars=1 -->\n\n🚀", result);
    }

    // 3. Без recall: маркера нет, результат = text как есть (null и пустая строка — одно и то же).
    [Fact]
    public void Склейка_БезRecall_БезМаркера()
    {
        const string text = "просто текст 🚀";
        Assert.Equal(text, ClaudeSession.BuildTurnTextForCli(null, text));
        Assert.Equal(text, ClaudeSession.BuildTurnTextForCli("", text));
    }

    // 4. Формат маркера зафиксирован: его строку прочитает тест-константа прокси (шаг 3).
    //    Если константу поменяют — этот тест краснеет, и шаг 3 не уедет в рассинхрон.
    [Fact]
    public void ФорматМаркера_Зафиксирован()
    {
        Assert.Equal("<!-- ccs:human-text chars={0} -->", ClaudeSession.HumanTextMarkerFormat);
    }
}
