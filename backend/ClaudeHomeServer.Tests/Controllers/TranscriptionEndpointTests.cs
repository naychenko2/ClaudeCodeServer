using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/local-model/transcription (вкладка «Встречи», Б1): только admin (роль на действии,
// как у hardware/farm → не-админ 403, аноним 401); админ получает снимок как есть — уровни и блоки
// считает бэкенд, контроллер их не трогает. Форма ответа — плоская по proposal.md (поля блоков
// на верхнем уровне, вложенных pipeline/now/queue нет): проверяется на живом JSON ответа, а не
// на типе. Устойчивость читателя (пустые блоки вместо 500) — в TranscriptionStateReaderTests;
// здесь — права, то, что ручка отдаёт снимок без изменений, и плоская форма контракта.
[Trait("Category", "Host")]
public class TranscriptionEndpointTests : IClassFixture<TranscriptionEndpointTests.Host>, IDisposable
{
    // Фиксированный снимок: форма ответа проверяется на нём, а не на состоянии живой фермы.
    private static readonly TranscriptionSnapshot Fixed = new()
    {
        Phase = "idle",
        UpdatedAt = 1_768_000_000_000,
        Host = "worker",
        Pid = 4242,
        Alive = true,
        OutputHost = "worker",
        OutputDir = "/srv/ccs/home/andrey/Встречи",
        Level = "ok",
        SummaryQueue = 0,
        Deps =
        [
            new("asr", "unknown", 1_768_000_000_000), new("summary", "unknown", 1_768_000_000_000),
            new("dify", "unknown", 1_768_000_000_000), new("share", "ok", 1_768_000_000_000),
        ],
    };

    // Верхний уровень ответа — ровно поля блоков proposal.md + level (уровень точки считает
    // бэкенд). Ничего сверх них: ни вложенных блоков, ни прежних имён (summaryCount, status).
    private static readonly string[] WireFields =
    [
        "phase", "updatedAt", "host", "pid", "alive", "outputHost", "outputDir", "level",
        "file", "folder", "stage", "stageStartedAt", "durationMin", "batchIndex", "batchSize",
        "asrQueue", "summaryQueue", "fails", "recent", "dify", "checkedAt", "deps",
    ];

    private sealed class FakeReader : ITranscriptionStateReader
    {
        public TranscriptionSnapshot Read() => Fixed;
    }

    /// <summary>
    /// Хост на класс: подставной читатель зарегистрирован один раз на весь класс — он перекрывает
    /// реальный (тот читал бы живой конвейер worker-машины).
    /// </summary>
    public sealed class Host : TestWebApplicationFactory
    {
        public Host()
        {
            ExtraServices = s => s.AddSingleton<ITranscriptionStateReader>(new FakeReader());
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public TranscriptionEndpointTests(Host host)
    {
        _factory = host;
        _admin = host.CreateAuthenticatedClient();
        _user = host.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // Хост гасит фикстура после класса; здесь — только клиенты теста
    public void Dispose() { _admin.Dispose(); _user.Dispose(); }

    // Мутация: снять [Authorize(Roles="admin")] с действия — не-админ получит 200, тест краснеет.
    [Fact]
    public async Task НеАдмин_403() =>
        (await _user.GetAsync("/api/local-model/transcription")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Аноним_401() =>
        (await _factory.CreateClient().GetAsync("/api/local-model/transcription")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Админ_200_СнимокКакЕсть()
    {
        var r = await _admin.GetAsync("/api/local-model/transcription");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        var s = await r.Content.ReadFromJsonAsync<TranscriptionSnapshot>() ?? throw new InvalidOperationException("пустой ответ");
        s.Level.Should().Be("ok");
        s.Phase.Should().Be("idle");
        s.Host.Should().Be("worker");
        s.OutputDir.Should().Be("/srv/ccs/home/andrey/Встречи");
        s.SummaryQueue.Should().Be(0);
        s.Deps.Should().HaveCount(4);
        s.Deps.Should().Contain(d => d.Key == "share" && d.State == "ok");
    }

    // Плоский контракт (пункт приёмки 1): верхний уровень ответа — ровно поля proposal.md + level;
    // вложенных pipeline/now/queue и прежних имён нет; deps[] несёт state, не status.
    // Мутация «убрать разворачивание в плоский набор» (снимок снова вложенный) краснит этот тест.
    [Fact]
    public async Task Админ_200_ПлоскийОтветБезВложенныхБлоков()
    {
        var r = await _admin.GetAsync("/api/local-model/transcription");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().BeEquivalentTo(WireFields);

        foreach (var dep in doc.RootElement.GetProperty("deps").EnumerateArray())
        {
            dep.TryGetProperty("state", out _).Should().BeTrue();
            dep.TryGetProperty("status", out _).Should().BeFalse();
        }
    }
}

/// <summary>
/// Живой ответ ручки с БОЕВЫМ читателем (не подменённым): метка начала текущей операции
/// доходит до JSON как есть — stage_started_at снимка конвейера (unix-сек) становится
/// stageStartedAt (unix-мс); при отсутствии операции поля в ответе нет (null). Фикстура
/// конвейера — temp-каталог, путь к нему подменён Transcription:PipelineRoot; файлы
/// переписываются между тестами: читатель читает их в момент запроса.
/// Мутация «подставить вместо метки начала updated» краснит первый тест (пункт приёмки 3).
/// </summary>
[Trait("Category", "Host")]
public class TranscriptionStageStartedAtTests : IClassFixture<TranscriptionStageStartedAtTests.Host>, IDisposable
{
    public sealed class Host : TestWebApplicationFactory
    {
        // Корень фикстуры конвейера: под ним logs/state.json, как у живой установки.
        public string PipelineRoot { get; } =
            Path.Combine(Path.GetTempPath(), "ccs-transc-endpoint-" + Guid.NewGuid().ToString("N"));

        public Host()
        {
            Directory.CreateDirectory(Path.Combine(PipelineRoot, "logs"));
            ExtraConfig["Transcription:PipelineRoot"] = PipelineRoot;
        }

        // Фикстура конвейера живёт вместе с хостом (на весь класс): xUnit создаёт экземпляр
        // тестового класса на каждый тест и зовёт Dispose каждого — чистить её там нельзя.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try { Directory.Delete(PipelineRoot, true); } catch (IOException) { }
        }
    }

    private readonly Host _host;
    private readonly HttpClient _admin;

    public TranscriptionStageStartedAtTests(Host host)
    {
        _host = host;
        _admin = host.CreateAuthenticatedClient();
    }

    // Хост и фикстуру конвейера гасит фикстура после класса; здесь — только клиент теста
    public void Dispose() => _admin.Dispose();

    // Снимок конвейера фикстуры: updated — «сейчас» (снимок свежий), pid — сам тестовый
    // процесс (на Linux /proc/<pid> есть; на Windows его нет — это меняет только phase/alive,
    // а метку начала BuildNow считает по фазе и файлу, поэтому тест платформонезависим).
    private void WriteState(string phase = "транскрибация", string? current = "X.mp4",
        string? folder = "Video-RR", long? stageStartedAt = null)
    {
        var f = new List<string>
        {
            $"\"phase\": \"{phase}\"",
            $"\"updated\": \"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\"",
            $"\"pid\": {Environment.ProcessId}",
        };
        if (current is not null) f.Add($"\"current\": \"{current}\"");
        if (folder is not null) f.Add($"\"folder\": \"{folder}\"");
        if (stageStartedAt is not null) f.Add($"\"stage_started_at\": {stageStartedAt}");
        File.WriteAllText(
            Path.Combine(_host.PipelineRoot, "logs", "state.json"), "{ " + string.Join(", ", f) + " }");
    }

    private async Task<JsonElement> ResponseRoot()
    {
        var r = await _admin.GetAsync("/api/local-model/transcription");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ЖивойОтветStageStartedAtИзМеткиНачалаОперации()
    {
        var stageSec = DateTimeOffset.Now.Subtract(TimeSpan.FromMinutes(12)).ToUnixTimeSeconds();
        WriteState(stageStartedAt: stageSec);

        var json = await ResponseRoot();

        json.GetProperty("stage").GetString().Should().Be("asr");
        json.GetProperty("stageStartedAt").GetInt64().Should().Be(stageSec * 1000); // unix-сек → unix-мс
        // возраст снимка — своё поле, и он не равен началу операции (именно это чинила подмена)
        json.GetProperty("updatedAt").GetInt64().Should().NotBe(stageSec * 1000);
    }

    [Fact]
    public async Task ЖивойОтветБезОперацииStageStartedAtNull()
    {
        WriteState(phase: "ожидание", current: null, folder: null);

        var json = await ResponseRoot();

        json.GetProperty("stage").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("stageStartedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
