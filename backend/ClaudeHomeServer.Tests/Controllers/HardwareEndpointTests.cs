using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/local-model/hardware (вкладка «Железо», Б1): только admin (роль на действии → 403 не-админу),
// неверное window → 400. Поле hardware в stats и
// stats/media — только у админа (у не-админа scope "self" его нет).
[Trait("Category", "Host")]
public class HardwareEndpointTests : IClassFixture<HardwareEndpointTests.Host>, IDisposable
{
    private sealed class FakeComfyStats : IComfyStats
    {
        public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct) =>
            Task.FromResult<ComfyMediaStatsSnapshot?>(new ComfyMediaStatsSnapshot(
                Configured: true, Status: "working", DownSince: null, Version: "1.0", Gpus: [], LoadedModels: [],
                SlotsUsed: 0, SlotsLimit: 4, Running: [], Queue: [], LastHour: [], Unsupported: []));
    }

    private sealed class FakeReader : ILocalModelStatsReader
    {
        public LocalModelStatsCurrent Current { get; } = new() { Configured = true, Status = "working" };
        public double? TokensPerStep => null;
        public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null) => [];
    }

    /// <summary>
    /// Хост на класс: заглушки датчиков зарегистрированы один раз на весь класс (не на тест).
    /// </summary>
    public sealed class Host : TestWebApplicationFactory
    {
        public Host()
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IComfyStats>(new FakeComfyStats());
                s.AddSingleton<ILocalModelStatsReader>(new FakeReader());
            };
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public HardwareEndpointTests(Host host)
    {
        _factory = host;
        _admin = host.CreateAuthenticatedClient();
        _user = host.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // Хост гасит фикстура после класса; здесь — только клиенты теста
    public void Dispose() { _admin.Dispose(); _user.Dispose(); }

    // Мутация: убрать [Authorize(Roles="admin")] с действия — не-админ получит 200, тест краснеет.
    [Fact]
    public async Task НеАдмин_403() =>
        (await _user.GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Аноним_401() =>
        (await _factory.CreateClient().GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("bad")] [InlineData("30m")] [InlineData("2h")]
    public async Task НеверноеОкно_400(string window) =>
        (await _admin.GetAsync($"/api/local-model/hardware?window={window}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Theory]
    [InlineData("1m")] [InlineData("5m")] [InlineData("15m")] [InlineData("1h")]
    public async Task ВерноеОкно_200_КонтрактОтвета(string window)
    {
        var resp = await _admin.GetAsync($"/api/local-model/hardware?window={window}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok", "нет датчиков (Testing) — пустые узлы, не 500");
        body.GetProperty("level").GetString().Should().Be("ok");
        body.GetProperty("nodes").GetArrayLength().Should().Be(0);
        body.GetProperty("stepMs").GetInt64().Should().BeGreaterThan(0);
    }

    // hardware есть у админа в stats и stats/media...
    [Fact]
    public async Task Админ_StatsИMedia_ИмеютHardware()
    {
        var stats = await (await _admin.GetAsync("/api/local-model/stats")).Content.ReadFromJsonAsync<JsonElement>();
        stats.TryGetProperty("hardware", out var h1).Should().BeTrue();
        h1.GetProperty("level").GetString().Should().Be("ok");

        var media = await (await _admin.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
        media.GetProperty("scope").GetString().Should().Be("all");
        media.TryGetProperty("hardware", out _).Should().BeTrue();
    }

    // ...а у не-админа (scope "self") поля нет. Мутация: убрать `isAdmin ?` в контроллере — тест краснеет.
    [Fact]
    public async Task НеАдмин_Media_БезHardware()
    {
        var media = await (await _user.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
        media.GetProperty("scope").GetString().Should().Be("self");
        media.TryGetProperty("hardware", out _).Should().BeFalse("hardware отдаётся только админу");
    }

    // ─── Узлы фермы (Ш3b) ───

    [Fact]
    public async Task Hardware_БезNode_ЭтоWorker_СПолямиУзла()
    {
        var resp = await _admin.GetAsync("/api/local-model/hardware");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("node").GetProperty("key").GetString().Should().Be("worker");
        body.TryGetProperty("uptimeS", out _).Should().BeTrue();
        body.TryGetProperty("cpuLoadPerc", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Hardware_НеизвестныйУзел_404() =>
        (await _admin.GetAsync("/api/local-model/hardware?node=nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    // Мутация: убрать [Authorize(Roles="admin")] с hardware — не-админ получит 200/404 вместо 403.
    [Theory]
    [InlineData("")] [InlineData("?node=worker")] [InlineData("?node=nope")]
    public async Task Hardware_НеАдмин_Отказ_ЛюбойУзел(string query) =>
        (await _user.GetAsync("/api/local-model/hardware" + query)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Farm_Админ_СводкаУзлов()
    {
        var resp = await _admin.GetAsync("/api/local-model/hardware/farm");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("farmNodes").GetArrayLength().Should().Be(1);
        body.GetProperty("farmNodes")[0].GetProperty("key").GetString().Should().Be("worker");
        body.GetProperty("level").GetString().Should().Be("ok");
    }

    // Мутация: убрать [Authorize(Roles="admin")] с hardware/farm — тест краснеет.
    [Fact]
    public async Task Farm_НеАдмин_403() =>
        (await _user.GetAsync("/api/local-model/hardware/farm")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Farm_Аноним_401() =>
        (await _factory.CreateClient().GetAsync("/api/local-model/hardware/farm")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Stats_НеАдмин_Отказ() =>
        (await _user.GetAsync("/api/local-model/stats")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Админ_Brief_СодержитУзлы()
    {
        var stats = await (await _admin.GetAsync("/api/local-model/stats")).Content.ReadFromJsonAsync<JsonElement>();
        var nodes = stats.GetProperty("hardware").GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(1);
        nodes[0].GetProperty("key").GetString().Should().Be("worker");
        var media = await (await _admin.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
        media.GetProperty("hardware").GetProperty("nodes").GetArrayLength().Should().Be(1);
    }

    // Два узла в конфиге → сводка отдаёт оба, в порядке конфига.
    [Trait("Category", "Host")]
    public sealed class ДваУзла : IClassFixture<ДваУзла.TwoNodesHost>
    {
        public sealed class TwoNodesHost : TestWebApplicationFactory
        {
            public TwoNodesHost()
            {
                ExtraConfig["HardwareNodes:0:Key"] = "worker";
                ExtraConfig["HardwareNodes:0:Source"] = "local";
                ExtraConfig["HardwareNodes:1:Key"] = "ai";
                ExtraConfig["HardwareNodes:1:Source"] = "http://127.0.0.1:9105";
                // Блок hardware в media — только на пути непустого снимка Comfy (ветка {configured:false}
                // его не содержит) — та же заглушка, что в Host класса.
                ExtraServices = s => s.AddSingleton<IComfyStats>(new FakeComfyStats());
            }
        }

        private readonly HttpClient _admin;
        public ДваУзла(TwoNodesHost host) => _admin = host.CreateAuthenticatedClient();

        [Fact]
        public async Task Farm_ДваУзла()
        {
            var farm = await (await _admin.GetAsync("/api/local-model/hardware/farm")).Content.ReadFromJsonAsync<JsonElement>();
            farm.GetProperty("farmNodes").GetArrayLength().Should().Be(2);
            farm.GetProperty("farmNodes")[1].GetProperty("key").GetString().Should().Be("ai");
        }

        // Список nodes админского /stats/media — тот же, что в реестре: оба узла, в порядке конфига,
        // с уровнями — не только длина (какой узел, какой уровень). Мутация (c2): в Brief()
        // [.. all.Select(…)] → [.. all.Select(…).Take(1)] — остаётся первый узел, тест краснеет.
        [Fact]
        public async Task StatsMedia_ДваУзла_СоставNodes()
        {
            var media = await (await _admin.GetAsync("/api/local-model/stats/media")).Content.ReadFromJsonAsync<JsonElement>();
            var nodes = media.GetProperty("hardware").GetProperty("nodes");
            nodes.GetArrayLength().Should().Be(2, "оба узла реестра, не первый");
            nodes[0].GetProperty("key").GetString().Should().Be("worker");
            nodes[0].GetProperty("level").GetString().Should().Be("ok");
            nodes[1].GetProperty("key").GetString().Should().Be("ai");
            nodes[1].GetProperty("level").GetString().Should().Be("ok", "недоступный узел не красится: красится только измеренное");
        }
    }

    // Реестр без узла worker: ручка без node — 404, не пустое тело; farm — по реестру.
    [Trait("Category", "Host")]
    public sealed class БезWorker : IClassFixture<БезWorker.NoWorkerHost>
    {
        public sealed class NoWorkerHost : TestWebApplicationFactory
        {
            public NoWorkerHost()
            {
                ExtraConfig["HardwareNodes:0:Key"] = "solo";
                ExtraConfig["HardwareNodes:0:Source"] = "local";
            }
        }

        private readonly HttpClient _admin;
        public БезWorker(NoWorkerHost host) => _admin = host.CreateAuthenticatedClient();

        [Fact]
        public async Task БезNode_404() =>
            (await _admin.GetAsync("/api/local-model/hardware")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Fact]
        public async Task УзелИзРеестра_200()
        {
            (await _admin.GetAsync("/api/local-model/hardware?node=solo")).StatusCode.Should().Be(HttpStatusCode.OK);
            var farm = await (await _admin.GetAsync("/api/local-model/hardware/farm")).Content.ReadFromJsonAsync<JsonElement>();
            farm.GetProperty("farmNodes")[0].GetProperty("key").GetString().Should().Be("solo");
        }
    }

    // Нет сервиса отчёта (снят Singleton IHardwareReportService — ветка эпохи отключаемой llm,
    // ADR-014 §5): ручки не должны падать в 500. Контракт ветки — комментарий контроллера
    // (LocalModelStatsController.cs:405,422): hardware — 200 пустой отчёт (узла нет),
    // farm — 200 с пустым farmNodes. ExtraServices идёт после регистраций Program.cs (тот же
    // путь, что noComfy в LocalModelStatsControllerTests).
    [Trait("Category", "Host")]
    public sealed class БезСервисаОтчёта : IClassFixture<БезСервисаОтчёта.NoServiceHost>
    {
        public sealed class NoServiceHost : TestWebApplicationFactory
        {
            public NoServiceHost() => ExtraServices = s => s.RemoveAll(typeof(IHardwareReportService));
        }

        private readonly HttpClient _admin;
        public БезСервисаОтчёта(NoServiceHost host) => _admin = host.CreateAuthenticatedClient();

        // Мутация (c1): снять в контроллере `?? new HardwareFarmReport(…, [])` (разыменование null)
        // — необработанное исключение даёт 500, тест краснеет.
        [Fact]
        public async Task Farm_НетСервиса_200_ПустойСписок()
        {
            var resp = await _admin.GetAsync("/api/local-model/hardware/farm");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "нет сервиса отчёта — пустая сводка, не 500");
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("level").GetString().Should().Be("ok");
            body.GetProperty("farmNodes").GetArrayLength().Should().Be(0);
        }

        // Та же ветка у ручки узла: 200 пустой отчёт, не 500; узла нет — node null.
        [Fact]
        public async Task Hardware_НетСервиса_200_ПустойОтчёт()
        {
            var resp = await _admin.GetAsync("/api/local-model/hardware");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "нет сервиса отчёта — пустой отчёт, не 500");
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().Be("ok");
            body.GetProperty("level").GetString().Should().Be("ok");
            body.GetProperty("nodes").GetArrayLength().Should().Be(0);
            body.GetProperty("node").ValueKind.Should().Be(JsonValueKind.Null, "узла, к которому относится отчёт, нет");
        }
    }
}
