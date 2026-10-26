using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты документов фичи (волна 3 «Обзор фич», вкладка «Планы и макеты»):
// FeatureProjector.BuildPlans — чистая проекция FeatureRun → List<FeaturePlan> БЕЗ хранилища.
// Покрытие: порядок (роль cut → design → plan → consult, внутри роли по CreatedAt), роль как есть
// (step с заголовком «Разрез» не попадает), design в документах (М2: роль, hasText, mockupPaths,
// не дедуплицируется), hasText (пустой/пробельный ResultMarkdown → false),
// mockupPaths (путь с «..» отброшен, дубли схлопнуты, потолок 20). М5: mockupPaths несут и
// LinkedFiles задачи (ADR/спеки/записки вне docs/mockups): порядок (сначала текст, потом
// linkedFiles), дубли схлопнуты, пустые и «..» отброшены, потолок общий. Мутации:
//   • BuildPlans(SlimRun(run)) → у всех hasText=false (Slim обнуляет ResultMarkdown) — краснеет;
//   • убрать фильтр «..» → путь с «..» появляется — краснеет;
//   • не брать LinkedFiles (старый MockupPaths) — linkedFiles-тесты краснеют.
public class FeaturePlansTests
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

    // Дочерняя задача фичи (FeatureId = epicId). role/title/resultMarkdown/personaId/createdAt —
    // как есть; completedAt задаёт статус Done (иначе Todo); linkedFiles — приложенные файлы
    // (M5: доезжают в mockupPaths как приложены).
    private static TaskItem Child(string id, string epicId, string role, string title,
        string? resultMarkdown = null, string? personaId = null, string? stepRef = null,
        DateTime? createdAt = null, DateTime? completedAt = null, string[]? linkedFiles = null) => new()
    {
        Id = id,
        FeatureId = epicId,
        Role = role,
        Title = title,
        ResultMarkdown = resultMarkdown,
        PersonaId = personaId,
        StepRef = stepRef,
        Status = completedAt is null ? TaskItemStatus.Todo : TaskItemStatus.Done,
        CreatedAt = createdAt ?? T0,
        CompletedAt = completedAt,
        LinkedFiles = linkedFiles is null ? [] : [.. linkedFiles],
    };

    // FeatureRun из эпика + дочерних (чистое ядро BuildCore — ПОЛНЫЙ, необлегчённый run:
    // BuildPlans считает по нему ДО SlimRun, т.к. hasText/mockupPaths — из ResultMarkdown,
    // который Slim обнуляет).
    private static FeatureRun Run(TaskItem epic, params TaskItem[] children)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(children);
        return FeatureProjector.BuildCore(epic, all);
    }

    // ── 1. Порядок: роль (cut → plan → consult), внутри роли по CreatedAt ───────────────────

    // cut (c2 раньше, c1 позже) → plan → consult; внутри cut — по CreatedAt (c2, c1).
    // Порядок входа фикстуры ПРОТИВОРЕЧИТ порядку CreatedAt (c1 подаётся раньше c2):
    // без OrderBy внутри роли результат вышел бы «c1, c2» и тест покраснел.
    [Fact]
    public void BuildPlans_Порядок_Роль_И_ВнутриРолиПоCreatedAt()
    {
        var epic = Epic("epic");
        var c2 = Child("c2", "epic", TaskRoles.Cut, "Разрез 2",
            createdAt: new DateTime(2026, 10, 1, 9, 0, 0));
        var c1 = Child("c1", "epic", TaskRoles.Cut, "Разрез 1",
            createdAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var p1 = Child("p1", "epic", TaskRoles.Plan, "План",
            createdAt: new DateTime(2026, 10, 1, 11, 0, 0));
        var k1 = Child("k1", "epic", TaskRoles.Consult, "Консультация",
            createdAt: new DateTime(2026, 10, 1, 12, 0, 0));

        var plans = FeatureProjector.BuildPlans(Run(epic, c1, c2, p1, k1));

        // Порядок: cut (c2, c1 по CreatedAt) → plan (p1) → consult (k1).
        plans.Select(p => p.TaskId).Should().Equal("c2", "c1", "p1", "k1");
        // Роль — из TaskItem.Role как есть.
        plans[0].Role.Should().Be("cut");
        plans[1].Role.Should().Be("cut");
        plans[2].Role.Should().Be("plan");
        plans[3].Role.Should().Be("consult");
    }

    // ── 2. М2: design в документах — роль, hasText и mockupPaths как у остальных ролей ────────

    // Задача role=design даёт запись plans[] с role="design"; hasText и mockupPaths считаются
    // по её ResultMarkdown тем же правилом, что у cut/plan/consult.
    [Fact]
    public void BuildPlans_Design_Попадает_Role_HasText_mockupPaths()
    {
        var epic = Epic("epic");
        var design = Child("d1", "epic", TaskRoles.Design, "Макет панели",
            resultMarkdown: "Отчёт дизайнера: см. docs/mockups/panel-a.html и docs/mockups/panel-b.md.");

        var plans = FeatureProjector.BuildPlans(Run(epic, design));

        var p = plans.Single();
        p.TaskId.Should().Be("d1");
        p.Role.Should().Be("design");
        p.HasText.Should().BeTrue();
        p.MockupPaths.Should().Equal("docs/mockups/panel-a.html", "docs/mockups/panel-b.md");
    }

    // Полный порядок ролей: cut → design → plan → consult; внутри design — по CreatedAt (не по
    // порядку вызывающего); design не дедуплицируется — обе задачи в списке.
    [Fact]
    public void BuildPlans_Порядок_С_Design_Роль_И_ВнутриРолиПоCreatedAt()
    {
        var epic = Epic("epic");
        var c1 = Child("c1", "epic", TaskRoles.Cut, "Разрез",
            createdAt: new DateTime(2026, 10, 1, 9, 0, 0));
        var d2 = Child("d2", "epic", TaskRoles.Design, "Макет 2",
            createdAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var d1 = Child("d1", "epic", TaskRoles.Design, "Макет 1",
            createdAt: new DateTime(2026, 10, 1, 11, 0, 0));
        var p1 = Child("p1", "epic", TaskRoles.Plan, "План",
            createdAt: new DateTime(2026, 10, 1, 12, 0, 0));
        var k1 = Child("k1", "epic", TaskRoles.Consult, "Консультация",
            createdAt: new DateTime(2026, 10, 1, 13, 0, 0));

        // Задачи даны вперемешку, а внутри design порядок входа ПРОТИВОРЕЧИТ порядку CreatedAt
        // (d1 11:00 подаётся раньше d2 10:00): без OrderBy внутри роли вышло бы «d1, d2» и
        // тест покраснел.
        var plans = FeatureProjector.BuildPlans(Run(epic, k1, p1, d1, c1, d2));

        plans.Select(p => p.TaskId).Should().Equal("c1", "d2", "d1", "p1", "k1");
        plans.Select(p => p.Role).Should().Equal("cut", "design", "design", "plan", "consult");
    }

    // ── 3. step с заголовком «Разрез» не попадает (роль как есть, догадок по заголовку нет) ──

    [Fact]
    public void BuildPlans_Step_СЗаголовкомРазрез_НеПопадает()
    {
        var epic = Epic("epic");
        var step = Child("s1", "epic", TaskRoles.Step, "Разрез", stepRef: "B1");
        var cut = Child("c1", "epic", TaskRoles.Cut, "Разрез реальный");

        var plans = FeatureProjector.BuildPlans(Run(epic, step, cut));

        // Только cut; step (даже с заголовком «Разрез») не попал.
        plans.Select(p => p.TaskId).Should().Equal("c1");
    }

    // ── 4. hasText: пустой/пробельный ResultMarkdown → false; текст → true ──────────────────

    [Fact]
    public void BuildPlans_HasText_ПустойИПробельный_False()
    {
        var epic = Epic("epic");
        var withText = Child("a", "epic", TaskRoles.Plan, "План", resultMarkdown: "Текст плана");
        var empty = Child("b", "epic", TaskRoles.Plan, "План пустой", resultMarkdown: "");
        var whitespace = Child("c", "epic", TaskRoles.Plan, "План пробелы", resultMarkdown: "   \n\t  ");
        var nullText = Child("d", "epic", TaskRoles.Cut, "Разрез без текста");

        var plans = FeatureProjector.BuildPlans(Run(epic, withText, empty, whitespace, nullText));

        plans.Single(p => p.TaskId == "a").HasText.Should().BeTrue();
        plans.Single(p => p.TaskId == "b").HasText.Should().BeFalse();
        plans.Single(p => p.TaskId == "c").HasText.Should().BeFalse();
        plans.Single(p => p.TaskId == "d").HasText.Should().BeFalse();
    }

    // ── 5. mockupPaths: путь с «..» отброшен, дубли схлопнуты, потолок 20 ────────────────────

    // Путь с «..» (pathtraversal) отброшен; валидные пути — в порядке появления.
    [Fact]
    public void BuildPlans_MockupPaths_ПутьСДвойнойТочкой_Отброшен()
    {
        var epic = Epic("epic");
        var md = "См. docs/mockups/features-overview-3-plans-2.md и docs/mockups/../leak.md, " +
                "а также docs/mockups/a/b.html.";
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: md);

        var plans = FeatureProjector.BuildPlans(Run(epic, plan));

        // «..» (docs/mockups/../leak.md) отброшен; остаются два валидных пути в порядке появления.
        plans.Single().MockupPaths.Should().Equal(
            "docs/mockups/features-overview-3-plans-2.md",
            "docs/mockups/a/b.html");
    }

    // Дубликаты схлопнуты (distinct, порядок первого появления).
    [Fact]
    public void BuildPlans_MockupPaths_Дубли_Схлопнуты()
    {
        var epic = Epic("epic");
        var md = "docs/mockups/x.md и docs/mockups/x.md, потом docs/mockups/y.md.";
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: md);

        var plans = FeatureProjector.BuildPlans(Run(epic, plan));

        plans.Single().MockupPaths.Should().Equal("docs/mockups/x.md", "docs/mockups/y.md");
    }

    // Потолок 20: 25 уникальных путей → первые 20.
    [Fact]
    public void BuildPlans_MockupPaths_Потолок20()
    {
        var epic = Epic("epic");
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 25; i++)
            sb.Append($"docs/mockups/m{i}.md ");
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: sb.ToString());

        var plans = FeatureProjector.BuildPlans(Run(epic, plan));

        plans.Single().MockupPaths.Should().HaveCount(20);
        plans.Single().MockupPaths[0].Should().Be("docs/mockups/m1.md");
        plans.Single().MockupPaths[^1].Should().Be("docs/mockups/m20.md");
    }

    // ── 6. Мутация: BuildPlans(SlimRun(run)) → у всех hasText=false ─────────────────────────

    // Slim обнуляет ResultMarkdown (контракт master): если BuildPlans считать по SlimRun(run),
    // у ВСЕХ hasText=false (хотя в полном run текст есть) → тест краснеет.
    [Fact]
    public void BuildPlans_Мутация_SlimRun_ВсеHasTextFalse()
    {
        var epic = Epic("epic");
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: "Текст");
        var run = Run(epic, plan);

        // Полный run: hasText=true.
        FeatureProjector.BuildPlans(run).Single().HasText.Should().BeTrue();
        // Мутация: считать по SlimRun(run) → ResultMarkdown обнулён → hasText=false.
        var slim = FeatureProjector.SlimRun(run);
        FeatureProjector.BuildPlans(slim).Single().HasText.Should().BeFalse();
    }

    // ── 7. Мутация: убрать фильтр «..» → путь с «..» появляется ────────────────────────────

    // Если не отбрасывать пути с «..», docs/mockups/../leak.md попадёт в список → тест краснеет.
    // Проверяем, что текущая реализация «..» отбрасывает (т.е. фильтр работает).
    [Fact]
    public void BuildPlans_Мутация_ФильтрДвойныхТочек_Работает()
    {
        var epic = Epic("epic");
        var md = "docs/mockups/../leak.md и docs/mockups/ok.md.";
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: md);

        var paths = FeatureProjector.BuildPlans(Run(epic, plan)).Single().MockupPaths;

        // Фильтр «..» работает: leak.md (с «..») нет, ok.md есть.
        paths.Should().NotContain("docs/mockups/../leak.md");
        paths.Should().Contain("docs/mockups/ok.md");
    }

    // ── 8. М5: LinkedFiles задачи доезжают в mockupPaths (документы вне docs/mockups) ──────

    // Задача role=plan приложила ADR и спеку через linkedFiles — раньше эти пути терялись
    // (regex брал только docs/mockups из ResultMarkdown). Теперь — в mockupPaths как приложены.
    [Fact]
    public void BuildPlans_LinkedFiles_ДокументыВнеMockups_НеТеряются()
    {
        var epic = Epic("epic");
        var plan = Child("p1", "epic", TaskRoles.Plan, "План",
            linkedFiles: ["docs/adr/0042-feature-docs.md", "docs/features/plan-spec.md"]);

        var plans = FeatureProjector.BuildPlans(Run(epic, plan));

        plans.Single().MockupPaths.Should().Equal(
            "docs/adr/0042-feature-docs.md",
            "docs/features/plan-spec.md");
    }

    // Порядок: сначала пути из ResultMarkdown (порядок появления), затем из LinkedFiles;
    // путь, присутствующий в обоих источниках, — один раз, на позиции из текста.
    [Fact]
    public void BuildPlans_Paths_СначалаТело_ПотомLinkedFiles_ДублиСхлопнуты()
    {
        var epic = Epic("epic");
        var plan = Child("p1", "epic", TaskRoles.Plan, "План",
            resultMarkdown: "См. docs/mockups/panel.html.",
            linkedFiles: ["docs/adr/0042.md", "docs/mockups/panel.html"]);

        var paths = FeatureProjector.BuildPlans(Run(epic, plan)).Single().MockupPaths;

        // Тело первым; linkedFiles после; дубль panel.html — один раз (на позиции из текста).
        paths.Should().Equal("docs/mockups/panel.html", "docs/adr/0042.md");
    }

    // Пустые строки и пути с «..» из LinkedFiles отброшены (та же безопасность, что у текста);
    // валидные — остались.
    [Fact]
    public void BuildPlans_LinkedFiles_ПустыеИДвойнаяТочка_Отброшены()
    {
        var epic = Epic("epic");
        var plan = Child("p1", "epic", TaskRoles.Plan, "План",
            linkedFiles: ["", "   ", "docs/adr/../leak.md", "docs/adr/0042.md"]);

        var paths = FeatureProjector.BuildPlans(Run(epic, plan)).Single().MockupPaths;

        paths.Should().Equal("docs/adr/0042.md");
    }

    // Потолок 20 — общий на оба источника: 15 путей из текста + 10 из linkedFiles → первые 20.
    [Fact]
    public void BuildPlans_Paths_ОбщийПотолок20_НаДваИсточника()
    {
        var epic = Epic("epic");
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 15; i++)
            sb.Append($"docs/mockups/m{i}.md ");
        var linked = Enumerable.Range(1, 10).Select(i => $"docs/adr/a{i}.md").ToArray();
        var plan = Child("p1", "epic", TaskRoles.Plan, "План", resultMarkdown: sb.ToString(),
            linkedFiles: linked);

        var paths = FeatureProjector.BuildPlans(Run(epic, plan)).Single().MockupPaths;

        paths.Should().HaveCount(20);
        paths[0].Should().Be("docs/mockups/m1.md");
        paths[14].Should().Be("docs/mockups/m15.md");
        // Осталось 5 слотов — из linkedFiles входят только первые пять.
        paths[15].Should().Be("docs/adr/a1.md");
        paths[^1].Should().Be("docs/adr/a5.md");
    }

    // LinkedFiles задач НЕ-документных ролей (step) в documents не появляются: роль как есть,
    // догадок по заголовку нет (тот же принцип, что п.3). Сторонний файл шага — не документ.
    [Fact]
    public void BuildPlans_LinkedFiles_ШагаНеДокументнойРоли_НеПопадают()
    {
        var epic = Epic("epic");
        var step = Child("s1", "epic", TaskRoles.Step, "Шаг", stepRef: "B1",
            linkedFiles: ["docs/adr/0042.md"]);
        var plan = Child("p1", "epic", TaskRoles.Plan, "План");

        var plans = FeatureProjector.BuildPlans(Run(epic, step, plan));

        plans.Select(p => p.TaskId).Should().Equal("p1");
        plans.Single().MockupPaths.Should().BeEmpty();
    }
}
