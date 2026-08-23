using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// SubagentStreamWatcher поллит транскрипты сабагентов и эмитит паспорта прогонов.
// Тесты ниже проверяют два ключевых сценария:
// 1. Чаты под ProfilesRoot (profiles/{key}/projects/…) — ватчер находит папку
// 2. Старые файлы без новых строк не забивают журнал нулевыми паспортами
//
// sessionId уникален НА КАЖДЫЙ тест: фолбэк-поиск ватчера ищет папку по id сессии,
// и общая константа позволяла одному тесту найти папку другого через утёкший в
// статический WorkflowAgentParser._extraRoots корень (в проде id сессии уникален —
// сессия живёт ровно в одном проекте)
public class SubagentStreamWatcherTests : IDisposable
{
    // Временный корень ProfilesRoot для теста — изолирован от реального профиля
    private readonly string _profilesRoot;
    private readonly string _sessionId = "sess-" + Guid.NewGuid().ToString("N");
    private readonly List<SubagentRunPassport> _passports = new();
    private int _messageCount;

    public SubagentStreamWatcherTests()
    {
        _profilesRoot = Path.Combine(Path.GetTempPath(), "claude-profiles-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
            Directory.Delete(_profilesRoot, recursive: true);
    }

    // Создаёт структуру папок профиля с транскриптом сабагента
    private (string cwd, string agentFile) SetupProfileSubagent(string profileKey, string sessionId, string agentId,
        string? cwd = null, params string[] transcriptLines)
    {
        // cwd — АБСОЛЮТНЫЙ путь проекта, как его передаёт продукт (RootPath); flat-имя
        // папки ватчер вычисляет заменой не-алфанумерических символов на '-'
        var actualCwd = cwd ?? Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
        var flat = string.Concat(actualCwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        var subagentsDir = Path.Combine(_profilesRoot, profileKey, "projects", flat, sessionId, "subagents");
        Directory.CreateDirectory(subagentsDir);

        var agentFile = Path.Combine(subagentsDir, $"agent-{agentId}.jsonl");
        File.WriteAllLines(agentFile, transcriptLines);

        // meta.json с toolUseId
        var toolUseId = "toolu_" + Guid.NewGuid().ToString("N");
        var metaFile = Path.Combine(subagentsDir, $"agent-{agentId}.meta.json");
        File.WriteAllText(metaFile, string.Format("{{\"toolUseId\":\"{0}\",\"agentType\":\"mark\",\"description\":\"Тестовый агент\"}}", toolUseId));

        return (actualCwd, agentFile);
    }

    private static string Json(string withSingleQuotes) => withSingleQuotes.Replace('\'', '"');

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
    }

    private static string Prompt(string ts, string text)
    {
        var json = string.Format("{{'type':'user','timestamp':'{0}','message':{{'role':'user','content':'{1}'}}}}", ts, text);
        return Json(json);
    }

    private static string ToolCall(string ts, string tool)
    {
        var json = "{'type':'assistant','timestamp':'" + ts + "','message':{'role':'assistant'," +
            "'model':'claude-opus-5','stop_reason':'tool_use'," +
            "'usage':{'input_tokens':10,'cache_read_input_tokens':1000,'cache_creation_input_tokens':0,'output_tokens':100}," +
            "'content':[{'type':'tool_use','name':'" + tool + "','input':{}}]}}";
        return Json(json);
    }

    private static string Report(string ts)
    {
        var json = "{'type':'assistant','timestamp':'" + ts + "','message':{'role':'assistant'," +
            "'model':'claude-opus-5','stop_reason':'end_turn'," +
            "'usage':{'input_tokens':2,'cache_read_input_tokens':2000,'cache_creation_input_tokens':50,'output_tokens':500}," +
            "'content':[{'type':'text','text':'Готово'}]}}";
        return Json(json);
    }

    [Fact]
    public void ProfilesRoot_ВатчерНаходитПапкуИПарсит()
    {
        // Устанавливаем ProfilesRoot перед созданием ватчера
        var originalProfilesRoot = WorkflowAgentParser.ProfilesRoot;
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;

        try
        {
            // Arrange: создаём ватчер ПЕРЕД созданием транскрипта (новый агент текущего хода)
            // Используем конкретный cwd, но файлы создадим ПОСЛЕ старта
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            var watcher = new SubagentStreamWatcher(cwd, _sessionId,
                msg => { _messageCount++; return Task.CompletedTask; },
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot);
            watcher.Start();

            // Создаём структуру под ProfilesRoot ПОСЛЕ старта ватчера
            var (_, _) = SetupProfileSubagent("sub-test", _sessionId, "agent1", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                ToolCall("2026-08-22T10:00:10.000Z", "Read"),
                Report("2026-08-22T10:00:20.000Z"));

            // Ждём, пока ватчер прочитает транскрипт
            WaitUntil(() => _passports.Count > 0);

            watcher.Dispose();

            // Assert: ватчер нашёл папку и прочитал транскрипт
            _passports.Should().HaveCount(1, "должен быть один паспорт агента");
            var passport = _passports[0];
            passport.AgentId.Should().Be("agent1");
            passport.ToolUses.Should().Be(1);
            passport.AssistantMessages.Should().Be(2);
            passport.Prompts.Should().Be(1);
            passport.LastStopReason.Should().Be("end_turn");
            passport.Truncated.Should().BeFalse();
        }
        finally
        {
            WorkflowAgentParser.ProfilesRoot = originalProfilesRoot;
        }
    }

    [Fact]
    public void СтарыйФайл_БезНовыхСтрок_ПаспортНеПишется()
    {
        // Arrange: создаём БОГАТЫЙ файл агента ДО старта ватчера (Prompt + ToolCall + Report)
        var (cwd, _) = SetupProfileSubagent("sub-test", _sessionId, "agent-old",
            Prompt("2026-08-22T10:00:00.000Z", "Задача"),
            ToolCall("2026-08-22T10:00:10.000Z", "Bash"),
            Report("2026-08-22T10:00:20.000Z"));

        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            // Act: запускаем ватчер, но новых строк нет
            var watcher = new SubagentStreamWatcher(cwd, _sessionId,
                msg => Task.CompletedTask,
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot);
            watcher.Start();
            Thread.Sleep(2000); // пара тиков поллинга, новых строк нет
            watcher.Dispose();

            // Assert: паспорт не должен быть записан — файл был старым без новой активности
            _passports.Should().BeEmpty("старый файл без активности не даёт паспорта");
        }
        finally
        {
            WorkflowAgentParser.ProfilesRoot = null;
        }
    }

