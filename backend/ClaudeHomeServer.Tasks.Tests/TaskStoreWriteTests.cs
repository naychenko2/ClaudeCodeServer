using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Запись стора задач: формат файла (компактный, без \uXXXX) и дебаунс записи.
/// Оба свойства про диск, поэтому проверяются по реальному файлу во временном каталоге.
/// </summary>
public class TaskStoreWriteTests : IDisposable
{
    private readonly string _dir;
    private readonly List<TaskManager> _created = [];

    public TaskStoreWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tasks_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var m in _created) m.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string StorePath => Path.Combine(_dir, "tasks.json");

    // debounceMs: null — дефолт продукта, 0 — синхронная запись, N — окно дебаунса
    private TaskManager NewManager(int? debounceMs = 0)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        };
        if (debounceMs is not null) settings["Tasks:SaveDebounceMs"] = debounceMs.Value.ToString();
        var manager = new TaskManager(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _created.Add(manager);
        return manager;
    }

    // ─── Формат записи ───────────────────────────────────────────────────────

    [Fact]
    public void Формат_КириллицаПишетсяКакЕсть_БезЭкранирования()
    {
        var sut = NewManager();
        sut.Create("proj-1", "user-1", new CreateTaskRequest(
            Title: "Выкатить релиз", Description: "Проверить миграции и уведомления"));

        var json = File.ReadAllText(StorePath);

        json.Should().Contain("Выкатить релиз");
        json.Should().Contain("Проверить миграции и уведомления");
        // \u0412 — «В» в экранированном виде: именно это раздувало файл вшестеро
        json.Should().NotContain("\\u04");
    }

    [Fact]
    public void Формат_БезОтступов()
    {
        var sut = NewManager();
        sut.Create("proj-1", "user-1", new CreateTaskRequest("Задача"));

        var json = File.ReadAllText(StorePath);

        json.Should().NotContain("\n");
        json.Should().StartWith("[{");
    }

    [Fact]
    public void Формат_НовыйФайлЗаметноМеньшеПрежнегоФормата()
    {
        var sut = NewManager();
        // Описание в несколько абзацев — типичная постановка задачи в продукте: именно
        // текст, а не служебные поля, составляет основной объём стора
        var description = string.Concat(Enumerable.Repeat(
            "Длинное русское описание задачи, ровно такое, какие пишут постановщики: " +
            "несколько предложений подряд, без единого латинского слова. ", 10));
        for (var i = 0; i < 20; i++)
            sut.Create("proj-1", "user-1", new CreateTaskRequest(
                Title: $"Задача номер {i}", Description: description));

        var actual = new FileInfo(StorePath).Length;
        // Прежний формат — дефолтные опции JsonFileStore (то, чем стор писался до правки)
        var previous = JsonSerializer.SerializeToUtf8Bytes(
            sut.GetByOwner("user-1").ToList(), (JsonSerializerOptions?)null).Length;

        actual.Should().BeLessThan(previous / 2,
            "кириллица в \\uXXXX занимает 6 байт на символ вместо 2");
    }

    [Fact]
    public void Формат_СтарыйФайлЧитается()
    {
        // Ровно то, что лежит на диске у работающего инстанса до этой правки:
        // отступы + кириллица в \uXXXX
        var legacy = JsonSerializer.Serialize(
            new List<TaskItem>
            {
                new()
                {
                    Id = "legacy-1", OwnerId = "user-1", ProjectId = "proj-1",
                    Title = "Старый формат", Description = "Отступы и \\uXXXX",
                    Order = 1000,
                },
            },
            new JsonSerializerOptions { WriteIndented = true });
        legacy.Should().Contain("\\u0421", "тест обязан кормить менеджер именно экранированным файлом");
        File.WriteAllText(StorePath, legacy);

        var sut = NewManager();

        sut.GetById("legacy-1")!.Title.Should().Be("Старый формат");
    }

    [Fact]
    public void Формат_СтарыйФайлПерезаписываетсяНовым()
    {
        File.WriteAllText(StorePath, JsonSerializer.Serialize(
            new List<TaskItem> { new() { Id = "legacy-1", OwnerId = "user-1", Title = "Старый", Order = 1000 } },
            new JsonSerializerOptions { WriteIndented = true }));

        var sut = NewManager();
        sut.Create(null, "user-1", new CreateTaskRequest("Новая задача"));

        var json = File.ReadAllText(StorePath);
        json.Should().NotContain("\\u04").And.NotContain("\n");
        json.Should().Contain("Старый").And.Contain("Новая задача");
    }

    // ─── Дебаунс записи ──────────────────────────────────────────────────────

    [Fact]
    public void Дебаунс_ПачкаИзмененийДаётОднуЗапись()
    {
        // Окно заведомо больше теста: сам таймер сработать не успеет, и всё, что
        // окажется на диске, — результат ровно одного сброса
        var sut = NewManager(60_000);

        var task = sut.Create("proj-1", "user-1", new CreateTaskRequest("Пачка"));
        sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress));
        sut.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        sut.MarkClaudeResult(task.Id, "success");
        sut.Update(task.Id, new UpdateTaskRequest(ResultMarkdown: "Готово"));

        // Пять изменений — ни одной записи
        File.Exists(StorePath).Should().BeFalse();

        sut.Flush();

        // Одна запись, и в ней всё накопленное
        var saved = ReadStore();
        saved.Should().ContainSingle();
        saved[0].Status.Should().Be(TaskItemStatus.InProgress);
        saved[0].ClaudeResult.Should().Be("success");
        saved[0].ResultMarkdown.Should().Be("Готово");
    }

    [Fact]
    public void Дебаунс_ТаймерСамДоводитИзменениеДоДиска()
    {
        var sut = NewManager(50);

        sut.Create("proj-1", "user-1", new CreateTaskRequest("Сам доеду"));

        WaitUntil(() => File.Exists(StorePath), "таймер дебаунса должен сбросить стор сам");
        ReadStore().Should().ContainSingle(t => t.Title == "Сам доеду");
    }

    [Fact]
    public void Дебаунс_ПовторныйFlushБезИзмененийНеПишетФайл()
    {
        var sut = NewManager(60_000);
        sut.Create("proj-1", "user-1", new CreateTaskRequest("Одна"));
        sut.Flush();
        var firstWrite = File.GetLastWriteTimeUtc(StorePath);

        sut.Flush();

        File.GetLastWriteTimeUtc(StorePath).Should().Be(firstWrite);
    }

    [Fact]
    public void Дебаунс_НольОтключаетОтложеннуюЗапись()
    {
        var sut = NewManager(0);

        sut.Create("proj-1", "user-1", new CreateTaskRequest("Синхронно"));

        ReadStore().Should().ContainSingle(t => t.Title == "Синхронно");
    }

    // ─── Сброс при остановке ─────────────────────────────────────────────────

    [Fact]
    public void Dispose_ДосбрасываетНесохранённое()
    {
        var sut = NewManager(60_000);
        sut.Create("proj-1", "user-1", new CreateTaskRequest("Не потеряюсь"));
        File.Exists(StorePath).Should().BeFalse();

        sut.Dispose();

        ReadStore().Should().ContainSingle(t => t.Title == "Не потеряюсь");
    }

    [Fact]
    public void Dispose_ПовторныйВызовБезопасен()
    {
        // Форвардер ITaskStatusReader зарегистрирован фабрикой, и контейнер DI
        // отслеживает тот же экземпляр дважды — Dispose прилетает два раза
        var sut = NewManager(60_000);
        sut.Create("proj-1", "user-1", new CreateTaskRequest("Дважды"));

        sut.Dispose();
        var act = () => sut.Dispose();

        act.Should().NotThrow();
        ReadStore().Should().ContainSingle();
    }

    [Fact]
    public void ПравкаПослеDispose_ПишетсяСразу()
    {
        // Таймера уже нет, и откладывать запись некому — иначе изменение пропало бы молча
        var sut = NewManager(60_000);
        sut.Dispose();

        sut.Create("proj-1", "user-1", new CreateTaskRequest("После остановки"));

        ReadStore().Should().ContainSingle(t => t.Title == "После остановки");
    }

    private List<TaskItem> ReadStore() =>
        JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(StorePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // Ожидание события, а не сна фиксированной длины: на слабом CI-раннере таймер
    // просыпается позже, и Task.Delay дал бы плавающее падение.
    private static void WaitUntil(Func<bool> condition, string because)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException($"условие не наступило за 10 с: {because}");
    }
}
