using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Характеризующие (golden) тесты сборки промпта хода — фундамент под шину событий хода
// (ADR-013 следующим пунктом). Пишутся ДО рефакторинга ClaudeSession/FallbackLlmSessionAdapter,
// фиксируют байтовое поведение TurnPromptAssembler.Combine и порядок склейки секций.
//
// Паттерн — тот же CapturingLauncher, что в ClaudeSessionPromptSectionsOrderTests: запускаем
// настоящий ClaudeSession с фейковым launcher, дожидаемся args старта процесса, читаем
// фактический аргумент --append-system-prompt, который ушёл бы модели. Никаких правок
// ClaudeSession.cs: вся сборка секций внутри RunTurnAsync приватная, и единственный честный
// шов — захват args при старте.
//
// Маркеры МАРКЕР_* живут в поддельных провайдерах (RecallProvider/PersonaPromptProvider/…):
// они не зависят от встроенных текстов, рефакторинг сборки их не трогает, и тест ловит
// тихую потерю секции или смену порядка. Встроенные тексты VoicePrompts.* проверяем точечно
// — это публичный контракт промптового слоя.
public class TurnPromptGoldenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-prompt-golden-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<int, Process> _clis = new();
    private readonly TaskCompletionSource<IReadOnlyList<string>> _argsCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TurnPromptGoldenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var p in _clis.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Захватывает args первого старта процесса, дальше держит фейковый CLI живым (молчуном) —
    // ход сам по себе тесту не нужен, только фактический --append-system-prompt.
    private sealed class CapturingLauncher(
        ConcurrentDictionary<int, Process> clis,
        TaskCompletionSource<IReadOnlyList<string>> argsCaptured) : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            argsCaptured.TrySetResult(spec.Args);
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 10 127.0.0.1 >nul"]
                    : ["-c", "sleep 10"],
                WorkingDirectory = spec.WorkingDirectory,
                ClearEnv = spec.ClearEnv,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            clis[clis.Count + 1] = process;
            return process;
        }

                public int EstimateCommandLineLength(ProcessSpec spec)
        {
            // Заглушка для фейков: тесты, которые гоняют ClaudeSession.ApplyBudget,
            // нуждаются в числовом ответе, но не в точной семантике раннера (её
            // проверяет DockerProcessRunnerCmdlineEstimationTests на реальном раннере).
            // Считаем FileName + args через TurnPromptAssembler.ArgCost — та же формула,
            // что в LocalProcessRunner.EstimateCommandLineLength, без RawArguments.
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }
        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    // Общий helper: поднимает ClaudeSession, гоняет один ход, возвращает собранный промпт.
    // Контекст приходит уже с проставленным CapturingLauncher — иначе ClaudeSession возьмёт
    // дефолтный LocalProcessRunner.Instance и стартанёт реальный claude.exe, которого нет.
    private async Task<string> RunTurnCapturePromptAsync(Session info, LlmSessionContext context)
    {
        context.Launcher.Should().BeOfType<CapturingLauncher>(
            "контекст golden-теста обязан идти с CapturingLauncher — иначе ClaudeSession стартанёт настоящий CLI");

        var session = new ClaudeSession(info, context);
        await using var _ = session;

        await session.SendMessageAsync("привет");

        var args = await WhenAnyAsync(_argsCaptured.Task, TimeSpan.FromSeconds(15));
        var idx = args.ToList().IndexOf("--append-system-prompt");
        idx.Should().BeGreaterThanOrEqualTo(0,
            "все подмены непустые — аргумент --append-system-prompt обязан присутствовать");
        return args[idx + 1];
    }

    private static async Task<IReadOnlyList<string>> WhenAnyAsync(
        Task<IReadOnlyList<string>> task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        done.Should().Be(task, "не дождались старта fake-CLI процесса с захватом args хода");
        return await task;
    }

    // Маркеры — уникальные строки, которые не пересекаются ни с одним встроенным текстом и
    // при этом читаются глазом в дампе промпта. Эти строки — единственное, что зависит от
    // наших поддельных провайдеров; встроенный текст сборки промпта их не использует.
    private const string M_RECALL_NOTES = "МАРКЕР_RECALL_NOTES";
    private const string M_RECALL_MEMORY = "МАРКЕР_RECALL_MEMORY";
    private const string M_DOSSIER_RECALL = "МАРКЕР_DOSSIER_RECALL";
    private const string M_PERSONA_LAYER = "МАРКЕР_PERSONA_LAYER";
    private const string M_PROMPT_SECTIONS = "МАРКЕР_PROMPT_SECTIONS";
    private const string M_DOSSIER_TRAILER = "МАРКЕР_DOSSIER_TRAILER";

    // ---------- 1. Проектный чат, обычный режим ----------

    [Fact]
    public async Task ПроектныйЧат_ОбычныйРежим_СклейкаПоКонтракту()
    {
        var info = new Session { ProjectId = "proj-1", OwnerId = "owner-1" };
        var context = BuildBaseContext(
            info,
            projectMcpProjectId: "proj-1",
            withRecallProvider: true,
            withPersonaRecallProvider: true,
            withDossierTrailer: true,
            withPromptSectionsProvider: true,
            dossierToolsEnabled: false,
            personaLayerText: $"{M_PERSONA_LAYER} Ты — Тестовая Персона");

        var prompt = await RunTurnCapturePromptAsync(info, context);

        // Сборка прошла, --append-system-prompt не пустой
        prompt.Should().NotBeNullOrWhiteSpace();

        // Маркеры секций recall присутствуют — оба провайдера подключены
        prompt.Should().Contain(M_RECALL_NOTES, "NotesMcp подключён — секция recall-notes обязана быть");
        prompt.Should().Contain(M_RECALL_MEMORY, "MemoryMcp подключён — секция recall-memory обязана быть");

        // Трейлер истории решений — только у проектного чата
        prompt.Should().Contain(M_DOSSIER_TRAILER, "DossierTrailerHint задан у проектного чата");

        // Секция voice-mode в обычном режиме: heard=true → LongAnswerSectionText
        prompt.Should().Contain(VoicePrompts.LongAnswerSectionText,
            "обычный режим без озвучки просит выдержку у длинных ответов (heard=true)");
        prompt.Should().NotContain(VoicePrompts.DigestSectionText,
            "digest не активен — DigestSectionText в промпте быть не должно");
        prompt.Should().NotContain(VoicePrompts.PersonaOverride,
            "talk-оговорка персонал-уровня добавляется только при VoiceMode=true (тут false)");

        // Порядок: dossier-trailer → recall-notes → recall-memory → prompt-sections → persona-layer
        int Pos(string marker)
        {
            var p = prompt.IndexOf(marker, StringComparison.Ordinal);
            p.Should().BeGreaterThanOrEqualTo(0, $"маркер «{marker}» обязан попасть в промпт");
            return p;
        }

        var trailerIdx = Pos(M_DOSSIER_TRAILER);
        var recallNotesIdx = Pos(M_RECALL_NOTES);
        var recallMemoryIdx = Pos(M_RECALL_MEMORY);
        var sectionsIdx = Pos(M_PROMPT_SECTIONS);
        var personaIdx = Pos(M_PERSONA_LAYER);

        trailerIdx.Should().BeLessThan(recallNotesIdx,
            "dossier-trailer → recall-notes: трейлер истории идёт до блока recall");
        recallNotesIdx.Should().BeLessThan(recallMemoryIdx,
            "recall-notes → recall-memory: блоки recall собраны в порядке проводки");
        recallMemoryIdx.Should().BeLessThan(sectionsIdx,
            "recall-memory → prompt-sections: секции специальности после recall");
        sectionsIdx.Should().BeLessThan(personaIdx,
            "слой персоны всегда последним (Combine клеит persona layer после всех секций)");
    }

    // ---------- 2. Личный чат вне проекта ----------

    [Fact]
    public async Task ЛичныйЧатВнеПроекта_СклейкаПоКонтракту()
    {
        var info = new Session { ProjectId = null, OwnerId = "owner-1" };
        var context = BuildBaseContext(
            info,
            projectMcpProjectId: null, // личный чат → личный vault / личные задачи
            withRecallProvider: true,
            withPersonaRecallProvider: true,
            withDossierTrailer: false, // трейлера вне проекта нет
            withPromptSectionsProvider: false,
            dossierToolsEnabled: false,
            personaLayerText: $"{M_PERSONA_LAYER} Ты — Личная Персона");

        var prompt = await RunTurnCapturePromptAsync(info, context);

        prompt.Should().NotBeNullOrWhiteSpace();

        // Провайдеры подключены — секции recall есть
        prompt.Should().Contain(M_RECALL_NOTES, "NotesMcp подключён с ProjectId=null — секция recall-notes есть");
        prompt.Should().Contain(M_RECALL_MEMORY, "MemoryMcp подключён — секция recall-memory есть");

        // Вне проекта — трейлера истории решений нет
        prompt.Should().NotContain(M_DOSSIER_TRAILER,
            "вне проекта DossierTrailerHint = null — трейлер в промпт не идёт");

        // Подсказка про показ картинок — тоже только у проекта (Info.ProjectId is not null)
        prompt.Should().NotContain("от корня проекта через /",
            "вне проекта подсказка про показ картинок не добавляется");

        // Обычный режим чата без озвучки — выдержка у длинных остаётся
        prompt.Should().Contain(VoicePrompts.LongAnswerSectionText);
        prompt.Should().NotContain(VoicePrompts.DigestSectionText);

        // Порядок: recall-notes → recall-memory → persona-layer (без трейлера и секций специальности)
        int Pos(string marker)
        {
            var p = prompt.IndexOf(marker, StringComparison.Ordinal);
            p.Should().BeGreaterThanOrEqualTo(0, $"маркер «{marker}» обязан попасть в промпт");
            return p;
        }

        Pos(M_RECALL_NOTES).Should().BeLessThan(Pos(M_RECALL_MEMORY),
            "recall-notes → recall-memory");
        Pos(M_RECALL_MEMORY).Should().BeLessThan(Pos(M_PERSONA_LAYER),
            "слой персоны последним");
    }

    // ---------- 3. Voice-mode digest: оговорка в слое персоны последняя ----------

    [Fact]
    public async Task VoiceMode_Digest_ОговоркаВСлоеПерсоныПоследняя()
    {
        var info = new Session { ProjectId = "proj-1", VoiceMode = true, VoiceStyle = VoiceStyles.Digest };
        // PersonaPromptBuilder.Build с voiceMode=true, voiceStyle="digest" дописывает
        // DigestPersonaOverride в самый конец слоя персоны; эмулируем ровно эту склейку.
        var personaLayer = $"{M_PERSONA_LAYER} Ты — Тестовая Персона\n\n{VoicePrompts.DigestPersonaOverride}";

        var context = BuildBaseContext(
            info,
            projectMcpProjectId: "proj-1",
            withRecallProvider: false,
            withPersonaRecallProvider: false,
            withDossierTrailer: false,
            withPromptSectionsProvider: false,
            dossierToolsEnabled: false,
            personaLayerText: personaLayer);

        var prompt = await RunTurnCapturePromptAsync(info, context);

        // Секция voice-mode в режиме digest: DigestSectionText
        prompt.Should().Contain(VoicePrompts.DigestSectionText,
            "voice-mode digest активен — секция формата озвучки DigestSectionText обязана быть");
        prompt.Should().Contain(VoicePrompts.DigestPersonaOverride,
            "оговорка digest в слое персоны дописывается PersonaPromptBuilder'ом и едет через --append-system-prompt");

        // Взаимоисключающие секции формата
        prompt.Should().NotContain(VoicePrompts.LongAnswerSectionText,
            "digest активен — обычная выдержка не нужна (heard=true, voiceMode=true, digest=true → DigestSectionText, не LongAnswer)");
        prompt.Should().NotContain(VoicePrompts.PersonaOverride,
            "PersonaOverride — talk-оговорка, digest использует DigestPersonaOverride");

        // Порядок: секция voice-mode идёт ДО слоя персоны (все секции склеиваются до persona layer)
        int Pos(string marker)
        {
            var p = prompt.IndexOf(marker, StringComparison.Ordinal);
            p.Should().BeGreaterThanOrEqualTo(0, $"маркер «{marker}» обязан попасть в промпт");
            return p;
        }

        var digestSectionIdx = Pos(VoicePrompts.DigestSectionText);
        var personaLayerIdx = Pos(M_PERSONA_LAYER);
        var digestOverrideIdx = Pos(VoicePrompts.DigestPersonaOverride);

        digestSectionIdx.Should().BeLessThan(personaLayerIdx,
            "секция voice-mode — обычная секция промпта, клеится ДО слоя персоны");

        personaLayerIdx.Should().BeLessThan(digestOverrideIdx,
            "DigestPersonaOverride дописан в конец слоя персоны, идёт ПОСЛЕ идентичности персоны");

        // Главный инвариант фичи: оговорка digest — последний непустой текст всего промпта хода
        var tail = prompt[(digestOverrideIdx + VoicePrompts.DigestPersonaOverride.Length)..];
        tail.Trim().Should().BeEmpty(
            "оговорка digest обязана быть последним текстом всего --append-system-prompt");
    }

    // ---------- 4. change-dossiers-recall: отдельная секция dossier-recall ----------

    [Fact]
    public async Task ChangeDossiersRecall_Включен_ОтдельнаяСекцияDossierRecall()
    {
        var info = new Session { ProjectId = "proj-1", OwnerId = "owner-1" };

        var context = BuildBaseContext(
            info,
            projectMcpProjectId: "proj-1",
            withRecallProvider: false,
            withPersonaRecallProvider: true,
            withDossierTrailer: false,
            withPromptSectionsProvider: false,
            dossierToolsEnabled: true, // флаг change-dossiers-recall включён
            personaLayerText: $"{M_PERSONA_LAYER} Ты — Персона");

        // Этап 2: контракт «досье отдельно от recall» теперь раздаёт BuildBaseContext: при
        // dossierToolsEnabled=true мок-контрибьютор PersonaRecall эмитит обе секции
        // (recall-memory + dossier-recall) без отдельной подмены контекста.
        var prompt = await RunTurnCapturePromptAsync(info, context);

        // Отдельная секция dossier-recall обязана быть — это контракт плана «Секции промптов»
        prompt.Should().Contain(M_DOSSIER_RECALL,
            "MemoryMcp.DossierToolsEnabled=true + persona-recall с DossierText → отдельная секция dossier-recall");

        // Recall-memory тоже едет (это независимая секция)
        prompt.Should().Contain(M_RECALL_MEMORY,
            "recall-memory едет своей секцией — это не та же, что dossier-recall");

        // Порядок: recall-memory идёт раньше dossier-recall (сборка: см. ClaudeSession.cs:2802 → 2828)
        int Pos(string marker)
        {
            var p = prompt.IndexOf(marker, StringComparison.Ordinal);
            p.Should().BeGreaterThanOrEqualTo(0, $"маркер «{marker}» обязан попасть в промпт");
            return p;
        }

        Pos(M_RECALL_MEMORY).Should().BeLessThan(Pos(M_DOSSIER_RECALL),
            "recall-memory → dossier-recall: порядок проводки в RunTurnAsync");
    }

    // ---------- 5. Дыра: контекст выключен → секция отсутствует, не падает ----------

    // ClaudeSession.cs:2632 — _recallProvider вызывается только при _notesMcp is not null.
    // ClaudeSession.cs:2798 — _personaRecallProvider вызывается только при _memoryMcp is not null.
    // Без этих тестов рефакторинг шины мог бы тихо перенести вызов наружу if и ронять ход
    // NPE на каждом сеансе без подключённого notes/memory сервера.

    [Fact]
    public async Task RecallProviderЗаданНоNotesMcpОтсутствует_СекцииRecallNotesНет()
    {
        var info = new Session { ProjectId = "proj-1", OwnerId = "owner-1" };

        // Базовый контекст с NotesMcp=null, но с подключённым MemoryMcp+PersonaRecallProvider
        var context = BuildBaseContext(
            info,
            projectMcpProjectId: "proj-1",
            withRecallProvider: true,    // провайдер задан, но…
            withPersonaRecallProvider: true,
            withDossierTrailer: false,
            withPromptSectionsProvider: false,
            dossierToolsEnabled: false,
            personaLayerText: $"{M_PERSONA_LAYER} Ты — Персона");

        // …выключаем NotesMcp: подменяем контекст, чтобы notes-сервер был null.
        // RecallProvider остаётся в контексте — но гейт _notesMcp is not null его не пустит.
        context = context with { NotesMcp = null };

        var prompt = await RunTurnCapturePromptAsync(info, context);

        prompt.Should().NotContain(M_RECALL_NOTES,
            "NotesMcp=null → секция recall-notes НЕ добавляется, даже если RecallProvider задан (ClaudeSession.cs:2632)");
        prompt.Should().Contain(M_RECALL_MEMORY,
            "MemoryMcp подключён → секция recall-memory добавляется независимо от notes");
    }

    [Fact]
    public async Task PersonaRecallProviderЗаданНоMemoryMcpОтсутствует_СекцииRecallMemoryНет()
    {
        var info = new Session { ProjectId = "proj-1", OwnerId = "owner-1" };

        var context = BuildBaseContext(
            info,
            projectMcpProjectId: "proj-1",
            withRecallProvider: true,
            withPersonaRecallProvider: true, // провайдер задан, но…
            withDossierTrailer: false,
            withPromptSectionsProvider: false,
            dossierToolsEnabled: false,
            personaLayerText: $"{M_PERSONA_LAYER} Ты — Персона");

        // …выключаем MemoryMcp — persona-recall не должен зваться.
        context = context with { MemoryMcp = null };

        var prompt = await RunTurnCapturePromptAsync(info, context);

        prompt.Should().NotContain(M_RECALL_MEMORY,
            "MemoryMcp=null → секция recall-memory НЕ добавляется, даже если PersonaRecallProvider задан (ClaudeSession.cs:2798)");
        prompt.Should().Contain(M_RECALL_NOTES,
            "NotesMcp подключён → секция recall-notes добавляется независимо от memory");

        // Параллельно: dossier-recall тоже не должен появиться — он часть persona-recall,
        // без MemoryMcp проводки нет
        prompt.Should().NotContain(M_DOSSIER_RECALL,
            "dossier-recall — производная от persona-recall, без MemoryMcp его не должно быть");
    }

    // ===== Builders =====

    // Конструктор контекста с минимальным набором MCP-контекстов, нужным для golden-тестов:
    // tasks/notes/memory/personas/workspace/widgets/code-graph подключены так, чтобы их
    // подсказки присутствовали в снимке промпта (это часть байтового поведения, которое
    // мы фиксируем). Конкретные тексты подсказок не проверяем — они могут косметически
    // меняться, контракт «секция присутствует» достаточно.
    //
    // Этап 2: 6 провайдеров LlmSessionContext и DossierTrailerHint заменены реестром
    // IPromptSectionContributor — собираем его на TurnEventBus, шину прокидываем через
    // LlmSessionContext.Events. Каждый контрибьютор — мок-подписчик Filter-события
    // prompt/assembling, кладёт секцию с маркером (или не кладёт — гейт IsEnabled).
    // Гейт IsEnabled повторяет прежнее «_notesMcp is not null» / «_memoryMcp is not null» —
    // его потерю ловят golden-фикстуры 5/6.
    // Инстансный (не static) — нужно подсунуть CapturingLauncher текущего инстанса теста,
    // иначе ClaudeSession стартанёт настоящий claude.exe.
    private LlmSessionContext BuildBaseContext(
        Session info,
        string? projectMcpProjectId,
        bool withRecallProvider,
        bool withPersonaRecallProvider,
        bool withDossierTrailer,
        bool withPromptSectionsProvider,
        bool dossierToolsEnabled,
        string personaLayerText)
    {
        var hasPersonaPrompt = true; // persona-layer всегда строится

        var bus = new TurnEventBus();

        // DossierTrailerContributor (Order=100): гейт ownerId != null && ProjectId != null.
        // В тесте ProjectId приходит через info, а владелец — owner-1 или null.
        bus.OnFilter<PromptAssembling>(100, (e, next) =>
        {
            if (withDossierTrailer && e.Session.OwnerId is not null && e.Session.Session.ProjectId is not null)
                e.Sections.Add(new PromptSection("dossier-trailer", M_DOSSIER_TRAILER));
            return next();
        }, "Test.DossierTrailer");

        // NotesRecallContributor (Order=200): гейт NotesMcp != null && OwnerId != null.
        bus.OnFilter<PromptAssembling>(200, async (e, next) =>
        {
            if (withRecallProvider && e.Session.HasNotesMcp && e.Session.OwnerId is not null)
                e.Sections.Add(new PromptSection("recall-notes", M_RECALL_NOTES));
            await next();
        }, "Test.NotesRecall");

        // PersonaRecallContributor (Order=300): гейт MemoryMcp != null && Persona != null.
        // Эмитит recall-memory + (при splitDossier) dossier-recall.
        bus.OnFilter<PromptAssembling>(300, async (e, next) =>
        {
            if (withPersonaRecallProvider && e.Session.HasMemoryMcp && e.Session.Persona is not null)
            {
                e.Sections.Add(new PromptSection("recall-memory", M_RECALL_MEMORY));
                if (dossierToolsEnabled)
                    e.Sections.Add(new PromptSection("dossier-recall", M_DOSSIER_RECALL));
            }
            await next();
        }, "Test.PersonaRecall");

        // PromptSectionsContributor (Order=400): гейт persona != null и т.д.
        bus.OnFilter<PromptAssembling>(400, async (e, next) =>
        {
            if (withPromptSectionsProvider && e.Session.Persona is not null)
                e.Sections.Add(new PromptSection("prompt-sections", M_PROMPT_SECTIONS));
            await next();
        }, "Test.PromptSections");

        // PersonaBindingsContributor (Order=500): гейт persona != null.
        bus.OnFilter<PromptAssembling>(500, async (e, next) =>
        {
            if (hasPersonaPrompt && e.Session.Persona is not null)
                e.Sections.Add(new PromptSection("persona-bindings", "привязки"));
            await next();
        }, "Test.PersonaBindings");

        // CodeGraphContributor (Order=600): гейт workspace != null.
        bus.OnFilter<PromptAssembling>(600, async (e, next) =>
        {
            if (e.Session.HasWorkspaceMcp)
            {
                e.Sections.Add(new PromptSection("code-graph", "graph-slice"));
                e.Sections.Add(new PromptSection(
                    "code-navigation", CodeNavigationPrompts.SectionText));
            }
            await next();
        }, "Test.CodeGraph");

        // PersonaLayerContributor (Order=900): всегда строит persona-layer при persona != null.
        bus.OnFilter<PromptAssembling>(900, async (e, next) =>
        {
            if (hasPersonaPrompt)
                e.Sections.Add(new PromptSection("persona-layer", personaLayerText));
            await next();
        }, "Test.PersonaLayer");

        return new LlmSessionContext(
            RootPath: _root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            PermissionRules: null,
            TasksMcp: projectMcpProjectId is null
                ? new TasksMcpContext("http://tasks.invalid", () => "tok", ProjectId: null)
                : new TasksMcpContext("http://tasks.invalid", () => "tok", ProjectId: projectMcpProjectId),
            NotesMcp: projectMcpProjectId is null
                ? new NotesMcpContext("http://notes.invalid", () => "tok", ProjectId: null)
                : new NotesMcpContext("http://notes.invalid", () => "tok", ProjectId: projectMcpProjectId),
            MemoryMcp: projectMcpProjectId is null
                ? new MemoryMcpContext("http://memory.invalid", () => "tok", PersonaId: "persona-1", ProjectId: null,
                    DossierToolsEnabled: dossierToolsEnabled)
                : new MemoryMcpContext("http://memory.invalid", () => "tok", PersonaId: "persona-1",
                    ProjectId: projectMcpProjectId, DossierToolsEnabled: dossierToolsEnabled),
            // Этап 2: PersonaProvider нужен ClaudeSession для PromptSessionContext.Persona —
            // гейты IsEnabled PersonaRecall/PersonaBindings/PromptSections/PersonaLayer читают
            // его, без него секции персоны не поедут. Тест эмулирует живую персону через
            // стаба без ID, чтобы пройти контрактную проверку.
            PersonaProvider: () => new Persona { Id = "persona-1", Name = "Тестовая Персона" },
            PersonasMcp: new PersonasMcpContext("http://personas.invalid", () => "tok",
                ProjectId: projectMcpProjectId),
            NotificationsMcp: new NotificationsMcpContext("http://notifications.invalid", () => "tok"),
            WorkspaceMcp: new WorkspaceMcpContext("http://workspace.invalid", () => "tok",
                ProjectId: projectMcpProjectId,
                Sections: ["files", "chats", "git"]),
            WidgetsMcp: new WidgetsMcpContext("http://widgets.invalid", () => "tok", UseHttp: true),
            CodeGraphMcp: new CodeGraphMcpContext("http://codegraph.invalid", () => "tok",
                ProjectId: projectMcpProjectId ?? "proj-1"),
            // CapturingLauncher ОБЯЗАН идти отсюда — иначе ClaudeSession стартанёт настоящий claude.exe
            Launcher: new CapturingLauncher(_clis, _argsCaptured),
            Events: bus);
    }
}
