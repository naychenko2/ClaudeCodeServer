using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Структурные события жизненного цикла фоновых агентов CLI 2.1.220+ (task_started/
// task_notification/background_tasks_changed). Часть 1 — чистые парсеры на реальных JSON-
// образцах CLI (по образцу ParseTaskOutputCompletion из BgAgentLifecycleTests). Часть 2 —
// обработчики (HandleTaskStarted/HandleStructuredTaskNotification/HandleBackgroundTasksChanged)
// и общий хелпер завершения CompleteBgTasksAsync: они приватные и работают с приватным CliRun
// (состояние прогона намеренно не течёт наружу из ClaudeSession) — публичный API это не
// воспроизводит, доступ через reflection (white-box, тот же приём, что в SessionManagerTests).
public class StructuredBgEventsTests : IDisposable
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    // === Часть 1: ParseTaskStarted / ParseTaskNotification / IsBackgroundTasksEmptySnapshot ===

    [Fact]
    public void ParseTaskStarted_РеальныйОбразец_ВозвращаетTaskIdИToolUseId()
    {
        // Живой образец CLI 2.1.220 (проверено на реальном процессе)
        var root = El("""
            {"type":"system","subtype":"task_started","task_id":"ad465d65e2756280a","tool_use_id":"toolu_0114LFv7u6n4VGauHg8ffycj","description":"Test task","subagent_type":"general-purpose","task_type":"local_agent","prompt":"Reply with the single word DONE and nothing else"}
            """);

        var r = ClaudeSession.ParseTaskStarted(root);

        r.Should().NotBeNull();
        r!.Value.TaskId.Should().Be("ad465d65e2756280a");
        r.Value.ToolUseId.Should().Be("toolu_0114LFv7u6n4VGauHg8ffycj");
    }

    [Fact]
    public void ParseTaskStarted_БезToolUseId_ВозвращаетNull()
    {
        // Без tool_use_id привязать задачу к карточке в ленте нечем
        ClaudeSession.ParseTaskStarted(El("""{"task_id":"ad465d65e2756280a"}""")).Should().BeNull();
    }

    [Fact]
    public void ParseTaskStarted_БезTaskId_ВозвращаетNull()
    {
        ClaudeSession.ParseTaskStarted(El("""{"tool_use_id":"toolu_1"}""")).Should().BeNull();
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("failed", true)]
    [InlineData("stopped", true)]
    [InlineData("running", true)] // нераспознанный/непромежуточный статус тоже считаем обрывом
    public void ParseTaskNotification_МаппитStatusВAborted(string status, bool expectedAborted)
    {
        var root = El($$"""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1","status":"{{status}}"}""");

        var r = ClaudeSession.ParseTaskNotification(root);

        r.Should().NotBeNull();
        r!.Value.TaskId.Should().Be("ad465d65e2756280a");
        r.Value.Aborted.Should().Be(expectedAborted);
    }

    [Fact]
    public void ParseTaskNotification_БезTaskId_ВозвращаетNull()
    {
        ClaudeSession.ParseTaskNotification(El("""{"status":"completed"}""")).Should().BeNull();
    }

    [Fact]
    public void ParseTaskNotification_БезToolUseId_ToolUseIdNull_НоРезультатЕсть()
    {
        // tool_use_id опционален у структурного task_notification — парсер не должен из-за
        // этого отбрасывать событие целиком (доставка не завязана на его наличие)
        var r = ClaudeSession.ParseTaskNotification(El("""{"task_id":"ad465d65e2756280a","status":"completed"}"""));

        r.Should().NotBeNull();
        r!.Value.ToolUseId.Should().BeNull();
    }

    [Fact]
    public void IsBackgroundTasksEmptySnapshot_ПустойМассив_True()
    {
        ClaudeSession.IsBackgroundTasksEmptySnapshot(El("""{"tasks":[]}""")).Should().BeTrue();
    }

    [Fact]
    public void IsBackgroundTasksEmptySnapshot_РеальныйНепустойОбразец_False()
    {
        // Живой образец CLI 2.1.220
        var root = El("""
            {"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"ad465d65e2756280a","task_type":"local_agent","description":"Test task"}]}
            """);
        ClaudeSession.IsBackgroundTasksEmptySnapshot(root).Should().BeFalse();
    }

    [Fact]
    public void IsBackgroundTasksEmptySnapshot_БезПоляTasks_False()
    {
        ClaudeSession.IsBackgroundTasksEmptySnapshot(El("""{"subtype":"background_tasks_changed"}""")).Should().BeFalse();
    }

    // === Часть 2: обработчики + общий хелпер завершения (white-box через reflection) ===

    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo HandleTaskStartedMethod =
        typeof(ClaudeSession).GetMethod("HandleTaskStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo HandleStructuredTaskNotificationMethod =
        typeof(ClaudeSession).GetMethod("HandleStructuredTaskNotification", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo HandleBackgroundTasksChangedMethod =
        typeof(ClaudeSession).GetMethod("HandleBackgroundTasksChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo HandleTaskNotificationTextMethod =
        typeof(ClaudeSession).GetMethod("HandleTaskNotification", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo CompleteBgTasksAsyncMethod =
        typeof(ClaudeSession).GetMethod("CompleteBgTasksAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo RunField =
        typeof(ClaudeSession).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) p.Dispose();
    }

    // CliRun — приватный вложенный класс: Process required, но обработчики его не трогают,
    // пока run.TurnDone == false (дефолт) — CloseStdinIfIdle выходит раньше, чем до него
    // доберётся. Тем не менее выставляем настоящий (незапущенный) Process — дешёво и не
    // оставляет required-поле в непредсказуемом дефолтном состоянии.
    private object NewRun()
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        return run;
    }

    // Карточки агентов из потока сообщений. Рядом с ними в тот же поток идёт присутствие
    // фона (BgAgentsPresenceMessage — сигнал для списка чатов), поэтому «сколько карточек
    // закрылось» считаем по типу, а не по длине списка
    private static IReadOnlyList<BgAgentDoneMessage> DoneOf(IEnumerable<ServerMessage> sent) =>
        sent.OfType<BgAgentDoneMessage>().ToList();

    private static IReadOnlyList<BgAgentsPresenceMessage> PresenceOf(IEnumerable<ServerMessage> sent) =>
        sent.OfType<BgAgentsPresenceMessage>().ToList();

    private static Dictionary<string, string> PendingBgOf(object run) =>
        (Dictionary<string, string>)CliRunType.GetField("PendingBg")!.GetValue(run)!;

    private static bool PendingBgUnknownOf(object run) =>
        (bool)CliRunType.GetField("PendingBgUnknown")!.GetValue(run)!;

    private static void SetPendingBgUnknown(object run, bool value) =>
        CliRunType.GetField("PendingBgUnknown")!.SetValue(run, value);

    private static HashSet<string> UnknownBgToolUsesOf(object run) =>
        (HashSet<string>)CliRunType.GetField("UnknownBgToolUses")!.GetValue(run)!;

    private static HashSet<string> BgLaunchCandidatesOf(object run) =>
        (HashSet<string>)CliRunType.GetField("BgLaunchCandidates")!.GetValue(run)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewClaudeSession()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: null);
        return (new ClaudeSession(new Session(), context), sent);
    }

    private static void InvokeHandleTaskStarted(ClaudeSession session, object run, JsonElement root) =>
        HandleTaskStartedMethod.Invoke(session, [run, root]);

    private static void InvokeHandleStructuredTaskNotification(ClaudeSession session, object run, JsonElement root) =>
        HandleStructuredTaskNotificationMethod.Invoke(session, [run, root]);

    private static void InvokeHandleBackgroundTasksChanged(ClaudeSession session, object run, JsonElement root) =>
        HandleBackgroundTasksChangedMethod.Invoke(session, [run, root]);

    private static void InvokeHandleTaskNotificationText(ClaudeSession session, object run, string text)
    {
        RunField.SetValue(session, run);
        HandleTaskNotificationTextMethod.Invoke(session, [text]);
    }

    private static async Task InvokeCompleteBgTasksAsync(
        ClaudeSession session, IReadOnlyList<string> toolUseIds, bool aborted, bool drainSubagent = false)
    {
        var task = (Task)CompleteBgTasksAsyncMethod.Invoke(session, [toolUseIds, aborted, drainSubagent])!;
        await task;
    }

    // Завершение через структурный/текстовый путь шлёт bg_agent_done из fire-and-forget
    // Task.Run — ждём появления сообщения вместо фиксированной паузы
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    // --- 1. task_started регистрирует фоновую задачу как активную ---

    [Fact]
    public void HandleTaskStarted_РегистрируетЗадачуВPendingBgКакАктивную()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();

        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));

        PendingBgOf(run).Should().ContainKey("ad465d65e2756280a").WhoseValue.Should().Be("toolu_1");
    }

    [Fact]
    public void HandleTaskStarted_БезToolUseId_НеРегистрирует()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();

        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a"}"""));

        PendingBgOf(run).Should().BeEmpty();
    }

    [Fact]
    public void HandleTaskStarted_СнимаетToolUseIdИзUnknown_КогдаТекстовыйПутьЕгоНеРаспознал()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        UnknownBgToolUsesOf(run).Add("toolu_1");
        SetPendingBgUnknown(run, true);

        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));

        UnknownBgToolUsesOf(run).Should().BeEmpty();
        PendingBgUnknownOf(run).Should().BeFalse();
    }

    // --- 2. task_notification доставляется и не ломает состояние при неизвестном taskId ---

    [Fact]
    public void HandleStructuredTaskNotification_НеизвестныйTaskIdБезToolUseId_НеЛомаетСостояние()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun(); // PendingBg пуст — задача этому прогону неизвестна

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"неизвестный-id","status":"completed"}"""));

        PendingBgOf(run).Should().BeEmpty();
        sent.Should().BeEmpty(); // без tool_use_id закрыть карточку нечем — событие не шлётся
    }

    [Fact]
    public async Task HandleStructuredTaskNotification_ИзвестныйTaskId_ЗавершаетИДоставляетСобытие()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1","status":"completed"}"""));
        await WaitForAsync(() => DoneOf(sent).Count > 0);

        PendingBgOf(run).Should().NotContainKey("ad465d65e2756280a");
        DoneOf(sent).Should().ContainSingle();
        var msg = DoneOf(sent)[0];
        msg.ToolUseIds.Should().Equal("toolu_1");
        msg.Aborted.Should().BeFalse();
    }

    [Fact]
    public async Task HandleStructuredTaskNotification_НеизвестныйTaskIdСToolUseId_ФолбэкВсёРавноДоставляет()
    {
        // Fallback: запуск проехал мимо PendingBg (TrackBgLaunch не распознал tool_result и не
        // снял tool_use_id с учёта), но tool_use_id всё ещё числится кандидатом на запуск
        // (BgLaunchCandidates — так и есть в реальности: он появляется там ДО task_notification,
        // при первом же content_block_start у Task/Agent с run_in_background) — карточку всё
        // равно закрываем, иначе она крутилась бы вечно
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        BgLaunchCandidatesOf(run).Add("toolu_орфан");

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"неизвестный-id","tool_use_id":"toolu_орфан","status":"failed"}"""));
        await WaitForAsync(() => DoneOf(sent).Count > 0);

        DoneOf(sent).Should().ContainSingle();
        var msg = DoneOf(sent)[0];
        msg.ToolUseIds.Should().Equal("toolu_орфан");
        msg.Aborted.Should().BeTrue();
    }

    // --- 3. background_tasks_changed приводит состояние активных задач к пришедшему списку ---

    [Fact]
    public void HandleBackgroundTasksChanged_ПустойСписок_СбрасываетНеучтённыеЗадачи()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        UnknownBgToolUsesOf(run).Add("toolu_1");
        SetPendingBgUnknown(run, true);

        InvokeHandleBackgroundTasksChanged(session, run, El("""{"tasks":[]}"""));

        PendingBgUnknownOf(run).Should().BeFalse();
    }

    [Fact]
    public void HandleBackgroundTasksChanged_НепустойСписок_НеТрогаетСостояние()
    {
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        SetPendingBgUnknown(run, true);
        var root = El("""{"tasks":[{"task_id":"ad465d65e2756280a","task_type":"local_agent","description":"Test task"}]}""");

        InvokeHandleBackgroundTasksChanged(session, run, root);

        PendingBgUnknownOf(run).Should().BeTrue();
    }

    [Fact]
    public void HandleBackgroundTasksChanged_НеЗакрываетУжеАктивныеКарточки()
    {
        // Снэпшот приводит только PendingBgUnknown к пришедшему списку; сами карточки
        // (PendingBg) закрывает исключительно task_notification/финализация прогона
        var (session, _) = NewClaudeSession();
        var run = NewRun();
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));

        InvokeHandleBackgroundTasksChanged(session, run, El("""{"tasks":[]}"""));

        PendingBgOf(run).Should().ContainKey("ad465d65e2756280a");
    }

    // --- 4. общий хелпер завершения вызывается на всех путях и идемпотентен ---

    [Fact]
    public async Task CompleteBgTasksAsync_ОтправляетBgAgentDoneОдинРаз()
    {
        var (session, sent) = NewClaudeSession();

        await InvokeCompleteBgTasksAsync(session, ["tool1"], aborted: false);

        sent.Should().ContainSingle().Which.Should().BeOfType<BgAgentDoneMessage>();
        ((BgAgentDoneMessage)sent[0]).ToolUseIds.Should().Equal("tool1");
    }

    [Fact]
    public async Task CompleteBgTasksAsync_ПустойСписокЗадач_НичегоНеШлёт()
    {
        // Идемпотентность самого хелпера: вызов без задач (второе завершение уже закрытой) — no-op
        var (session, sent) = NewClaudeSession();

        await InvokeCompleteBgTasksAsync(session, [], aborted: false);

        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStructuredTaskNotification_ПовторноеСобытиеУжеЗакрытойЗадачи_НеДублирует()
    {
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));
        var notification = El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1","status":"completed"}""");

        InvokeHandleStructuredTaskNotification(session, run, notification); // первое завершение
        await WaitForAsync(() => DoneOf(sent).Count > 0);
        InvokeHandleStructuredTaskNotification(session, run, notification); // повтор того же события
        await Task.Delay(100); // дать шанс возможному второму fire-and-forget Task.Run

        DoneOf(sent).Should().ContainSingle("повторное событие для уже закрытой задачи не должно задваивать bg_agent_done");
    }

    [Fact]
    public async Task СтруктурныйИТекстовыйПутьРазделяютОбщийХелпер_ВторойНеДублирует()
    {
        // Общий хелпер завершения переиспользуется структурным (HandleStructuredTaskNotification)
        // и текстовым (HandleTaskNotification) путями через один и тот же run.PendingBg — откат
        // рефакторинга (возврат к третьей копии Drain→BgAgentDone→CloseStdinIfIdle в одном из
        // путей вместо переиспользования) уронил бы именно этот тест: второй путь либо честно
        // не найдёт задачу (что и проверяем), либо, при дублирующей логике, пошлёт вторую карточку
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1"}"""));

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"ad465d65e2756280a","tool_use_id":"toolu_1","status":"completed"}""")); // структурный путь
        await WaitForAsync(() => DoneOf(sent).Count > 0);

        InvokeHandleTaskNotificationText(session, run,
            "<task-notification><task-id>ad465d65e2756280a</task-id></task-notification>"); // текстовый путь, тот же run
        await Task.Delay(100);

        DoneOf(sent).Should().ContainSingle("текстовый путь после структурного не находит задачу в PendingBg и не шлёт вторую карточку");
    }

    // --- 5. присутствие фона: сигнал для СПИСКА чатов, только на переходе 0↔N ---

    [Fact]
    public async Task ПерваяФоноваяЗадача_ПубликуетПрисутствие()
    {
        // Пока фоновый агент работает, ход чата уже завершён и статус сессии — Active,
        // у которого нет ни свечения, ни движения. Это событие — единственный способ
        // для списка чатов узнать, что работа в чате всё-таки идёт
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        InvokeHandleTaskStarted(session, run, El("""{"task_id":"task-1","tool_use_id":"toolu_1"}"""));
        await WaitForAsync(() => PresenceOf(sent).Count > 0);

        PresenceOf(sent).Should().ContainSingle().Which.Active.Should().BeTrue();
    }

    [Fact]
    public async Task ВтораяФоноваяЗадача_ПрисутствиеНеПовторяется()
    {
        // Гейт по переходу: событий должно быть столько, сколько РАЗ менялось состояние,
        // а не сколько задач запущено — иначе десяток агентов дал бы десяток рассылок
        // всем вкладкам проекта подряд
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        InvokeHandleTaskStarted(session, run, El("""{"task_id":"task-1","tool_use_id":"toolu_1"}"""));
        await WaitForAsync(() => PresenceOf(sent).Count > 0);
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"task-2","tool_use_id":"toolu_2"}"""));
        await Task.Delay(100); // дать шанс возможной второй fire-and-forget рассылке

        PresenceOf(sent).Should().ContainSingle("присутствие публикуется на переходе 0↔N, а не на каждой задаче");
    }

    [Fact]
    public async Task НеучтённыйФоновыйЗапуск_ПрисутствиеНеПубликуется()
    {
        // PendingBgUnknown значит «видели фоновый запуск, но id задачи не распознали». Он
        // намеренно консервативен и держит процесс живым — но говорить человеку «здесь
        // работают агенты» на этом основании нельзя: конкретной задачи нет, панель агентов
        // пуста, и значок на карточке чата оказывается ложным (замечено на бою 22.08)
        var (session, sent) = NewClaudeSession();
        var run = NewRun();

        InvokeHandleTaskNotificationText(session, run, "неважно"); // не трогает набор задач
        SetPendingBgUnknown(run, true);
        UnknownBgToolUsesOf(run).Add("toolu_1");
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"","tool_use_id":""}""")); // не парсится
        await Task.Delay(100);

        PresenceOf(sent).Should().BeEmpty("неопознанный запуск — не повод светить значок агентов");
    }

    [Fact]
    public async Task ЗакрытиеПоследнейЗадачи_СнимаетПрисутствие()
    {
        // Пока жива хоть одна задача — присутствие держится; снимается ровно на последней
        var (session, sent) = NewClaudeSession();
        var run = NewRun();
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"task-1","tool_use_id":"toolu_1"}"""));
        InvokeHandleTaskStarted(session, run, El("""{"task_id":"task-2","tool_use_id":"toolu_2"}"""));
        await WaitForAsync(() => PresenceOf(sent).Count > 0);

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"task-1","tool_use_id":"toolu_1","status":"completed"}"""));
        await Task.Delay(100);
        PresenceOf(sent).Should().ContainSingle("одна задача ещё работает — присутствие не снимаем");

        InvokeHandleStructuredTaskNotification(session, run,
            El("""{"task_id":"task-2","tool_use_id":"toolu_2","status":"completed"}"""));
        await WaitForAsync(() => PresenceOf(sent).Count > 1);

        PresenceOf(sent).Should().HaveCount(2);
        PresenceOf(sent)[1].Active.Should().BeFalse();
    }
}
