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
    public void НовыйСтор_ОтдаётОбаПресетаИзКода_ВалидныеПоПравилам()
    {
        var all = NewStore().List();

        all.Select(e => e.Template.BuiltInKey).Should().Equal("full-cycle", "fast-track");
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

        store.List().Select(e => e.Template.BuiltInKey).Should().Equal("full-cycle", "fast-track");
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
