using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// J-P1c (разрез 2026-11-12): явный флаг бэклога keeper/backlog-until.json и его действие
// на разбор. Критерии: c1 — Set(сегодня+14) активен сегодня и на границе until, не активен
// на until+1; нет файла и мусор в файле — false без исключения. c2 — 20 параллельных Set
// с разными датами плюс параллельные чтения: 0 исключений, итоговый файл парсится и несёт
// одну из записанных дат (атомарная замена temp+Move: читатель видит целый файл).
// c3 — флаг доходит до разбора: с ним план из 16 новых страниц раздела роняет лимит только
// на 16-й (поднятый лимит 15 проходит целиком), без него — уже на 6-й (обычный лимит 5).
// Мутация «IsBacklogPass = false» в WikiIngestService — красный здесь (флаг есть, а
// политика его не видит).
public class WikiBacklogFlagTests : IDisposable
{
    private const string User = "u1";
    // Два независимых раздела с своими карточками: прогон без флага судит своё состояние
    // раздела. В одном разделе второй прогон проверял бы гейтом финального состояния и
    // страницы первого — источник у них другой, и это была бы проверка не флага.
    private const string SectionA = "projects/p1";
    private const string SectionB = "projects/p2";
    private const string CardPathA = "Проекты/p1/Источники/Карточка-A.md";
    private const string CardPathB = "Проекты/p2/Источники/Карточка-B.md";

