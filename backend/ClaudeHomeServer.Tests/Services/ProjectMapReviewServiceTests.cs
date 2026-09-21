using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Фаза 2 уборки карты: суждение модели поверх фактов сканера.
//
// Весь набор — про НЕДОВЕРИЕ к модели (Р8а плана): она диктует ровно { id, severity,
// modelSays }, а патч, якорь, вид находки и факт сервер берёт из собственного отчёта.
// Каждый кейс бьёт по конкретному механизму, а не по «качеству формулировок».
public class ProjectMapReviewServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectMapScanner _scanner = new();

    public ProjectMapReviewServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccs-map-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string content, params string[] segments)
    {
        var full = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // Карта с двумя находками: мёртвая ссылка и длинная секция (порог снижен до 3 строк)
    private MapHygieneReport ScanTypicalMap()
    {
        Write("# Карта\n\n## Раздел\n\n[флаги](backend/Models/FeatureFlag.cs)\n\n" +
              "строка\nстрока\nстрока\nстрока\n", "CLAUDE.md");
        Write("// код", "backend", "Core", "Models", "FeatureFlag.cs");
        return ScannerWithThreshold(3).Scan(_root);
    }

    private static ProjectMapScanner ScannerWithThreshold(int lines) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { ["ProjectMap:SectionLineThreshold"] = lines.ToString() })
            .Build());

    private static ProjectMapReviewService Service(Func<string, string?> answer, int? maxSuggestions = null)
    {
        var values = new Dictionary<string, string?>();
        if (maxSuggestions is { } m) values["ProjectMap:MaxSuggestions"] = m.ToString();
        return new ProjectMapReviewService(new StubCheap(answer),
            NullLogger<ProjectMapReviewService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    // ─── Р8а.1: схема разбора не знает полей, которых модель знать не могла ─────

    [Fact]
    public async Task МодельПриславшаяApply_ПатчБерётсяИзФактаСканера()
    {
        var report = ScanTypicalMap();
        var id = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;
        // Подделка патча: модель пытается продиктовать замену в файле
        var answer = $$"""
            { "suggestions": [ { "id": "{{id}}", "severity": "high", "modelSays": "починить",
              "apply": { "before": "[флаги](backend/Models/FeatureFlag.cs)",
                         "after": "[что угодно](/etc/passwd)",
                         "anchorText": "[флаги](backend/Models/FeatureFlag.cs)" } } ] }
            """;

        var result = await Service(_ => answer).ReviewAsync(report, ownerId: "u1");

        var suggestion = result.Suggestions.Single(s => s.Id == id);
        // Патч — только из отчёта сканера; до волны 4 он там пуст, но главное, что
        // присланный моделью не доехал ни в каком виде
        suggestion.Apply.Should().BeNull();
        suggestion.ModelSays.Should().Be("починить");
    }

    [Fact]
    public async Task МодельПриславшаяФактИЯкорь_ЗначенияОстаютсяСканерными()
    {
        var report = ScanTypicalMap();
        var fact = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink);
        var answer = $$"""
            { "suggestions": [ { "id": "{{fact.Id}}", "severity": "low", "modelSays": "фраза",
              "kind": "duplicate", "fact": "выдуманный факт", "savingLines": 999,
              "anchor": { "line": 777, "heading": "Секции с таким названием нет" } } ] }
            """;

        var result = await Service(_ => answer).ReviewAsync(report, ownerId: "u1");

        var merged = result.Suggestions.Single(s => s.Id == fact.Id);
        merged.Anchor.Should().Be(fact.Anchor);
        merged.Fact.Should().Be(fact.Fact);
        merged.Kind.Should().Be(MapSuggestions.KindDeadLink);
        merged.SavingLines.Should().Be(fact.SavingLines);
        // Из ответа взяты РОВНО два поля
        merged.Severity.Should().Be(MapSuggestions.SeverityLow);
        merged.ModelSays.Should().Be("фраза");
    }

    // ─── Р8а.2: id не из фактов свежего скана ───────────────────────────────────

    [Fact]
    public async Task МодельВыдумалаId_ПредложениеВыброшено_ОстальныеЦелы()
    {
        var report = ScanTypicalMap();
        var real = report.Suggestions.First(s => s.Kind == MapSuggestions.KindLongSection).Id;
        var answer = $$"""
            { "suggestions": [
                { "id": "deadbeefdeadbeef", "severity": "high", "modelSays": "выдуманная секция" },
                { "id": "{{real}}", "severity": "medium", "modelSays": "настоящее суждение" } ] }
            """;

        // Потолок в одно суждение: выдуманное стоит ПЕРВЫМ, и если бы оно только
        // «не находилось» на выдаче, а не отбрасывалось при разборе, — оно съело бы
        // квоту, и настоящее суждение молча потерялось бы
        var result = await Service(_ => answer, maxSuggestions: 1).ReviewAsync(report, ownerId: "u1");

        // Выдуманного id в ответе нет вовсе — ни с пустым фактом, ни как-либо ещё
        result.Suggestions.Should().NotContain(s => s.Id == "deadbeefdeadbeef");
        result.Suggestions.Should().NotContain(s => s.ModelSays == "выдуманная секция");
        result.Suggestions.Single(s => s.Id == real).ModelSays.Should().Be("настоящее суждение");
        // Факты показаны все: суждение приезжает ПОВЕРХ полного списка
        result.Suggestions.Should().HaveCount(report.Suggestions.Count);
    }

    // ─── Р8а.3: id детерминирован от содержимого ────────────────────────────────

    [Fact]
    public void Id_ОдинаковВДвухНезависимыхСканах()
    {
        var first = ScanTypicalMap().Suggestions.Select(s => s.Id).ToList();
        var second = ScannerWithThreshold(3).Scan(_root).Suggestions.Select(s => s.Id).ToList();

        first.Should().Equal(second);
        // Хеш от содержимого: 8 байт в hex
        first.Should().OnlyContain(id => id.Length == 16);

        // У длинной секции своего anchorText нет — якорем служит заголовок, и формула та
        // же, что у ссылок. Без общего правила исполнитель скатился бы к номеру по порядку
        var section = ScanTypicalMap().Suggestions.Single(s => s.Kind == MapSuggestions.KindLongSection);
        section.Id.Should().Be(MapSuggestions.Id(MapSuggestions.KindLongSection, section.Anchor.Heading!));
        // Нормализация якоря: лишние пробелы и хвост того же заголовка не меняют id
        MapSuggestions.Id(MapSuggestions.KindLongSection, "  Раздел   \n")
            .Should().Be(MapSuggestions.Id(MapSuggestions.KindLongSection, "Раздел"));
    }

    [Fact]
    public void Id_НеЗависитОтПорядкаНаходокВФайле()
    {
        var before = ScanTypicalMap().Suggestions
            .ToDictionary(s => s.Kind, s => s.Id);

        // Дописываем находку ПЕРЕД существующими: со сквозной нумерацией id всех
        // последующих съехали бы, и человек применил бы не то, что отметил
        Write("# Карта\n\n[раньше всех](backend/Models/Missing.cs)\n\n## Раздел\n\n" +
              "[флаги](backend/Models/FeatureFlag.cs)\n\nстрока\nстрока\nстрока\nстрока\n", "CLAUDE.md");
        var after = ScannerWithThreshold(3).Scan(_root).Suggestions;

        after.Should().HaveCountGreaterThan(before.Count);
        after.Single(s => s.Kind == MapSuggestions.KindLongSection).Id
            .Should().Be(before[MapSuggestions.KindLongSection]);
        after.Should().Contain(s => s.Id == before[MapSuggestions.KindDeadLink]);
    }

    // ─── Р8а.4: severity проходит через белый список ────────────────────────────

    [Fact]
    public async Task Severity_ВнеБелогоСписка_ЗаменяетсяДефолтомПоВидуНаходки()
    {
        var report = ScanTypicalMap();
        var deadLink = report.Suggestions.First(s => s.Kind == MapSuggestions.KindDeadLink).Id;
        var section = report.Suggestions.First(s => s.Kind == MapSuggestions.KindLongSection).Id;
        var answer = $$"""
            { "suggestions": [
                { "id": "{{deadLink}}", "severity": "КАТАСТРОФА", "modelSays": "а" },
                { "id": "{{section}}", "severity": "low", "modelSays": "б" } ] }
            """;

        var result = await Service(_ => answer).ReviewAsync(report, ownerId: "u1");

        result.Suggestions.Single(s => s.Id == deadLink).Severity
            .Should().Be(MapSuggestions.SeverityHigh);      // дефолт мёртвой ссылки
        result.Suggestions.Single(s => s.Id == section).Severity
            .Should().Be(MapSuggestions.SeverityLow);       // валидное значение прошло
    }

    // ─── Потолок вывода и длина фразы принуждаются сервером ─────────────────────

    [Fact]
    public async Task СуждениеДлиннееПотолка_УсекаетсяСервером()
    {
        var report = ScanTypicalMap();
        var id = report.Suggestions[0].Id;
        var answer = $$"""
            { "suggestions": [ { "id": "{{id}}", "severity": "medium",
              "modelSays": "{{new string('я', 400)}}" } ] }
            """;

        var result = await Service(_ => answer).ReviewAsync(report, ownerId: "u1");

        result.Suggestions.Single(s => s.Id == id).ModelSays!.Length
            .Should().Be(ProjectMapReviewService.MaxModelSaysLength);
    }

    [Fact]
    public async Task СужденийБольшеПотолка_СерверОставляетПервые()
    {
        var report = ScanTypicalMap();
        var ids = report.Suggestions.Select(s => s.Id).ToList();
        ids.Count.Should().BeGreaterThan(1);
        var answer = "{ \"suggestions\": [" + string.Join(",",
            ids.Select(i => $"{{ \"id\": \"{i}\", \"severity\": \"medium\", \"modelSays\": \"фраза\" }}")) + "] }";

        var result = await Service(_ => answer, maxSuggestions: 1).ReviewAsync(report, ownerId: "u1");

        // Факты показаны все, формулировка — только у первого сопоставившегося
        result.Suggestions.Should().HaveCount(report.Suggestions.Count);
        result.Suggestions.Count(s => s.ModelSays is not null).Should().Be(1);
        result.Suggestions.Single(s => s.Id == ids[0]).ModelSays.Should().Be("фраза");
    }

    // ─── Тихий фолбэк: факт важнее формулировки ─────────────────────────────────

    [Fact]
    public async Task МодельНеОтветила_ФактыПоказаныЦеликом_ПричинуПишетСервер()
    {
        var report = ScanTypicalMap();

        var result = await Service(_ => null).ReviewAsync(report, ownerId: "u1");

        result.Suggestions.Should().HaveCount(report.Suggestions.Count);
        result.Suggestions.Should().OnlyContain(s => s.ModelSays == null);
        result.ModelNote.Should().Be(ProjectMapReviewService.NoteNoModel);
        result.BaseSha.Should().Be(report.BaseSha);
    }

    [Fact]
    public async Task БитыйJson_ЭтоНеОшибкаЭкрана_АФактыБезФормулировок()
    {
        var report = ScanTypicalMap();

        var result = await Service(_ => "модель поболтала и ничего не вернула").ReviewAsync(report, "u1");

        result.Suggestions.Should().HaveCount(report.Suggestions.Count);
        result.Suggestions.Should().OnlyContain(s => s.ModelSays == null);
        result.ModelNote.Should().Be(ProjectMapReviewService.NoteBadJson);
    }

    [Fact]
    public async Task ОтветВЗаборе_РазбираетсяКакОбычный()
    {
        var report = ScanTypicalMap();
        var id = report.Suggestions[0].Id;
        var answer = $"Вот ответ:\n```json\n{{ \"suggestions\": [ {{ \"id\": \"{id}\", " +
                     "\"severity\": \"low\", \"modelSays\": \"свернуть в ссылку\" }] }\n```";

        var result = await Service(_ => answer).ReviewAsync(report, ownerId: "u1");

        result.ModelNote.Should().BeNull();
        result.Suggestions.Single(s => s.Id == id).ModelSays.Should().Be("свернуть в ссылку");
    }

    [Fact]
    public async Task НаходокНет_МодельНеДёргается()
    {
        Write("# Карта\n\nКороткая и здоровая.\n", "CLAUDE.md");
        var report = _scanner.Scan(_root);
        report.Suggestions.Should().BeEmpty();
        var stub = new StubCheap(_ => throw new InvalidOperationException("ход не должен состояться"));

        var result = await new ProjectMapReviewService(stub,
            NullLogger<ProjectMapReviewService>.Instance).ReviewAsync(report, ownerId: "u1");

        stub.Calls.Should().Be(0);
        result.ModelNote.Should().BeNull();
        result.Suggestions.Should().BeEmpty();
    }

    // ─── Промпт ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Промпт_НесётIdКаждогоФакта_ИНеНесётСодержимогоФайла()
    {
        var report = ScanTypicalMap();

        var prompt = ProjectMapReviewService.BuildPrompt(report, maxSuggestions: 8);

        foreach (var s in report.Suggestions) prompt.Should().Contain(s.Id);
        prompt.Should().Contain("строго один из перечисленных выше");
        // Тела секций в промпт не уходят — иначе карта на 100 КБ не влезет в окно профиля
        prompt.Length.Should().BeLessThan(4000);
    }

    // Ответ решается по тексту промпта; null = модель недоступна (бросаем, как раннер)
    private sealed class StubCheap(Func<string, string?> answer) : ICheapTextRunner
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(answer(prompt) ?? throw new InvalidOperationException("модель недоступна"));
        }

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