    [Fact]
    public void СтарыйФайл_СНовымиСтроками_ПаспортПишется()
    {
        // Arrange: создаём БОГАТЫЙ файл с базовым содержимым ДО старта ватчера
        var (cwd, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-new",
            Prompt("2026-08-22T10:00:00.000Z", "Задача"),
            ToolCall("2026-08-22T10:00:10.000Z", "Bash"),
            Report("2026-08-22T10:00:20.000Z"));

        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            // Act: стартуем ватчер, затем дописываем строки
            var watcher = new SubagentStreamWatcher(cwd, _sessionId,
                msg => { _messageCount++; return Task.CompletedTask; },
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot);
            watcher.Start();
            Thread.Sleep(200);

            // Дописываем строки после старта
            File.AppendAllText(agentFile, ToolCall("2026-08-22T10:00:30.000Z", "Read") + "\n");

            // Ждём, пока ватчер прочитает новые строки
            WaitUntil(() => _messageCount > 0);

            watcher.Dispose();

            // Assert: паспорт должен быть записан — были новые строки
            _passports.Should().HaveCount(1);
            var passport = _passports[0];
            passport.AgentId.Should().Be("agent-new");
            passport.ToolUses.Should().BeGreaterThan(0);
            passport.ContextTokens.Should().BeGreaterThan(0);
        }
        finally
        {
            WorkflowAgentParser.ProfilesRoot = null;
        }
    }

    // ─── Дебаунс детектора обрыва на bg_done ─────────────────────────────────
    // Событие bg_agent_done приходит РАНЬШЕ, чем финальный отчёт агента доезжает до
    // agent-*.jsonl (медиана дозаписи 2.6 с): хвост на момент сигнала кончается tool_use,
    // и без паузы паспорт ложно помечался оборванным. Ватчер обязан выждать, перечитать
    // хвост и эмитить паспорт один раз — штатный, если end_turn доехал.

    private static string ToolUseIdOf(string agentFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(agentFile, ".meta.json")));
        return doc.RootElement.GetProperty("toolUseId").GetString()!;
    }

    [Fact]
    public async Task BgDone_ФиналДоезжаетПозжеСигнала_ПаспортШтатныйИОдинРаз()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => { _passports.Add(p); got.TrySetResult(); }, profilesRoot: _profilesRoot)
            {
                BgDoneRecheckDelay = TimeSpan.FromSeconds(1.5),
            };
            // Start не зовём: FinalizeAsync дренирует сам, без цикла поллинга тест детерминирован
            var (_, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-late", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                ToolCall("2026-08-22T10:00:10.000Z", "Bash"));

            await watcher.FinalizeAsync([ToolUseIdOf(agentFile)], "bg_done");
            // Хвост — tool_use, но паспорт НЕ эмитится сразу: эмиссия отложена до перепроверки
            _passports.Should().BeEmpty("обрыв на bg_done объявляется только после перепроверки");

            // Финальный отчёт доезжает до транскрипта после сигнала — как в проде
            File.AppendAllText(agentFile, Report("2026-08-22T10:00:20.000Z") + "\n");

            (await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(15))))
                .Should().Be(got.Task, "перепроверка обязана эмитить паспорт");
            _passports.Should().HaveCount(1);
            _passports[0].Truncated.Should().BeFalse("end_turn доехал — обрыв опровергнут");
            _passports[0].LastStopReason.Should().Be("end_turn");
            _passports[0].FinishedBy.Should().Be("bg_done");

            // Dispose не задваивает паспорт: новой активности после перепроверки не было
            watcher.Dispose();
            _passports.Should().HaveCount(1);
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    [Fact]
    public async Task BgDone_ФиналТакИНеДоехал_ПаспортОборванныйПослеПерепроверки()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => { _passports.Add(p); got.TrySetResult(); }, profilesRoot: _profilesRoot)
            {
                BgDoneRecheckDelay = TimeSpan.FromMilliseconds(100),
            };
            var (_, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-cut", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                ToolCall("2026-08-22T10:00:10.000Z", "Bash"));

            await watcher.FinalizeAsync([ToolUseIdOf(agentFile)], "bg_done");
            _passports.Should().BeEmpty();

            (await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(15))))
                .Should().Be(got.Task, "перепроверка обязана эмитить паспорт и при реальном обрыве");
            _passports.Should().HaveCount(1, "паспорт с bg_done и обрывом эмитится один раз");
            _passports[0].Truncated.Should().BeTrue("end_turn не появился — обрыв настоящий");
            _passports[0].FinishedBy.Should().Be("bg_done");

            watcher.Dispose();
            _passports.Should().HaveCount(1);
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    [Fact]
    public async Task BgDone_ФиналДоехалПослеПерепроверки_УходитОпровержение()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => { _passports.Add(p); got.TrySetResult(); }, profilesRoot: _profilesRoot)
            {
                BgDoneRecheckDelay = TimeSpan.FromMilliseconds(100),
            };
            var (_, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-slow", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                ToolCall("2026-08-22T10:00:10.000Z", "Bash"));

            await watcher.FinalizeAsync([ToolUseIdOf(agentFile)], "bg_done");
            (await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(15)))).Should().Be(got.Task);
            _passports.Should().ContainSingle().Which.Truncated.Should().BeTrue();

            // Хвост дозаписи длиннее окна перепроверки: финал доехал уже после оборванного
            // паспорта — очередной скан обязан отдать опровержение тем же finishedBy
            File.AppendAllText(agentFile, Report("2026-08-22T10:00:40.000Z") + "\n");
            await watcher.DrainAsync();

            _passports.Should().HaveCount(2);
            _passports[1].Truncated.Should().BeFalse();
            _passports[1].FinishedBy.Should().Be("bg_done");

            watcher.Dispose();
            _passports.Should().HaveCount(2, "Dispose без новой активности паспорт не задваивает");
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    [Fact]
    public async Task BgDone_ВатчерУмерРаньшеПерепроверки_ПаспортОтдаётDisposeОдинРаз()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot);
            // BgDoneRecheckDelay остаётся дефолтным (10 с) — перепроверка заведомо не успевает
            var (_, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-dispose", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                ToolCall("2026-08-22T10:00:10.000Z", "Bash"));

            await watcher.FinalizeAsync([ToolUseIdOf(agentFile)], "bg_done");
            _passports.Should().BeEmpty();

            // Ход закончился, ватчер умирает до перепроверки: паспорт обязан уйти финальной
            // эмиссией Dispose, а отменённая перепроверка — не эмитить второй. Закрыт он как
            // bg_done — продукт уже объявил результат агента: с run_end SessionManager не звал
            // бы NoteTruncatedBgAgent, и реальный обрыв ждал бы result, который уже прошёл
            watcher.Dispose();
            _passports.Should().HaveCount(1);
            _passports[0].FinishedBy.Should().Be("bg_done");
            _passports[0].Truncated.Should().BeTrue();
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    // ─── «Свой» профиль первым ───────────────────────────────────────────────
    // Папка сессии живёт сразу в нескольких профилях, когда у чата были фолбэк-ходы через
    // другие провайдеры; перебор в порядке ФС находил первую попавшуюся — старую копию без
    // новых строк, и ватчер молчал весь день (чат 95926c09: 6 обрывов, 0 паспортов).

    [Fact]
    public async Task PreferredConfigRoot_ПапкаСессииВДвухПрофилях_ЧитаетСвой()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            // «Чужой» профиль — первым в порядке ФС (имя «a-…»), со старым транскриптом
            SetupProfileSubagent("a-alien", _sessionId, "agent-alien", cwd,
                Prompt("2026-08-22T09:00:00.000Z", "Старая задача"),
                Report("2026-08-22T09:00:20.000Z"));
            // «Свой» профиль — последним по ФС; папка сессии есть, транскриптов пока нет
            var ownRoot = Path.Combine(_profilesRoot, "z-own");
            var flat = string.Concat(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
            var ownDir = Path.Combine(ownRoot, "projects", flat, _sessionId, "subagents");
            Directory.CreateDirectory(ownDir);

            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot, preferredConfigRoot: ownRoot);
            watcher.Start();

            // Агент текущего прогона пишет в свой профиль
            File.WriteAllLines(Path.Combine(ownDir, "agent-own.jsonl"),
            [
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                Report("2026-08-22T10:00:20.000Z"),
            ]);
            File.WriteAllText(Path.Combine(ownDir, "agent-own.meta.json"),
                "{\"toolUseId\":\"toolu_own\",\"agentType\":\"mark\",\"description\":\"Свой агент\"}");

            // Finalize дренирует сам — без ожидания тика поллинга
            await watcher.FinalizeAsync(["toolu_own"], "tool_result");
            watcher.Dispose();

            _passports.Select(p => p.AgentId).Should().Equal("own");
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    [Fact]
    public async Task PreferredConfigRoot_ПапкиВНёмНет_ПереборПрофилейКакРаньше()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;
        try
        {
            var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
            // Подсказка указывает на профиль без папки сессии — это подсказка, а не запрет:
            // общий перебор обязан найти агента там, где он есть
            var emptyProfile = Path.Combine(_profilesRoot, "empty-profile");
            Directory.CreateDirectory(Path.Combine(emptyProfile, "projects"));

            var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot, preferredConfigRoot: emptyProfile);
            watcher.Start();

            var (_, agentFile) = SetupProfileSubagent("sub-test", _sessionId, "agent-swept", cwd,
                Prompt("2026-08-22T10:00:00.000Z", "Задача"),
                Report("2026-08-22T10:00:20.000Z"));

            await watcher.FinalizeAsync([ToolUseIdOf(agentFile)], "tool_result");
            watcher.Dispose();

            _passports.Select(p => p.AgentId).Should().Equal("agent-swept");
        }
        finally { WorkflowAgentParser.ProfilesRoot = null; }
    }

    [Fact]
    public void Matches_ДругойПрофильПрогона_ВатчерНеПереиспользуется()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "Ccs W test " + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(_profilesRoot, "glm");
        using var watcher = new SubagentStreamWatcher(cwd, _sessionId, _ => Task.CompletedTask,
            profilesRoot: _profilesRoot, preferredConfigRoot: root);

        watcher.Matches(cwd, _sessionId, root).Should().BeTrue();
        // Фолбэк сменил провайдера → новый процесс под другим CLAUDE_CONFIG_DIR: папка
        // транскриптов другая, закешированный _dir старого ватчера её не увидит
        watcher.Matches(cwd, _sessionId, Path.Combine(_profilesRoot, "sub-claude-2")).Should().BeFalse();
        watcher.Matches(cwd, _sessionId, null).Should().BeFalse();
    }

    [Fact]
    public void МножествоАгентов_ПрофильИДопКорень_НаходятсяВсе()
    {
        WorkflowAgentParser.ProfilesRoot = _profilesRoot;

        // Регистрируем дополнительный корень
        var extraRoot = Path.Combine(Path.GetTempPath(), "claude-extra-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extraRoot);
        WorkflowAgentParser.AddAllowedRoot(extraRoot);

        try
        {
            // У каждой сессии свой ватчер со своим cwd — как в продукте. Один ватчер
            // следит ровно за одной папкой (_dir кэшируется), поэтому корни проверяем
            // двумя независимыми ватчерами. Id сессий РАЗНЫЕ: в продукте сессия живёт
            // ровно в одном проекте, а фолбэк-поиск папки идёт по id сессии без учёта
            // cwd — с общим id второй ватчер нашёл бы папку первого
            var extraPassports = new List<SubagentRunPassport>();
            var seen1 = 0;
            var seen2 = 0;
            var sessionId2 = "sess-" + Guid.NewGuid().ToString("N");

            var cwd1 = Path.Combine(Path.GetTempPath(), "Ccs W test1 " + Guid.NewGuid().ToString("N"));
            using var watcher1 = new SubagentStreamWatcher(cwd1, _sessionId,
                _ => { seen1++; return Task.CompletedTask; },
                runSink: p => _passports.Add(p), profilesRoot: _profilesRoot);

            var cwd2 = Path.Combine(Path.GetTempPath(), "Ccs W test2 " + Guid.NewGuid().ToString("N"));
            using var watcher2 = new SubagentStreamWatcher(cwd2, sessionId2,
                _ => { seen2++; return Task.CompletedTask; },
                runSink: p => extraPassports.Add(p), profilesRoot: _profilesRoot);

            watcher1.Start();
            watcher2.Start();

            // Агент под ProfilesRoot (профиль подписки, не зарегистрирован в AllowedRoots)
            SetupProfileSubagent("sub-test", _sessionId, "agent-profile", cwd1,
                Prompt("2026-08-22T10:00:00.000Z", "Задача 1"),
                Report("2026-08-22T10:00:20.000Z"));

            // Агент под дополнительным корнем (зарегистрированный профиль провайдера)
            var flat2 = string.Concat(cwd2.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
            var sessionDir = Path.Combine(extraRoot, flat2, sessionId2, "subagents");
            Directory.CreateDirectory(sessionDir);
            File.WriteAllLines(Path.Combine(sessionDir, "agent-extra.jsonl"), new[]
            {
                Prompt("2026-08-22T10:01:00.000Z", "Задача 2"),
                Report("2026-08-22T10:01:20.000Z")
            });
            File.WriteAllText(Path.Combine(sessionDir, "agent-extra.meta.json"),
                "{\"toolUseId\":\"toolu_extra\",\"agentType\":\"designer\",\"description\":\"Доп. агент\"}");

            // Ждём, пока оба ватчера реально прочитают своих агентов (текст реплики
            // эмитится в ленту) — потом Dispose эмитит паспорта
            WaitUntil(() => seen1 >= 1 && seen2 >= 1);
            watcher1.Dispose();
            watcher2.Dispose();

            _passports.Select(p => p.AgentId).Should().Contain("agent-profile");
            // agent-extra.jsonl → id «extra» (префикс agent- отрезается при разборе имени)
            extraPassports.Select(p => p.AgentId).Should().Contain("extra");
        }
        finally
        {
            WorkflowAgentParser.ProfilesRoot = null;
            if (Directory.Exists(extraRoot))
                Directory.Delete(extraRoot, recursive: true);
        }
    }
}
