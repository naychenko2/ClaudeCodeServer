using System.Security.Cryptography;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// Стор шаблонов процесса (B1b): платформонезависимо — пути от Path.GetTempPath().
public class WorkflowTemplateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wts-{Guid.NewGuid():N}");

    public WorkflowTemplateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private string FilePath => Path.Combine(_dir, "workflow-templates.json");

    private WorkflowTemplateStore NewStore() => new(FilePath);

    private static WorkflowTemplate Custom(string name = "мой", string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Name = name,
        Stages =
        [
            new WorkflowStage { Role = TaskRoles.Step, Presence = WorkflowStagePresence.On },
            new WorkflowStage { Role = TaskRoles.Final, Presence = WorkflowStagePresence.On },
            new WorkflowStage { Role = TaskRoles.Merge, Presence = WorkflowStagePresence.On },
        ],
    };

    private static WorkflowTemplateException Fail(Action act) =>
        act.Should().Throw<WorkflowTemplateException>().Which;

    [Fact]
    public void НовыйСтор_ОтдаётВсеПресетыИзКода_ВалидныеПоПравилам()
    {
        var all = NewStore().List();

        all.Select(e => e.Template.BuiltInKey).Should().Equal("full-cycle", "fast-track", "parallel-cycle");
        all.Should().OnlyContain(e => e.Scope == WorkflowTemplateScope.Global && e.Template.Version == 1);
        foreach (var e in all) WorkflowRules.Validate(e.Template).Should().BeEmpty();

        var full = all[0].Template.Stages.ToDictionary(s => s.Role, s => s.Presence);
        full[TaskRoles.Cut].Should().Be("on");
        full[TaskRoles.Design].Should().Be("optional");
        full[TaskRoles.Qa].Should().Be("optional");
        var fast = all[1].Template.Stages.ToDictionary(s => s.Role, s => s.Presence);
        fast[TaskRoles.Cut].Should().Be("off");
        fast[TaskRoles.Design].Should().Be("off");
        fast[TaskRoles.Plan].Should().Be("optional");
        fast[TaskRoles.Qa].Should().Be("off");
        fast[TaskRoles.Final].Should().Be("on");
    }

    [Fact]
    public void Save_Новый_ВерсияОдин_ПереживаетПерезапуск()
    {
        var store = NewStore();
        var saved = store.Save(Custom("процесс"), WorkflowTemplateScope.Global);

        saved.Version.Should().Be(1);
        saved.BuiltInKey.Should().BeNull();
        var reread = NewStore().Get(saved.Id, WorkflowTemplateScope.Global);
        reread.Should().NotBeNull();
        reread!.Name.Should().Be("процесс");
        reread.Version.Should().Be(1);
    }

    [Fact]
    public void Save_Существующий_ВерсияПлюсОдин_БезОбъявленияВстроенным()
    {
        var store = NewStore();
        var t = store.Save(Custom(), WorkflowTemplateScope.Global);
        t.BuiltInKey = "full-cycle"; // клиент не может сделать шаблон встроенным
        t.Name = "правка";

        var second = store.Save(t, WorkflowTemplateScope.Global);

        second.Version.Should().Be(2);
        second.BuiltInKey.Should().BeNull();
    }

    // Гонка: два параллельных сохранения без expectedVersion — N+1 и N+2, обе применены.
    [Fact]
    public async Task Save_ДваПараллельно_ВерсияПлюсДва()
    {
        var store = NewStore();
        var baseTemplate = store.Save(Custom(), WorkflowTemplateScope.Global);
        using var barrier = new Barrier(2);

        Task<WorkflowTemplate> Run(string name) => Task.Run(() =>
        {
            var copy = store.Get(baseTemplate.Id, WorkflowTemplateScope.Global)!;
            copy.Name = name;
            barrier.SignalAndWait();
            return store.Save(copy, WorkflowTemplateScope.Global);
        });

        var results = await Task.WhenAll(Run("а"), Run("б"));

        results.Select(r => r.Version).OrderBy(v => v).Should().Equal(baseTemplate.Version + 1, baseTemplate.Version + 2);
        var final = NewStore().Get(baseTemplate.Id, WorkflowTemplateScope.Global)!;
        final.Version.Should().Be(baseTemplate.Version + 2);
        final.Name.Should().Be(results.OrderBy(r => r.Version).Last().Name); // побеждает вторая по очереди
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Save_УстаревшаяВерсия_Конфликт_ФайлНеТронут()
    {
        var store = NewStore();
        var t = store.Save(Custom(), WorkflowTemplateScope.Global);          // v1
        store.Save(t, WorkflowTemplateScope.Global, expectedVersion: 1);     // v2
        var before = File.ReadAllText(FilePath);

        var ex = Fail(() => store.Save(t, WorkflowTemplateScope.Global, expectedVersion: 1));

        ex.Kind.Should().Be(WorkflowTemplateErrorKind.Conflict);
        ex.Message.Should().Contain("изменился");
        File.ReadAllText(FilePath).Should().Be(before);
    }

    [Fact]
    public void Save_НевалидныйШаблон_ВалидацияСПолнымСписком_ФайлНеСоздан()
    {
        var bad = Custom();
        bad.Stages[1].Presence = WorkflowStagePresence.Off; // final несъёмный
        bad.Name = " ";

        var ex = Fail(() => NewStore().Save(bad, WorkflowTemplateScope.Global));

        ex.Kind.Should().Be(WorkflowTemplateErrorKind.Validation);
        ex.Errors.Should().HaveCount(2);
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void Save_ПорогМолчанияКлампитсяИПробелыТриммятся()
    {
        var t = Custom();
        t.Stages[0].StallMinutes = 1;
        t.Stages[0].Presence = " on ";

        var saved = NewStore().Save(t, WorkflowTemplateScope.Global);

        saved.Stages[0].StallMinutes.Should().Be(5);
        saved.Stages[0].Presence.Should().Be("on");
    }

    // G7: Title нормализуется тем же путём, что проверяет Validate: трим по краям, «пустая → null».
    [Fact]
    public void Save_TitleТриммится_ПустойСтановитсяNull()
    {
        var t = Custom();
        t.Stages[0].Title = "  Рабочий шаг  ";

        var saved = NewStore().Save(t, WorkflowTemplateScope.Global);

        saved.Stages[0].Title.Should().Be("Рабочий шаг");

        var blank = NewStore().Get(saved.Id, WorkflowTemplateScope.Global)!;
        blank.Stages[0].Title = "   ";
        var second = NewStore().Save(blank, WorkflowTemplateScope.Global);
        second.Stages[0].Title.Should().BeNull();
    }

    [Fact]
    public void Delete_Встроенный_Отказ_Свой_Удаляется()
    {
        var store = NewStore();
        var builtIn = store.List()[0].Template;

        Fail(() => store.Delete(builtIn.Id, WorkflowTemplateScope.Global)).Kind.Should().Be(WorkflowTemplateErrorKind.Forbidden);
        store.Get(builtIn.Id, WorkflowTemplateScope.Global).Should().NotBeNull();

        var own = store.Save(Custom(), WorkflowTemplateScope.Global);
        store.Delete(own.Id, WorkflowTemplateScope.Global);
        store.Get(own.Id, WorkflowTemplateScope.Global).Should().BeNull();
        Fail(() => store.Delete(own.Id, WorkflowTemplateScope.Global)).Kind.Should().Be(WorkflowTemplateErrorKind.NotFound);
    }

    [Fact]
    public void ResetBuiltIn_ВозвращаетКодИДаётВерсиюПлюсОдин()
    {
        var store = NewStore();
        var fast = store.List().Single(e => e.Template.BuiltInKey == "fast-track").Template;
        fast.Stages.Single(s => s.Role == TaskRoles.Qa).Presence = WorkflowStagePresence.On;
        var edited = store.Save(fast, WorkflowTemplateScope.Global);
        edited.Version.Should().Be(2);
        edited.BuiltInKey.Should().Be("fast-track");

        var reset = store.ResetBuiltIn("fast-track");

        reset.Version.Should().Be(3);
        reset.Stages.Single(s => s.Role == TaskRoles.Qa).Presence.Should().Be("off");
        NewStore().Get(reset.Id, WorkflowTemplateScope.Global)!.Version.Should().Be(3);
        Fail(() => store.ResetBuiltIn("нет-такого")).Kind.Should().Be(WorkflowTemplateErrorKind.NotFound);
    }

    // G5 (посев пресетов): каждый встроенный пресет валиден и разрешим при ЛЮБОМ снятии
    // optional-этапов — ни одна ветка не должна оставаться без стока; у линейных пресетов
    // id этапа после Normalize равен роли (их форма не меняется).
    [Fact]
    public void ВсеВстроенныеПресеты_ВалидныИРазрешимы_ПриЛюбомСнятииOptional()
    {
        WorkflowTemplateStore.BuiltInKeys.Should().Equal("full-cycle", "fast-track", "parallel-cycle");

        foreach (var key in WorkflowTemplateStore.BuiltInKeys)
        {
            var preset = WorkflowTemplateStore.BuiltInPreset(key);
            WorkflowRules.Validate(preset).Should().BeEmpty($"пресет «{key}»");

            var nodes = WorkflowGraph.Normalize(preset.Stages);
            var optional = nodes.Where(n => n.Presence == WorkflowStagePresence.Optional).Select(n => n.Id).ToList();

            for (var mask = 0; mask < (1 << optional.Count); mask++)
            {
                var skipped = optional.Where((_, bit) => (mask >> bit & 1) == 1).ToList();
                var label = $"пресет «{key}», снято [{string.Join(",", skipped)}]";
                var route = WorkflowGraph.Resolve(preset, skipped);

                WorkflowGraph.DanglingIds(route).Should().BeEmpty($"{label}: ветка обязана сходиться в общий сток");
                route.Select(s => s.Id).Should().BeEquivalentTo(
                    nodes.Where(n => n.Presence != WorkflowStagePresence.Off && !skipped.Contains(n.Id)).Select(n => n.Id),
                    $"{label}: в маршруте ровно неснятые этапы");
            }
        }

        foreach (var key in new[] { WorkflowTemplateStore.FullCycleKey, WorkflowTemplateStore.FastTrackKey })
            WorkflowGraph.Normalize(WorkflowTemplateStore.BuiltInPreset(key).Stages)
                .Should().OnlyContain(n => n.Id == n.Role, $"пресет «{key}» линейный: id этапа = роль");
    }

    // G5: форма графа parallel-cycle (разрез 2 §1.10) — две линии шага в одной колонке,
    // ветка проверки плана рядом с планом, общий QA правее ревью. Тот же рисунок, что в тестах
    // фронта (frontend/src/features/workflowTemplates/workflowGraphModel.test.ts).
    [Fact]
    public void ParallelCycle_ДвеЛинииШагаВОднойКолонке_РёбраПоРазрезу()
    {
        var preset = WorkflowTemplateStore.BuiltInPreset(WorkflowTemplateStore.ParallelCycleKey);
        var route = WorkflowGraph.Resolve(preset, []);
        var columns = WorkflowGraph.Columns(route);

        route.Select(s => s.Id).Should().Equal(
            "cut", "design", "plan", "qa-plan", "step-back", "step-front", "review", "qa", "final", "merge");
        WorkflowGraph.DanglingIds(route).Should().BeEmpty();

        columns["step-back"].Should().Be(columns["step-front"], "две линии шага — одна колонка");
        columns["qa-plan"].Should().Be(columns["plan"], "ветка проверки плана идёт рядом с планом");
        columns["qa"].Should().BeGreaterThan(columns["review"], "qa сходится из ревью и ветки плана");
        columns["final"].Should().BeGreaterThan(columns["qa"]);
        columns["merge"].Should().BeGreaterThan(columns["final"]);

        route.Single(s => s.Id == "review").After.Should().Equal("step-back", "step-front");
        route.Single(s => s.Id == "qa").After.Should().Equal("review", "qa-plan");
        // Presence — по образцу full-cycle: съёмные те же роли (design, qa) плюс своя ветка
        // проверки плана, несъёмные (step/final/merge) — всегда «on»
        preset.Stages.Select(s => (s.Id ?? s.Role, s.Presence)).Should().Equal(
            ("cut", "on"), ("design", "optional"), ("plan", "on"), ("qa-plan", "optional"),
            ("step-back", "on"), ("step-front", "on"), ("review", "on"), ("qa", "optional"),
            ("final", "on"), ("merge", "on"));
        // линии — только у step и разные: иначе StageFor не выбрал бы этап по линии задачи
        route.Where(s => s.Role == TaskRoles.Step).Select(s => (s.Id, s.Lane)).Should()
            .Equal(("step-back", "back"), ("step-front", "front"));
        // специальность ветки проверки плана — тестировщик (разрез 2 §1.10)
        preset.Stages.Single(s => s.Id == "qa-plan").Specialty.Should().Be(PersonaSpecialty.Tester);

        // G7 (a5d0c76e): qa-plan и qa делят роль — в пресете у каждого своё имя, и Resolve
        // копирует его в подтверждённый маршрут (заморозка рядом с Id/Role/Lane).
        preset.Stages.Single(s => s.Id == "qa-plan").Title.Should().Be("Проверка плана");
        preset.Stages.Single(s => s.Id == "qa").Title.Should().Be("Проверка");
        route.Single(s => s.Id == "qa-plan").Title.Should().Be("Проверка плана");
        route.Single(s => s.Id == "qa").Title.Should().Be("Проверка");
        route.Single(s => s.Id == "review").Title.Should().BeNull(
            "у этапа без имени в пресете Title нет — он не выдумывается проекцией");
    }

    // G5: сброс parallel-cycle идёт тем же путём, что у full-cycle/fast-track (B1b):
    // правка → Save → ResetBuiltIn → граф кода, Version +1.
    [Fact]
    public void ResetBuiltIn_ПараллельныйЦикл_ВозвращаетГрафКода()
    {
        var store = NewStore();
        var preset = store.List().Single(e => e.Template.BuiltInKey == "parallel-cycle").Template;
        preset.Stages.Single(s => s.Id == "qa").Presence = WorkflowStagePresence.On; // правка: общий QA обязателен
        var edited = store.Save(preset, WorkflowTemplateScope.Global);
        edited.Version.Should().Be(2);
        edited.Stages.Single(s => s.Id == "qa").Presence.Should().Be("on");

        var reset = store.ResetBuiltIn(WorkflowTemplateStore.ParallelCycleKey);

        reset.Version.Should().Be(3);
        reset.Should().BeEquivalentTo(
            WorkflowTemplateStore.BuiltInPreset(WorkflowTemplateStore.ParallelCycleKey),
            opt => opt.Excluding(t => t.Version));
        var fromFile = NewStore().Get(reset.Id, WorkflowTemplateScope.Global)!;
        fromFile.Version.Should().Be(3);
        fromFile.Stages.Single(s => s.Id == "qa").Presence.Should().Be("optional");
        fromFile.Stages.Single(s => s.Id == "step-front").After.Should().Equal("plan");
        fromFile.Stages.Single(s => s.Id == "qa-plan").Specialty.Should().Be(PersonaSpecialty.Tester);
    }

    // Конкурентность G5: сброс встроенного и сохранение своего шаблона одновременно. Один лок
    // стора сериализует обе правки — в файле остаются обе; без лока последний снимок стёр бы
    // одну из них (мутация — в отчёте задачи).
    [Fact]
    public async Task Reset_ПараллельноССохранением_ОбаШаблонаЦелы()
    {
        var store = NewStore();
        var own = store.Save(Custom("свой"), WorkflowTemplateScope.Global);
        using var barrier = new Barrier(2);

        var resetting = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return store.ResetBuiltIn(WorkflowTemplateStore.ParallelCycleKey);
        });
        var saving = Task.Run(() =>
        {
            var copy = store.Get(own.Id, WorkflowTemplateScope.Global)!;
            copy.Name = "правка";
            barrier.SignalAndWait();
            return store.Save(copy, WorkflowTemplateScope.Global);
        });

        var reset = await resetting;
        var saved = await saving;

        reset.Version.Should().Be(2);
        saved.Version.Should().Be(2);

        var after = NewStore();
        var preset = after.Get(WorkflowTemplateStore.BuiltInPreset(WorkflowTemplateStore.ParallelCycleKey).Id,
            WorkflowTemplateScope.Global)!;
        preset.Version.Should().Be(2, "сброс применён к файлу");
        preset.Stages.Should().HaveCount(10);
        after.Get(own.Id, WorkflowTemplateScope.Global)!.Name.Should().Be("правка", "сохранение применено к файлу");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Get_List_ОтдаютКопии_ПравкаКопииСторНеМеняет()
    {
        var store = NewStore();
        var saved = store.Save(Custom("исходное"), WorkflowTemplateScope.Global);

        var got = store.Get(saved.Id, WorkflowTemplateScope.Global)!;
        got.Name = "испорчено";
        got.Stages.Clear();
        store.List().Select(e => e.Template).Single(t => t.Id == saved.Id).Stages.Should().NotBeEmpty();
        store.Get(saved.Id, WorkflowTemplateScope.Global)!.Name.Should().Be("исходное");
    }

    [Fact]
    public void БитыйФайл_УходитВCorrupt_СторПоднимаетсяНаПресетах()
    {
        File.WriteAllText(FilePath, "{ это не json");

        var store = NewStore();

        store.List().Select(e => e.Template.BuiltInKey).Should().Equal("full-cycle", "fast-track", "parallel-cycle");
        Directory.GetFiles(_dir, "*.corrupt-*.bak").Should().HaveCount(1);
    }

    [Fact]
    public void ПроектныйШаблон_BasedOnСохраняется_ВиденТолькоСвоемуПроекту()
    {
        var store = NewStore();
        var t = Custom("форк");
        t.BasedOn = new WorkflowTemplateRef { TemplateId = "builtin-full-cycle", Version = 1 };

        var saved = store.Save(t, WorkflowTemplateScope.Project, "p1");

        saved.BasedOn.Should().BeEquivalentTo(new WorkflowTemplateRef { TemplateId = "builtin-full-cycle", Version = 1 });
        store.List("p1").Count(e => e.Scope == WorkflowTemplateScope.Project).Should().Be(1);
        store.List("p2").Count(e => e.Scope == WorkflowTemplateScope.Project).Should().Be(0); // пустая коллекция
        store.List().Count(e => e.Scope == WorkflowTemplateScope.Project).Should().Be(0);
        store.Get(saved.Id, WorkflowTemplateScope.Global).Should().BeNull();
        Fail(() => store.Save(Custom(), WorkflowTemplateScope.Project, null)).Kind.Should().Be(WorkflowTemplateErrorKind.Validation);
        NewStore().Get(saved.Id, WorkflowTemplateScope.Project, "p1")!.BasedOn!.TemplateId.Should().Be("builtin-full-cycle");
    }

    // И4 (часть): сохранение шаблона не трогает tasks.json.
    [Fact]
    public void Save_НеТрогаетTasksJson()
    {
        var tasksPath = Path.Combine(_dir, "tasks.json");
        File.WriteAllText(tasksPath, "[{\"id\":\"t1\"}]");
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tasksPath)));
        var writtenBefore = File.GetLastWriteTimeUtc(tasksPath);

        var store = NewStore();
        var t = store.Save(Custom(), WorkflowTemplateScope.Global);
        store.Save(t, WorkflowTemplateScope.Global);
        store.ResetBuiltIn("full-cycle");
        store.Delete(t.Id, WorkflowTemplateScope.Global);

        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tasksPath))).Should().Be(before);
        File.GetLastWriteTimeUtc(tasksPath).Should().Be(writtenBefore);
    }
}
