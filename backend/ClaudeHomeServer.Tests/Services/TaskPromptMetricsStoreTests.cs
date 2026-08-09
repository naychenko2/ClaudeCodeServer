using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Стор замеров размера постановки задач. Главное здесь — сторож приватности: файл лежит
// рядом с аналитикой и отдаётся через API, содержимого постановки и чужих заметок в нём
// быть не может ни при каких условиях.
public class TaskPromptMetricsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "tpm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* временная папка теста — уборка не критична */ }
        GC.SuppressFinalize(this);
    }

    private TaskPromptMetricsStore Store() => new(_dir);

    private static TaskPromptMetricsStore.Entry Entry(string taskId, string ownerId = "o1",
        int total = 1000) =>
        new(DateTime.UtcNow, taskId, ownerId, "p1", "s1", "persona1",
            total, total / 4, 55, 0, 0, 646, 0, 252, 0, 31, 852);

    [Fact]
    public void Record_ЗатемForTask_ВозвращаетЗамерыЗадачи()
    {
        var store = Store();
        store.Record(Entry("task-1", total: 1000));
        store.Record(Entry("task-2", total: 2000));
        store.Record(Entry("task-1", total: 1500));

        var runs = store.ForTask("task-1");

        runs.Should().HaveCount(2, "чужая задача в выдачу не попадает");
        runs.Should().OnlyContain(r => r.TaskId == "task-1");
        // Новые сверху: последний запуск задачи интереснее первого
        runs[0].TotalChars.Should().Be(1500);
        runs[1].TotalChars.Should().Be(1000);
    }

    [Fact]
    public void ForTask_ФайлаНет_ПустойСписок()
    {
        // Задача запускалась до появления стора — это не ошибка, разбивки просто нет
        Store().ForTask("task-1").Should().BeEmpty();
    }

    [Fact]
    public void ForTask_БитаяСтрока_ОстальныеЗамерыЧитаются()
    {
        // Обрыв записи при падении процесса не должен прятать весь файл
        var store = Store();
        store.Record(Entry("task-1", total: 1000));
        File.AppendAllText(Path.Combine(_dir, TaskPromptMetricsStore.FileName),
            "{битый json" + Environment.NewLine);
        store.Record(Entry("task-1", total: 1500));

        store.ForTask("task-1").Should().HaveCount(2);
    }

    [Fact]
    public void Record_ПапкиНет_НеБросает()
    {
        // Учёт не должен ронять запуск задачи ни при каких условиях
        var store = new TaskPromptMetricsStore(Path.Combine(_dir, "нет", "такой", "папки"));

        var act = () => store.Record(Entry("task-1"));

        act.Should().NotThrow();
    }

    // ─── Сторож приватности ───────────────────────────────────────────────────

    [Fact]
    public void ВФайлеТолькоРазмеры_НиСимволаТекстаПостановкиИЗаметок()
    {
        // Прогоняем настоящую постановку с описанием, подзадачами, файлами и заметками —
        // и убеждаемся, что в стор не утёк ни один их фрагмент
        var task = new TaskItem
        {
            Id = "task-secret",
            OwnerId = "o1",
            Title = "СЕКРЕТНЫЙ_ЗАГОЛОВОК",
            Description = "СЕКРЕТНОЕ_ОПИСАНИЕ задачи",
            Subtasks = [new TaskSubtask { Title = "СЕКРЕТНАЯ_ПОДЗАДАЧА" }],
            LinkedFiles = ["src/СЕКРЕТНЫЙ_ФАЙЛ.cs"],
        };
        const string notes = "\n## Заметки\n### СЕКРЕТНАЯ_ЗАМЕТКА\nСЕКРЕТНЫЙ_СНИППЕТ из базы знаний";
        var prompt = TaskExecutionService.BuildPrompt(task, new Persona { Name = "Вера" }) + notes;
        var m = TaskExecutionService.MeasurePrompt(prompt, notes);

        var store = Store();
        store.Record(new TaskPromptMetricsStore.Entry(
            DateTime.UtcNow, task.Id, task.OwnerId!, "p1", "s1", null,
            m.TotalChars, m.TotalTokensEst, m.TaskSectionChars, m.ExpectedResultChars,
            m.ToolsChars, m.MandatoryChars, m.RestrictionsChars, m.DelegationChars,
            m.OmOChars, m.ContextChars, m.NotesContextChars));

        var raw = File.ReadAllText(Path.Combine(_dir, TaskPromptMetricsStore.FileName));

        raw.Should().NotContain("СЕКРЕТНЫЙ_ЗАГОЛОВОК");
        raw.Should().NotContain("СЕКРЕТНОЕ_ОПИСАНИЕ");
        raw.Should().NotContain("СЕКРЕТНАЯ_ПОДЗАДАЧА");
        raw.Should().NotContain("СЕКРЕТНЫЙ_ФАЙЛ");
        raw.Should().NotContain("СЕКРЕТНАЯ_ЗАМЕТКА");
        raw.Should().NotContain("СЕКРЕТНЫЙ_СНИППЕТ");
        // При этом размеры записаны — стор не пустой
        raw.Should().Contain("\"totalChars\":" + m.TotalChars);
        raw.Should().Contain("\"notes\":" + m.NotesContextChars);
    }
}
