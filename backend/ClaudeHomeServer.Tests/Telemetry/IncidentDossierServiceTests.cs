using ClaudeHomeServer.Telemetry.Alerts;
using ClaudeHomeServer.Telemetry.Incidents;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Сборка досье из ЗАФИКСИРОВАННЫХ ответов SigNoz: разрез, упавшие ходы, чужой контур,
/// выключенная телеметрия.
///
/// Отдельно стережём главное ограничение владельца: сбор досье детерминирован и модель
/// в нём не участвует — сервис не знает ни одного LLM-типа, вызывать её просто нечем.
/// </summary>
public class IncidentDossierServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-incidents-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private const string AlertsJson = """
    {"data":[
      {"fingerprint":"fp-1","status":{"state":"active"},"startsAt":"2026-08-19T10:30:00Z",
       "labels":{"alertname":"Всплеск ошибок LLM","deployment.environment":"dev",
                 "severity":"warning","ruleId":"rule-7"},
       "annotations":{"description":"Ходы падают чаще обычного"}},
      {"fingerprint":"fp-prod","status":{"state":"active"},"startsAt":"2026-08-19T10:00:00Z",
       "labels":{"alertname":"Всплеск ошибок LLM","deployment.environment":"production","severity":"critical"},
       "annotations":{"description":"Прод падает"}}
    ]}
    """;

    private const string BreakdownJson = """
    {"data":{"data":{"results":[{"aggregations":[{"series":[
      {"labels":{"error_type":"rate_limit"},"values":[{"value":4}]}
    ]}]}]}}}
    """;

    private const string TurnsJson = """
    {"data":{"data":{"results":[{"rows":[
      {"timestamp":"2026-08-19T10:31:00Z","data":{"chat_id":"chat-1","provider":"claude",
       "model":"opus","error_type":"rate_limit","duration_nano":1000000}},
      {"timestamp":"2026-08-19T10:32:00Z","data":{"chat_id":"chat-1","provider":"claude",
       "model":"opus","error_type":"rate_limit","duration_nano":1000000}}
    ]}]}}}
    """;

    private const string LogsJson = """
    {"data":{"data":{"results":[{"rows":[
      {"timestamp":"2026-08-19T10:31:05Z","data":{"severity_text":"Error","body":"429 от провайдера"}}
    ]}]}}}
    """;

    /// <summary>Клиент с зафиксированными ответами: по requestType/пути отдаём нужное тело.</summary>
    private sealed class FakeClient(string? alerts, string? breakdown, string? turns, string? logs)
        : ISignozQueryClient
    {
        public readonly List<string> Bodies = [];

        public Task<IReadOnlyList<SignozAlert>?> FetchAlertsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SignozAlert>?>(
                alerts is null ? null : AlertDigest.Parse(alerts));

        public Task<string?> QueryRangeAsync(string body, CancellationToken ct)
        {
            Bodies.Add(body);
            var answer = body.Contains("\"metrics\"") ? breakdown
                : body.Contains("\"traces\"") ? turns
                : logs;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeLocalContext(params IncidentChat[] chats) : IIncidentLocalContext
    {
        public IReadOnlyList<IncidentTurn> Seen = [];

        public IReadOnlyList<IncidentChat> Describe(
            IReadOnlyList<IncidentTurn> turns, DateTimeOffset from, DateTimeOffset to)
        {
            Seen = turns;
            return chats;
        }
    }

    private AlertStateStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        return new AlertStateStore(config, NullLogger<AlertStateStore>.Instance);
    }

    private static IncidentsOptions Options(bool configured = true, string environment = "dev")
        => new()
        {
            SignozUrl = "http://localhost:3301/telemetry-proxy",
            ApiKey = configured ? "key" : null,
            Environment = environment,
        };

    [Fact]
    public async Task Build_CollectsBreakdownTurnsAndChats()
    {
        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var local = new FakeLocalContext(new IncidentChat("chat-1", "prj-1", "Разбор багов", 2, 1500, ["tasks_create"]));
        var service = new IncidentDossierService(Options(), client, NewStore(), local);

        var dossier = await service.BuildAsync("fp-1", CancellationToken.None);

        dossier.Should().NotBeNull();
        dossier!.Status.Should().Be(IncidentStatus.Ok);
        dossier.Incident.Title.Should().StartWith("Всплеск ошибок LLM");
        dossier.Incident.Severity.Should().Be("warning");
        dossier.BreakdownTag.Should().Be("error_type");
        dossier.Breakdown.Should().ContainSingle().Which.Label.Should().Be("rate_limit");
        dossier.Turns.Should().HaveCount(2);
        dossier.Chats.Should().ContainSingle().Which.ChatId.Should().Be("chat-1");
        dossier.Logs.Should().ContainSingle();
        dossier.RulePath.Should().Be("/alerts/overview?ruleId=rule-7", "путь относительный: базу подставит фронт своим пробросом");
        local.Seen.Should().HaveCount(2, "локальный контекст получает те же ходы, что и карточка");
    }

    [Fact]
    public async Task Build_ForeignEnvironment_MarksItAndSkipsLocalChats()
    {
        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var local = new FakeLocalContext(new IncidentChat("chat-1", null, null, 2, 0, []));
        // Инстанс dev, а алерт fp-prod — боевого контура
        var service = new IncidentDossierService(Options(environment: "dev"), client, NewStore(), local);

        var dossier = await service.BuildAsync("fp-prod", CancellationToken.None);

        dossier!.IsForeignEnvironment.Should().BeTrue();
        dossier.Chats.Should().BeEmpty("чатов чужого контура на этом инстансе нет и быть не может");
        dossier.Breakdown.Should().NotBeEmpty("разрез по чужому контуру всё равно показываем");
    }

    [Fact]
    public async Task Build_NotConfigured_ReturnsHonestStatus()
    {
        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var service = new IncidentDossierService(
            Options(configured: false), client, NewStore(), new FakeLocalContext());

        var dossier = await service.BuildAsync("fp-1", CancellationToken.None);

        dossier!.Status.Should().Be(IncidentStatus.NotConfigured);
        dossier.Breakdown.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_SignozSilent_ReturnsUnavailable()
    {
        var client = new FakeClient(alerts: null, breakdown: null, turns: null, logs: null);
        var service = new IncidentDossierService(Options(), client, NewStore(), new FakeLocalContext());

        var dossier = await service.BuildAsync("fp-1", CancellationToken.None);

        dossier!.Status.Should().Be(IncidentStatus.Unavailable);
    }

    [Fact]
    public async Task Build_UnknownFingerprint_ReturnsNull()
    {
        // Протухший диплинк из уведомления: инцидента нет ни среди горящих, ни в истории
        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var service = new IncidentDossierService(Options(), client, NewStore(), new FakeLocalContext());

        var dossier = await service.BuildAsync("fp-неизвестный", CancellationToken.None);

        dossier.Should().BeNull();
    }

    [Fact]
    public async Task Build_ResolvedIncident_ComesFromHistory()
    {
        var store = NewStore();
        store.Remember("fp-old", new AlertMemo(
            "Отказы MCP-инструментов", DateTimeOffset.UtcNow.AddHours(-2),
            Severity: "warning", Environment: "dev", RuleId: "rule-4"));
        store.MarkResolved(["fp-old"]);

        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var service = new IncidentDossierService(Options(), client, store, new FakeLocalContext());

        var dossier = await service.BuildAsync("fp-old", CancellationToken.None);

        dossier.Should().NotBeNull();
        dossier!.Incident.IsFiring.Should().BeFalse();
        dossier.Incident.ResolvedAt.Should().NotBeNull();
        dossier.BreakdownTag.Should().Be("tool_name", "разрез выбирается по имени правила");
        dossier.RulePath.Should().Contain("ruleId=rule-4", "путь строится из памятки, алерта уже нет");
    }

    [Fact]
    public async Task List_ShowsFiringAboveResolved()
    {
        var store = NewStore();
        store.Remember("fp-old", new AlertMemo("Старый", DateTimeOffset.UtcNow.AddHours(-5)));
        store.MarkResolved(["fp-old"]);

        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var service = new IncidentDossierService(Options(), client, store, new FakeLocalContext());

        var (status, items) = await service.ListAsync(CancellationToken.None);

        status.Should().Be(IncidentStatus.Ok);
        items.Should().HaveCount(3);
        items.Take(2).Should().OnlyContain(i => i.IsFiring);
        items.Last().Fingerprint.Should().Be("fp-old");
    }

    [Fact]
    public async Task List_NotConfigured_SaysSoInsteadOfEmptyOk()
    {
        // Пустой список при выключенной телеметрии читался бы как «всё тихо» — самое
        // опасное враньё для этой фичи
        var client = new FakeClient(AlertsJson, BreakdownJson, TurnsJson, LogsJson);
        var service = new IncidentDossierService(
            Options(configured: false), client, NewStore(), new FakeLocalContext());

        var (status, items) = await service.ListAsync(CancellationToken.None);

        status.Should().Be(IncidentStatus.NotConfigured);
        items.Should().BeEmpty();
    }

    [Fact]
    public void Service_HasNoLlmDependencies()
    {
        // Жёсткое ограничение владельца: досье собирает детерминированный код, модель
        // зовётся только по кнопке «Объяснить» в контроллере
        var parameters = typeof(IncidentDossierService).GetConstructors().Single().GetParameters();

        parameters.Select(p => p.ParameterType.Name)
            .Should().NotContain(name => name.Contains("Llm") || name.Contains("Runner")
                                         || name.Contains("Ollama") || name.Contains("Claude"));
    }
}