    private static string CardFile(string title) => $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-11-12
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiBacklogFlagTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_backlog_flag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        // Карточка источника — цель сносок принятых операций: без неё план не применяется.
        foreach (var (section, path, title) in new[]
        {
            (SectionA, CardPathA, "Карточка-A"),
            (SectionB, CardPathB, "Карточка-B"),
        })
        {
            var r = _keeper.WritePageAsync(User, section, path, CardFile(title)).GetAwaiter().GetResult();
            r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── c1: Set → IsActive, граница включительно, после границы — нет ────────────────

    [Fact]
    public void Set_ЧетырнадцатьДней_АктивенДоГраницыВключительно_ПослеНете()
    {
        var today = Today();
        var until = today.AddDays(14);
        WikiBacklogFlag.Set(Root, until);

        // Мутация «today < until» (строгая граница): второй assert красный — until —
        // последний день флага, не первый день его конца.
        WikiBacklogFlag.IsActive(Root, today).Should().BeTrue("флаг активен в день постановки");
        WikiBacklogFlag.IsActive(Root, until).Should().BeTrue("until — последний день флага (граница включительно)");
        WikiBacklogFlag.IsActive(Root, until.AddDays(1)).Should().BeFalse("день после until флаг истёк");

        // Файл читается человеком: ровно обещанный формат {"until":"YYYY-MM-DD"}.
        var text = File.ReadAllText(SafePath.Join(Root, "keeper/backlog-until.json"));
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("until").GetString()
            .Should().Be(until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void НетаФлага_IsActiveFalseБезИсключения()
    {
        // Мусор — не только битый JSON: файл мог остаться от оборванной записи.
        WikiBacklogFlag.IsActive(Root, Today()).Should().BeFalse("нет файла — бэклога нет");
    }

    [Theory]
    [InlineData("")]                                   // пустой файл
    [InlineData("не json")]                           // не JSON
    [InlineData("{}")]                                // нет until
    [InlineData("""{"until":null}""")]                // until не строка
    [InlineData("""{"until":"2026-13-40"}""")]        // дата не существует
    [InlineData("""{"until":"2026-11-26T00:00:00Z"}""")] // не формат даты
    [InlineData("""{"untilr":"2026-11-26"}""")]       // чужое поле
    public void МусорВФайле_IsActiveFalseБезИсключения(string body)
    {
        var path = SafePath.Join(Root, "keeper/backlog-until.json");
        File.WriteAllText(path, body);

        // Ни одно из этих состояний не должно бросить: по нечитаемом флаге обычный проход.
        WikiBacklogFlag.IsActive(Root, Today().AddYears(-1)).Should().BeFalse(
            "испорченный флаг не активен и не исключение");
    }

    // ── c2: гонка записи и чтения ───────────────────────────────────────────────────

    [Fact]
    public async Task ДвадцатьПараллельныхЗаписейИЧтений_БезИсключенийИОдинЦелыйФлаг()
    {
        var today = Today();
        var dates = Enumerable.Range(1, 20).Select(i => today.AddDays(i)).ToList();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Мутация «писать напрямую без tmp+rename»: FileShare конфликтует на одновременной
        // записи того же файла (или читатель ловит половину строки) — краснеет здесь:
        // errors непустой или итоговый файл не парсится.
        var flagPath = SafePath.Join(Root, "keeper/backlog-until.json");
        // Чтение идёт всё время, пока пишется: плотный цикл, а не «200 чтений когда-нибудь»
        // — иначе промахиваешься окна записи и гонка ничего не проверяет.
        var writingDone = false;
        var reads = 0;
        var writes = Task.Run(() =>
        {
            Parallel.ForEach(dates, d =>
            {
                try { WikiBacklogFlag.Set(Root, d); } catch (Exception e) { errors.Add("Set: " + e.Message); }
            });
            Volatile.Write(ref writingDone, true);
        });
        var reading = Task.Run(() =>
        {
            while (!Volatile.Read(ref writingDone))
            {
                try
                {
                    WikiBacklogFlag.IsActive(Root, today);
                    // Прямое чтение тем же открытием, что делает флаг: при атомарной замене
                    // читатель видит либо прежний целый файл, либо новый целый. Половина файла
                    // (существовал, но пуст или не парсится) — находка: её ловит этот parse.
                    if (File.Exists(flagPath))
                    {
                        using var fs = new FileStream(flagPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var doc = JsonDocument.Parse(fs);
                        var until = DateOnly.ParseExact(doc.RootElement.GetProperty("until").GetString()!,
                            "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (!dates.Contains(until)) errors.Add($"флаг не из записанных дат: {until}");
                    }
                    reads++;
                }
                catch (Exception e) { errors.Add("read: " + e.Message); }
            }
        });
        await Task.WhenAll(writes, reading);

        errors.Should().BeEmpty("параллельная запись и чтение не дают исключений и не видят половины файла");
        reads.Should().BeGreaterThan(0, "чтение действительно шло во время записи");

        var text = File.ReadAllText(SafePath.Join(Root, "keeper/backlog-until.json"));
        var doc = JsonDocument.Parse(text);
        var until = DateOnly.ParseExact(
            doc.RootElement.GetProperty("until").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        dates.Should().Contain(until, "итоговый файл несёт ровно одну из записанных дат");
        Directory.GetFiles(Path.Combine(Root, "keeper"), "*.tmp").Should().BeEmpty(
            "промежуточных файлов записи не остаётся");
    }

    // ── c3: флаг доходит до разбора: лимит новых страниц раздела поднят ─────────────

    // 16 create-операций на 16 новых страниц: утверждение = цитата = своя строка источника
    // (число в утверждении подтверждено той же цитатой, «одна цитата — одна страница» —
    // цитаты разные). Группы A/B — два независимых плана: страницы и цитаты не пересекаются,
    // второй прогон не спотыкается о дедup первого.
    private static string Sentence(string group, int i) => $"Решение {group}{i}: формулировка предмета {group}{i}.";

    private static string SourceTextFor(string group) =>
        string.Join(" ", Enumerable.Range(1, 16).Select(i => Sentence(group, i)));

    private static string PlanJson(string group) => WikiSynthJson.Ok([.. Enumerable.Range(1, 16).Select(i =>
        WikiSynthJson.Op("create", $"Предмет-{group}{i}", Sentence(group, i), [Sentence(group, i)], kind: "decision"))]);

    private static WikiIngestService.Request Req(string section, string card, string ref_, string sourceText) =>
        new("session", ref_, card, sourceText, section, null);

    private WikiIngestService Sut(FakeLlm llm) => new(llm, new NoSpheres(), _apply, _config);

    private IReadOnlyList<string> NewPageFiles(string group)
    {
        var dir = SafePath.JoinBelowRoot(Root, group == "A" ? "pages/Проекты/p1" : "pages/Проекты/p2");
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, $"Предмет-{group}*.md")]
            : [];
    }

    [Fact]
    public async Task ФлагПоднимаетЛимитРаздела_15СтраницПроходят16Ждёт_БезФлагаПять()
    {
        var today = Today();

        // С флагом (поднятый лимит 15): из плана в 16 новых страниц проходят 15,
        // 16-я — единственная сверх поднятого лимита.
        // Мутация «IsBacklogPass = false» в WikiIngestService (флаг есть, политика его
        // не видит): лимит обычный 5 — первый же assert красный.
        WikiBacklogFlag.Set(Root, today.AddDays(14));
        var resBacklog = await Sut(new FakeLlm(PlanJson("A")))
            .IngestAsync(User, Req(SectionA, CardPathA, "session:s1#m1-9", SourceTextFor("A")));
        resBacklog.Outcome.Should().Be(WikiIngestService.OutcomeApplied, resBacklog.Reason);
        var pkgA = WikiPackageStore.TryRead(Root, resBacklog.PackageId!).Package!;
        pkgA.Operations.Should().HaveCount(15,
            "сквозной лимит 5 поднят до 15: страницы 6…15 не получают причину лимита");
        NewPageFiles("A").Should().HaveCount(15, "15 новых страниц раздела применены на диске");

        // Флаг истёк (until вчера): тот же по форме план — обычный лимит 5, страницы 6…16 ждут.
        // Мутация «IsBacklogPass = true» (политика всегда в бэклоге): второй блок красный.
        WikiBacklogFlag.Set(Root, today.AddDays(-1));
        var resPlain = await Sut(new FakeLlm(PlanJson("B")))
            .IngestAsync(User, Req(SectionB, CardPathB, "session:s2#m1-9", SourceTextFor("B")));
        resPlain.Outcome.Should().Be(WikiIngestService.OutcomeApplied, resPlain.Reason);
        var pkgB = WikiPackageStore.TryRead(Root, resPlain.PackageId!).Package!;
        pkgB.Operations.Should().HaveCount(5, "без флага — обычный лимит 5 новых страниц раздела");
        NewPageFiles("B").Should().HaveCount(5, "11 страниц сверх обычного лимита не применены");
    }

    // ── фикстура ────────────────────────────────────────────────────────────────────

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];

        private readonly string _answer;

        public FakeLlm(string answer) => _answer = answer;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(
                Answer(actionKey, prompt), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        public Task<OneShotResult?> RunLocalOrChosenDetailedAsync(string actionKey, string prompt,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<OneShotResult?>(new OneShotResult(
                Answer(actionKey, prompt), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey, string prompt)
        {
            var pass = prompt.Contains("single pass") ? "S" : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(LocalActionCatalog.WikiIngest, "место разбора — то, что назвал вызывающий");
            return _answer;
        }
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
