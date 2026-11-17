using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Sessions;

// И6 (детский режим, разрез «Знания репетитора» §4, строки 5–6): результат инструмента детского
// чата вырезается ДО записи в историю (TurnAccumulator.OnToolResult) и ДО широковещания — в оба
// канала (history.json и хреб) идёт один заменённый объект с Content = ChildToolResultPlaceholder.
// Строка-маркер исходного результата не встречается ни в одном канале; IsError сохраняется (на нём
// держатся виджет и детская лента). Взрослый чат — без изменений; ToolUseMessage (ввод) не тронут.
//
// Два детских теста разведены по каналам (история / хреб), чтобы мутация «вырезать после
// широковещания» краснила ровно тест хаба, а тест истории оставался зелёным.
//
// Маркеры — ASCII: history.json сериализуется с экранированием не-ASCII (\uXXXX), и проверка
// подстрокой по кириллическому маркеру не совпала бы с экранированным файлом.
public class ChildToolResultRedactionTests : SessionManagerTestBase
{
    private const string ResultMarker = "secret-material-42";
    private const string InputMarker = "input-request-7";

    private async Task<Session> MkChatAsync(string suffix, string? childSpace)
    {
        var dir = MkProjectDir("redact_" + suffix);
        var project = _projectManager.Create("REDACT-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: "чат");
        session.ChildSpace = childSpace;
        return session;
    }

    // Прогон ToolUse → ToolResult через ОДИН аккумулятор с saveKey: OnToolResult находит
    // зарегистрированный карточкой инструмент и пишет результат; SaveSnapshotAsync кладёт его
    // в history.json под saveKey — ровно та запись, что читает GET /api/chats/{id}/history.
    private async Task<(string CsId, string ToolId)> DriveToolResultAsync(Session session, bool isError)
    {
        var csId = "redact-" + Guid.NewGuid().ToString("N");
        var toolId = "toolu-" + Guid.NewGuid().ToString("N");
        var acc = new TurnAccumulator(new List<StoredMessage>(), csId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ToolUseMessage(toolId, "Read", InputMarker, null, null));
        await InvokeOnMessageAsync(session.Id, acc,
            new ToolResultMessage(toolId, ResultMarker, isError));
        return (csId, toolId);
    }

    private StoredToolUseMessage HistoryTool(string csId, string toolId) =>
        _historyService.LoadAsync(csId).GetAwaiter().GetResult()
            .OfType<StoredToolUseMessage>().Single(t => t.Id == toolId);

    private string HistoryRawText(string csId) =>
        File.ReadAllText(Path.Combine(_tempDir, "sessions", csId, "history.json"));

    private ToolResultMessage BroadcastResult(string toolId) =>
        Sent<ToolResultMessage>().Single(m => m.ToolUseId == toolId);

    [Fact]
    public async Task ДетскийЧат_Результат_Вырезан_ВИстории()
    {
        var session = await MkChatAsync("hist", ChildSpaces.Lessons);
        var (csId, toolId) = await DriveToolResultAsync(session, isError: true);

        // history.json: вместо результата — заглушка, IsError исходника сохранён, ввод (Input) цел.
        var stored = HistoryTool(csId, toolId);
        stored.Result.Should().Be(TurnCompletion.ChildToolResultPlaceholder);
        stored.Result.Should().NotContain(ResultMarker, "материалы детского чата не попадают в историю");
        stored.IsError.Should().BeTrue("IsError держит виджет и детскую ленту — он не вырезается");

        var raw = HistoryRawText(csId);
        raw.Should().NotContain(ResultMarker);
        raw.Should().Contain(InputMarker, "ToolUseMessage (ввод) не тронут вырезкой");
    }

    [Fact]
    public async Task ДетскийЧат_Результат_Вырезан_ВХабе()
    {
        var session = await MkChatAsync("hub", ChildSpaces.Lessons);
        var (csId, toolId) = await DriveToolResultAsync(session, isError: true);

        // Широковещание хаба: тот же заменённый объект, IsError сохранён, маркера результата нет.
        var sent = BroadcastResult(toolId);
        sent.Content.Should().Be(TurnCompletion.ChildToolResultPlaceholder);
        sent.Content.Should().NotContain(ResultMarker);
        sent.IsError.Should().BeTrue();
    }

    // Ревью К4a (К3): ветка entry == null — сессию удалили посреди хода, а результат инструмента ещё
    // летит. Вырезка раньше держалась на entry.Info.ChildSpace и без записи о сессии пропускала сырой
    // результат в хаб и историю; теперь без записи вырезка включается (fail-closed): признак ребёнка
    // узнать нечем, а читателя у результата уже нет. Мутация: вернуть условие `entry?.Info.ChildSpace is not null`
    // в TurnCompletion — тест краснеет (в хаб уходит исходный Content).
    [Fact]
    public async Task ДетскийЧат_СессияУдаленаПосредиХода_РезультатВХабИИсториюНеУходит()
    {
        var session = await MkChatAsync("gone", ChildSpaces.Lessons);
        var csId = "redact-" + Guid.NewGuid().ToString("N");
        var toolId = "toolu-" + Guid.NewGuid().ToString("N");
        var acc = new TurnAccumulator(new List<StoredMessage>(), csId);
        await InvokeOnMessageAsync(session.Id, acc, new ToolUseMessage(toolId, "Read", InputMarker, null, null));
        await _sut.DeleteAsync(session.Id);
        var sentBefore = Sent<ToolResultMessage>().Count;

        await InvokeOnMessageAsync(session.Id, acc, new ToolResultMessage(toolId, ResultMarker, false));

        Sent<ToolResultMessage>().Skip(sentBefore).Should().OnlyContain(m => !m.Content.Contains(ResultMarker),
            "после удаления чата сырой результат инструмента в хаб не уходит");
        var historyFile = Path.Combine(_tempDir, "sessions", csId, "history.json");
        (File.Exists(historyFile) ? File.ReadAllText(historyFile) : "").Should().NotContain(ResultMarker,
            "и в историю удалённого чата сырой результат не записывается");
    }

    [Fact]
    public async Task ВзрослыйЧат_Результат_БезИзменений()
    {
        var session = await MkChatAsync("adult", childSpace: null);
        var (csId, toolId) = await DriveToolResultAsync(session, isError: true);

        // Adult-путь: Content в истории и в хреб равен исходному побайтно, IsError цел, заглушка не ставится.
        var stored = HistoryTool(csId, toolId);
        stored.Result.Should().Be(ResultMarker);
        stored.IsError.Should().BeTrue();

        var raw = HistoryRawText(csId);
        raw.Should().Contain(ResultMarker);
        raw.Should().NotContain(TurnCompletion.ChildToolResultPlaceholder);

        var sent = BroadcastResult(toolId);
        sent.Content.Should().Be(ResultMarker);
        sent.IsError.Should().BeTrue();
    }
}
