using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>notes-tags</c> на живой модели (Батарея II, ось A).
///
/// Берётся НАСТОЯЩИЙ сервис места (<see cref="NotesAiService"/>) поверх НАСТОЯЩЕГО
/// хранилища заметок во временном каталоге: место читает заметку и словарь существующих
/// тегов само, из vault, — подменить их мимо хранилища значило бы подменить и промпт.
/// Подменяется только <see cref="ICheapTextRunner"/> — исполнителем прогона.
///
/// Словарь тегов кладётся в vault заметкой-словарём: продукт собирает существующие теги
/// из ДРУГИХ заметок базы (GetSummaries), и другого способа их туда положить нет.
///
/// ЭТАЛОНА У ЭТОГО МЕСТА НЕТ — единственное такое из семи. Теги в живой базе проставлены
/// неравномерно (часть заметок их не имеет вовсе, часть несёт теги прошлых соглашений), и
/// сверять с ними выбор места значило бы мерить историю базы, а не место. Сличитель
/// поэтому не передаётся, а сводка честно печатает «эталона у места нет»: пустая метрика
/// лучше выдуманной.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает; утверждается ровно одно —
/// прогон дошёл до конца по всем кейсам банка.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class NotesTagsLocalBenchTests(ITestOutputHelper output) : IDisposable
{
    private const string User = "bench-user";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "localbench_notes_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Замер_тегов_заметки()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.NotesTags);
        var vocabulary = Vocabulary(bank);
        var notes = BuildNotesService();
        SeedVocabularyNote(notes, vocabulary);

        var service = new NotesAiService(notes, new ConfigurationBuilder().Build(), runner);
        output.WriteLine($"Словарь базы: {vocabulary.Count} тегов");

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var noteId = EnsureNote(notes, c);
                var tags = await service.SuggestTagsAsync(User, noteId, CancellationToken.None);
                // Что место выдало на выходе: продукт отбрасывает негодные теги молча, и
                // по этой колонке видно, во что вырождается ответ для человека.
                return tags.Count == 0 ? "→ тегов нет" : "→ " + string.Join(", ", tags);
            },
            judge: (_, turns) => NotesTagsOracle.Violation(turns.Last.RawAnswer, vocabulary),
            output);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    // Заметка кейса: заголовок из input, тело из context.content. Уже созданную берём
    // как есть — прогревочный проход идёт по первому кейсу повторно.
    private string EnsureNote(NotesService notes, LocalBenchCase c)
    {
        var existing = notes.GetSummaries(User, null, null)
            .FirstOrDefault(s => s.Title == c.Input);
        if (existing is not null) return existing.Id;

        var content = c.Context?.TryGetProperty("content", out var el) == true
            ? el.GetString() ?? ""
            : "";
        return notes.Create(User, new CreateNoteRequest(c.Input, content, "personal")).Id;
    }

    // Словарь существующих тегов базы: единственный способ отдать его месту — заметка,
    // несущая эти теги во frontmatter (продукт собирает их из соседних заметок).
    private void SeedVocabularyNote(NotesService notes, IReadOnlyList<string> vocabulary)
    {
        var frontmatter = $"---\ntags: [{string.Join(", ", vocabulary)}]\n---\n"
                          + "Служебная заметка замера: несёт словарь существующих тегов базы.\n";
        notes.Create(User, new CreateNoteRequest("Словарь тегов базы", frontmatter, "personal"));
    }

    private NotesService BuildNotesService()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        return new NotesService(projects, config, NullLogger<NotesService>.Instance);
    }

    private static IReadOnlyList<string> Vocabulary(LocalBenchCaseBank bank)
    {
        if (bank.Shared?.TryGetProperty("vocabulary", out var el) != true
            || el.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"В банке «{bank.Place}» нет shared.vocabulary — словаря тегов базы");
        return el.EnumerateArray().Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0).ToList();
    }
}
