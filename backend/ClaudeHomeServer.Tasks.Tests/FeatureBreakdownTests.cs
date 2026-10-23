using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты разрезов времени фичи (волна 3 «Обзор фич», вкладка «Статистика», Б6):
// FeatureBreakdown.Build — чистая проекция FeatureRun → (FeatureBreakdown, FeatureLongestNow).
// Время передаётся параметром (now) — детерминизм. Мутация Build красит кейсы.
//
// Покрытие:
//  • персоны/роли/модели — объединение интервалов (параллельные задачи одной группы не складываются);
//  • ChatIds — только из снимка (чужой чат не попал);
//  • модель — из журнала при полном журнале, из снимка без него;
//  • «Авто» → полное имя слота (через modelResolver, не сырое «Авто»);
//  • LongestNow — самая долгая идущая, null когда ничего не идёт;
//  • сортировка групп по Minutes убыв.
public class FeatureBreakdownTests
{
    // ── Хелперы сборки ──────────────────────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0);

    // Эпик фичи (role=feature, FeatureId=null).
    private static TaskItem Epic(string id) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        Status = TaskItemStatus.Todo,
        CreatedAt = T0,
    };

    // Дочерняя задача фичи (FeatureId = epicId).
    private static TaskItem Child(string id, string epicId, string? personaId = null,
        string? role = TaskRoles.Step, string? stepRef = null,
        DateTime? startedAt = null, DateTime? completedAt = null, string? chatId = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        PersonaId = personaId,
        Role = role,
        StepRef = stepRef,
        Status = completedAt is not null ? TaskItemStatus.Done : TaskItemStatus.InProgress,
        CreatedAt = T0,
        ClaudeStartedAt = startedAt,
        CompletedAt = completedAt,
        LinkedSessionId = chatId,
    };

    // FeatureRun (ПОЛНЫЙ, необлегщённый) из эпика + дочерних.
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // Снимок сессий: чат → (id, taskId, model).
    private static List<FeatureIndexSession> Sessions(params (string Id, string? TaskId, string? Model)[] chats) =>
        chats.Select(c => new FeatureIndexSession(c.Id, c.TaskId, null, false, c.Model)).ToList();

    // modelResolver «как в Б4»: явная модель → её полное имя; «Авто» (Model null) → слот.
    // Здесь детерминированно: «Авто» → "Claude Opus (слот «сильная»)", явная → "Claude {model}".
    private static Func<FeatureIndexSession, (string? Label, string? Full)> AutoResolver() =>
        s => string.IsNullOrWhiteSpace(s.Model)
            ? ("Авто", "Claude Opus (слот «сильная»)")
            : (s.Model, $"Claude {s.Model}");

    // Журнал: полный (StartsWithCreated && !Truncated) с turn.model для чата.
    private static FeatureJournalReadResult CompleteJournal(params (string ChatId, string Model)[] turns) =>
        new()
        {
            StartsWithCreated = true,
            Truncated = false,
            Entries = turns
                .Select(t => new FeatureJournalEntry
                {
                    Kind = "turn",
                    SessionId = t.ChatId,
                    Model = t.Model,
                })
                .ToList(),
        };

    // ── 1. Параллельные задачи одной персоны: объединение, не сумма ─────────────────────────

    // Две задачи с одной PersonaId пересекаются: Minutes группы = объединение (6 ч), не сумма (8 ч).
    [Fact]
    public void Build_ПараллельныеЗадачиОднойПерсоны_ОбъединениеНеСумма()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: 10:00–14:00 (4 ч); s2: 12:00–16:00 (4 ч). Пересечение 12:00–14:00.
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));
        var s2 = Child("s2", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 16, 0, 0));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1, s2), null, Sessions(), null, now);

        // Группа персоны p1: объединение [10:00–16:00] = 6 ч = 360 мин (не 480 = сумма).
        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Minutes.Should().Be(360, "объединение интервалов, не сумма (сумма дала бы 480)");
    }

    // ── 2. ChatIds — только из снимка (чужой чат не попал) ───────────────────────────────────

    // Задача с чатом, которого НЕТ в снимке → её chatId не в ChatIds; задача с чатом из снимка → в
    // ChatIds. Мутация «убрать фильтр по снимку» → чужий чат попадёт → краснеет.
    [Fact]
    public void Build_ChatIds_ТолькоИзСнимка()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: чат "own" (в снимке); s2: чат "foreign" (НЕ в снимке).
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "own",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var s2 = Child("s2", "epic", personaId: "p1", chatId: "foreign",
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));

        var sessions = Sessions(("own", "s1", null)); // только "own"
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1, s2), null, sessions, null, now);

        var p1 = bd.Personas.Single(g => g.Key == "p1");
        // Только "own" (из снимка); "foreign" (чужой) не попал.
        p1.ChatIds.Should().BeEquivalentTo(new[] { "own" });
    }

    // ── 3. Модель: из журнала при полном журнале, из снимка без него ─────────────────────────

    // Полный журнал: model группы = turn.model из журнала (не из снимка).
    [Fact]
    public void Build_Модель_ИзЖурнала_ПриПолномЖурнале()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // Снимок: чат c1 с Model=null («Авто»). Журнал: полный, turn.model="claude-opus-4" для c1.
        var sessions = Sessions(("c1", "s1", null));
        var journal = CompleteJournal(("c1", "claude-opus-4"));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), journal, sessions, AutoResolver(), now);

        // Модель группы = из журнала: сырой id "claude-opus-4" резолвится в единый ключ
        // (modelFull, минор 6) — то же имя, что дал бы снимок с этой явной моделью.
        bd.Models.Should().ContainSingle(g => g.Key == "Claude claude-opus-4");
    }

    // Без журнала (null): model группы = из снимка через modelResolver (резолв «Авто»).
    [Fact]
    public void Build_Модель_ИзСнимка_БезЖурнала()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var sessions = Sessions(("c1", "s1", null)); // «Авто»
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, sessions, AutoResolver(), now);

        // Модель = резолв «Авто» через modelResolver (готовая функция Б4), не сырое «Авто».
        bd.Models.Should().ContainSingle(g => g.Key == "Claude Opus (слот «сильная»)");
    }

    // ── 4. «Авто» → полное имя слота (не сырое «Авто») ───────────────────────────────────────

    // Чат с Model=null («Авто»): modelResolver разворачивает в полное имя слота. Ключ группы —
    // полное имя, не «Авто» и не null.
    [Fact]
    public void Build_Авто_ПолноеИмяСлота()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var sessions = Sessions(("c1", "s1", null)); // «Авто»
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, sessions, AutoResolver(), now);

        // Ключ модели — полное имя слота; сырого «Авто»/null среди ключей нет.
        var keys = bd.Models.Select(g => g.Key).ToList();
        keys.Should().NotContain("Авто");
        keys.Should().NotContain((string?)null);
        keys.Should().Contain("Claude Opus (слот «сильная»)");
    }

    // ── 5. LongestNow: самая долгая идущая, null когда ничего не идёт ─────────────────────────

    // Две идущие задачи (ClaudeStartedAt, без CompletedAt): LongestNow — та, что стартовала раньше.
    [Fact]
    public void Build_LongestNow_СамаяДолгаяИдущая()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: старт 10:00 (идёт 10 ч); s2: старт 18:00 (идёт 2 ч).
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1", stepRef: "Ш1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0)); // без CompletedAt → идёт
        var s2 = Child("s2", "epic", personaId: "p2",
            startedAt: new DateTime(2026, 10, 1, 18, 0, 0)); // без CompletedAt → идёт

        var sessions = Sessions(("c1", "s1", null));
        var (_, longest) = FeatureBreakdown.Build(Run(epic, s1, s2), null, sessions, null, now);

        longest.Should().NotBeNull();
        longest!.TaskId.Should().Be("s1", "самая долгая — стартовала раньше");
        longest.PersonaId.Should().Be("p1");
        longest.StepRef.Should().Be("Ш1");
        longest.Minutes.Should().Be(600, "now(20:00) − старт(10:00) = 10 ч = 600 мин");
        longest.ChatId.Should().Be("c1", "чат из снимка (fail-closed)");
        longest.Tokens.Should().BeNull(); // до Б9
        longest.Share.Should().BeNull(); // до Б9
    }

    // Ничего не идёт (все задачи закрыты) → LongestNow = null.
    [Fact]
    public void Build_LongestNow_НичегоНеИдёт_Null()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // Задача закрыта (CompletedAt задан) → не «идёт».
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var (_, longest) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now);

        longest.Should().BeNull();
    }

    // ── 5c. (минор 6) LongestNow: ждущая/остановленная (InProgress) не попадают ───────────────

    // Задача InProgress, но ждёт человека (AwaitingHumanSince) или остановлена (ExecutorStoppedAt) —
    // статусом «идёт», но реально не работает → в «дольше всех сейчас» не попадает. Мутация «снять
    // фильтр» (FeatureBreakdown.cs:194) → s2 (стартовала 08:00, раньше всех) стала бы LongestNow →
    // тест (null) краснеет.
    [Fact]
    public void Build_LongestNow_ЖдущаяИОстановленная_НеПопадают()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: InProgress, ждёт человека (AwaitingHumanSince) → не «идёт».
        var s1 = new TaskItem
        {
            Id = "s1", FeatureId = "epic", PersonaId = "p1", Role = TaskRoles.Step,
            Status = TaskItemStatus.InProgress, CreatedAt = T0,
            ClaudeStartedAt = new DateTime(2026, 10, 1, 9, 0, 0),
            AwaitingHumanSince = new DateTime(2026, 10, 1, 15, 0, 0),
        };
        // s2: InProgress, остановлена (ExecutorStoppedAt) → не «идёт».
        var s2 = new TaskItem
        {
            Id = "s2", FeatureId = "epic", PersonaId = "p2", Role = TaskRoles.Step,
            Status = TaskItemStatus.InProgress, CreatedAt = T0,
            ClaudeStartedAt = new DateTime(2026, 10, 1, 8, 0, 0),
            ExecutorStoppedAt = new DateTime(2026, 10, 1, 16, 0, 0),
        };

        var (_, longest) = FeatureBreakdown.Build(Run(epic, s1, s2), null, Sessions(), null, now);

        // Ни s1 (ждёт), ни s2 (остановлена) не «идут» → LongestNow = null.
        longest.Should().BeNull();
    }

    // ── 6. Сортировка групп по Minutes убыв ───────────────────────────────────────────────────

    // Три персоны с разным временем: группы отсортированы по Minutes убыв.
    [Fact]
    public void Build_СортировкаПоМинутамУбыв()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // p1: 2 ч (120 мин); p2: 4 ч (240 мин); p3: 1 ч (60 мин).
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var s2 = Child("s2", "epic", personaId: "p2",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));
        var s3 = Child("s3", "epic", personaId: "p3",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 11, 0, 0));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1, s2, s3), null, Sessions(), null, now);

        // Порядок: p2 (240) → p1 (120) → p3 (60).
        bd.Personas.Select(g => g.Key).ToList().Should().Equal(new[] { "p2", "p1", "p3" });
        bd.Personas[0].Minutes.Should().Be(240);
        bd.Personas[1].Minutes.Should().Be(120);
        bd.Personas[2].Minutes.Should().Be(60);
    }

    // ── 7. Роли: группировка по TaskItem.Role (сырой) ─────────────────────────────────────────

    // Задачи разных ролей: группы ролей по сырому Role.
    [Fact]
    public void Build_Роли_ПоСыромуRole()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var step = Child("s1", "epic", personaId: "p1", role: TaskRoles.Step,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var review = Child("r1", "epic", personaId: "p2", role: TaskRoles.Review,
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 13, 0, 0));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, step, review), null, Sessions(), null, now);

        // Группы ролей: step (120 мин) и review (60 мин).
        bd.Roles.Should().Contain(g => g.Key == TaskRoles.Step);
        bd.Roles.Should().Contain(g => g.Key == TaskRoles.Review);
        bd.Roles.Single(g => g.Key == TaskRoles.Step).Minutes.Should().Be(120);
        bd.Roles.Single(g => g.Key == TaskRoles.Review).Minutes.Should().Be(60);
    }

    // ── Мутации ───────────────────────────────────────────────────────────────────────────────

    // Мутация 1: брать модель из снимка, даже когда журнал полный → кейс 3a (модель из журнала)
    // краснеет. Здесь проверяем обратное: при полном журнале модель ИМЕННО из журнала.
    // (Кейс 3a уже утверждает это; мутация «всегда из снимка» его красит.)
    [Fact]
    public void Build_Мутация_МодельВсегдаИзСнимка_Краснеет()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // Снимок: c1 с ЯВНОЙ моделью "claude-sonnet-4" (не «Авто»). Журнал: полный,
        // turn.model="claude-opus-4". Единый ключ (modelFull, минор 6): сырой id резолвится в
        // то же имя, что снимок с этой моделью. Из журнала → "Claude claude-opus-4"; если бы
        // брали из снимка → "Claude claude-sonnet-4".
        var sessions = Sessions(("c1", "s1", "claude-sonnet-4"));
        var journal = CompleteJournal(("c1", "claude-opus-4"));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), journal, sessions, AutoResolver(), now);

        // Полный журнал → модель из журнала (opus), не из снимка (sonnet) — единый ключ.
        bd.Models.Should().ContainSingle(g => g.Key == "Claude claude-opus-4");
        bd.Models.Should().NotContain(g => g.Key == "Claude claude-sonnet-4");
    }

    // Мутация 2: убрать фильтр по снимку в ChatIds → чужой чат попадёт → кейс 2 краснеет.
    // (Кейс 2 уже утверждает «только из снимка»; мутация «все chatId» его красит.)
    [Fact]
    public void Build_Мутация_ChatIdsБезФильтра_Краснеет()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: чат "own" (в снимке); s2: чат "foreign" (НЕ в снимке).
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "own",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var s2 = Child("s2", "epic", personaId: "p1", chatId: "foreign",
            startedAt: new DateTime(2026, 10, 1, 12, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 14, 0, 0));

        var sessions = Sessions(("own", "s1", null));
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1, s2), null, sessions, null, now);

        // Фильтр по снимку: только "own". Мутация «без фильтра» дала бы ["foreign","own"].
        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.ChatIds.Should().Equal("own");
    }

    // ── Б9b: токены/стоимость групп и LongestNow из записей расхода ─────────────────────────

    // Запись расхода (шов ISpendFeatureReader): токены (вход/выход), стоимость, источник.
    // unreliable — признак «цена от CLI ненадёжна» (модель в списке UnreliableCostModels):
    // его выставляет читатель (SpendFeatureReader) единой точкой правды (SpendCostReliability).
    private static FeatureSpendRecord Spend(string? personaId, string? taskId, string? model,
        long input, long output, double? cost, string source = "chat-turn", bool unreliable = false) => new()
    {
        PersonaId = personaId,
        TaskId = taskId,
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CostUsd = cost,
        Source = source,
        UnreliableCost = unreliable,
    };

    // Группа персоны: токены (вход+выход) и стоимость (надёжная цена) из записей расхода.
    [Fact]
    public void Build_Группа_ТокеныИСтоимость_ИзЗаписей()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var records = new[]
        {
            Spend("p1", "s1", "claude-opus-4", 100, 50, 1.5),
            Spend("p1", "s1", "claude-opus-4", 200, 100, 2.0),
        };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Tokens.Should().Be(450, "сумма (вход+выход) двух записей: (100+50)+(200+100)");
        p1.CostUsd.Should().Be(3.5m, "сумма надёжных цен 1.5+2.0");
    }

    // Запись без надёжной цены (Source != free, CostUsd = null) → стоимость группы null,
    // токены на месте. Мутация «считать стоимость 0» → CostUsd = 0 ≠ null → краснеет.
    [Fact]
    public void Build_Группа_НенадёжнаяЦена_СтоимостьNull()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // Source=chat-turn (не free) + CostUsd=null → ненадёжная цена.
        var records = new[] { Spend("p1", "s1", "deepseek-chat", 100, 50, null) };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Tokens.Should().Be(150);
        p1.CostUsd.Should().BeNull("ненадёжная цена → стоимость группы null, не 0");
    }

    // Единая точка правды (ревью Глеба, major): модель из списка UnreliableCostModels, у которой
    // CLI отчитал НАДЁЖНО ВЫГЛЯДЯЩУЮ цену (deepseek cost=2.0), → стоимость группы null, не 2.0.
    // Прежний разрез определял «ненадёжно» по `CostUsd is null` → 2.0 шло в сумму, а итог
    // расхода (SpendFeatureReader, по имени модели) давал 0 — две цифры по одной записи.
    // Мутация «разрез снова по CostUsd is null» → costUsd = 2.0 ≠ null → краснеет.
    [Fact]
    public void Build_Группа_НенадёжнаяМодельСЦеной_СтоимостьNull()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // deepseek-chat (в списке ненадёжных) + цена 2.0 от CLI → признак UnreliableCost.
        var records = new[] { Spend("p1", "s1", "deepseek-chat", 100, 50, 2.0, unreliable: true) };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Tokens.Should().Be(150, "токены ненадёжной модели в сумме");
        p1.CostUsd.Should().BeNull("ненадёжная модель → стоимость группы null, не 2.0 (как в итоге расхода)");
    }

    // Локальная модель (Source=free) → стоимость честно 0 (не null).
    [Fact]
    public void Build_Группа_FreeМодель_СтоимостьНуль()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var records = new[] { Spend("p1", "s1", "llama-3", 100, 50, 0, source: "free") };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Tokens.Should().Be(150);
        p1.CostUsd.Should().Be(0m, "free → честный 0, не null");
    }

    // Ось «роли»: запись расхода (TaskId → Role задачи) даёт группе роли токены/стоимость.
    [Fact]
    public void Build_ГруппаРолей_ТокеныИСтоимость()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", role: TaskRoles.Step,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var records = new[] { Spend("p1", "s1", "claude-opus-4", 100, 50, 1.0) };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        var step = bd.Roles.Single(g => g.Key == TaskRoles.Step);
        step.Tokens.Should().Be(150);
        step.CostUsd.Should().Be(1.0m);
    }

    // LongestNow: токены задачи (вход+выход записей TaskId) и доля в суммарных токенах среза.
    [Fact]
    public void Build_LongestNow_ТокеныИДоля()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // s1: идёт (старт 10:00, без CompletedAt) → LongestNow.
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0));

        // Две записи: s1 (150 токенов) + s2 (50 токенов) → total = 200; доля s1 = 150/200 = 0.75.
        var records = new[]
        {
            Spend("p1", "s1", "claude-opus-4", 100, 50, 1.0),
            Spend("p2", "s2", "claude-opus-4", 30, 20, 0.5),
        };
        var (_, longest) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(("c1", "s1", null)), null, now, records);

        longest.Should().NotBeNull();
        longest!.Tokens.Should().Be(150, "вход+выход записей задачи s1");
        longest.Share.Should().Be(0.75, "150 / (150+50) = 0.75");
    }

    // LongestNow без записей расхода → токены/доля null (нет данных, не 0).
    [Fact]
    public void Build_LongestNow_БезЗаписей_ТокеныNull()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1", chatId: "c1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0));

        var (_, longest) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(("c1", "s1", null)), null, now);

        longest.Should().NotBeNull();
        longest!.Tokens.Should().BeNull("записей расхода нет → null, не 0");
        longest.Share.Should().BeNull();
    }

    // Минор Б9b (документированное правило): запись ключевого (оркестраторского) чата БЕЗ
    // TaskId/PersonaId/Model не резолвится ни в одну ось → входит в ИТОГ расхода (срез по
    // чату), но не в этот разрез. Сумма разрезов < итога — осознанно, не баг.
    // Мутация «класть безключевую запись в группу null/«прочее»» → сумма разрезов = 200 ≠ 150.
    [Fact]
    public void Build_ЗаписьБезКлючей_ВИтогНоНеВРазрез()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        var s1 = Child("s1", "epic", personaId: "p1",
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0),
            completedAt: new DateTime(2026, 10, 1, 12, 0, 0));

        // Нормальная запись (p1/s1, 150 токенов) + запись ключевого чата без ключей (50 токенов).
        var records = new[]
        {
            Spend("p1", "s1", "claude-opus-4", 100, 50, 1.0),
            Spend(null, null, null, 30, 20, 0.5),
        };
        var (bd, _) = FeatureBreakdown.Build(Run(epic, s1), null, Sessions(), null, now, records);

        // Группа p1 — только нормальная запись (150), безключевая не добавлена.
        var p1 = bd.Personas.Single(g => g.Key == "p1");
        p1.Tokens.Should().Be(150, "безключевая запись не в разрезе персон");
        // Сумма ВСЕХ групп персон = 150 (не 200): безключевая запись ни в одной группе.
        bd.Personas.Sum(g => g.Tokens ?? 0).Should().Be(150);
    }

    // ── W4-DES-B: дизайн-задача в разрезе по персонам ─────────────────────────────────────────

    // Идущая задача с ролью design попадает в разрез по персонам и по ролям: работа
    // дизайнера видна (первый возврат R7a: run.Design не попадал в выборку AllTasks).
    // Мутация «убрать Design из AllTasks» → групп «mayya» и «design» нет → тест краснеет.
    [Fact]
    public void Build_ИдущаяDesignЗадача_ВРазрезеПоПерсонамИРолям()
    {
        var now = new DateTime(2026, 10, 1, 20, 0, 0);
        var epic = Epic("epic");
        // d1: дизайнер Майя, идёт с 10:00 (до now — 10 ч).
        var d1 = Child("d1", "epic", personaId: "mayya", role: TaskRoles.Design,
            startedAt: new DateTime(2026, 10, 1, 10, 0, 0));

        var (bd, _) = FeatureBreakdown.Build(Run(epic, d1), null, Sessions(), null, now);

        var mayya = bd.Personas.Single(g => g.Key == "mayya");
        mayya.Minutes.Should().Be(600, "10 часов работы дизайнера в разрезе персоны");
        bd.Roles.Single(r => r.Key == "design").Minutes.Should().Be(600);
    }
}
