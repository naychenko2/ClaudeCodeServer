using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.MediaHistory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// fail-closed (ADR-030, 2026-10-18): не-админ без непустого sub не получает «все записи» и не
// роняет сервер. Проверка идёт прямо на контроллерах с подставленным ClaimsPrincipal: токен без
// sub до контроллера не доходит (его отсекает JwtService.IsSessionCurrent, 401 — см.
// JwtServiceTests), поэтому контроллерная проверка — запасная, и держать её надо отдельно.
[Trait("Category", "Host")]
public class MediaSelfScopeFailClosedTests : IDisposable
{
    // Источник результата: отдаёт «файл», чтобы без fail-closed запрос дошёл до выдачи файла.
    // Файл — свой, во временном каталоге (платформонезависимость по CLAUDE.md: жёсткий /tmp/x.png
    // красит тест на машине, где его нет), и удаляется при Dispose.
    private sealed class FakeSource : IMediaHistoryResultSource, IDisposable
    {
        private readonly string _filePath = Path.Combine(
            Path.GetTempPath(), "media-self-scope-" + Guid.NewGuid().ToString("N") + ".png");
        public FakeSource() => File.WriteAllText(_filePath, "png");
        public string Source => "test-source";
        public Task<MediaHistoryResolution?> ResolveAsync(MediaHistoryRef reference, int variant, CancellationToken ct)
            => Task.FromResult<MediaHistoryResolution?>(new MediaHistoryResolution(1, _filePath, "image/png", null));
        public void Dispose() => File.Delete(_filePath);
    }

    private sealed class FakeComfyStats(ComfyMediaStatsSnapshot? snapshot) : IComfyStats
    {
        public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly FakeSource _source = new();

    public MediaSelfScopeFailClosedTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<IMediaHistoryResultSource>(_source),
        };
    }

    public void Dispose()
    {
        _factory.Dispose();
        _source.Dispose();
    }

    // sub == null — claim отсутствует вовсе; иначе claim с этим значением (в т.ч. "" и " ")
    private static ClaimsPrincipal Principal(string? sub, bool admin = false)
    {
        var claims = new List<Claim>();
        if (sub is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, sub));
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    // ─── MediaSelfScope.HasOwner ─────────────────────────────────────────────────

    // Мутация: вернуть true для пустого sub — кейсы null и "" краснеют.
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("u-1", false, true)]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    public void HasOwner_НеАдминуНуженНепустойSub_АдминБезОграничений(string? sub, bool admin, bool expected)
    {
        MediaSelfScope.HasOwner(Principal(sub, admin)).Should().Be(expected);
    }

    // Пробельный sub gate пропускает намеренно: его режет более строгая защита строк/индекса
    // (IsNullOrWhiteSpace) — см. тесты ниже.
    [Fact]
    public void HasOwner_ПробельныйSub_ПропускаетДальше()
    {
        MediaSelfScope.HasOwner(Principal(" ")).Should().BeTrue();
    }

    // ─── /stats/media ────────────────────────────────────────────────────────────

    private static LocalModelStatsController StatsController(ClaimsPrincipal user, ComfyMediaStatsSnapshot snapshot)
    {
        var services = new ServiceCollection()
            .AddSingleton<IComfyStats>(new FakeComfyStats(snapshot))
            .BuildServiceProvider();
        return new LocalModelStatsController(services, Options.Create(new LocalModelStatsMediaOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user, RequestServices = services },
            },
        };
    }

    private static ComfyMediaStatsSnapshot Snapshot(
        IReadOnlyList<ComfyJobRow>? running = null,
        IReadOnlyList<ComfyJobRow>? queue = null,
        IReadOnlyList<ComfyDoneRow>? lastHour = null) => new(
        Configured: true, Status: "working", DownSince: null, Version: "1.0",
        LoadedModels: [], SlotsUsed: 0, SlotsLimit: 4, Gpus: [],
        Running: running ?? [], Queue: queue ?? [], LastHour: lastHour ?? [], Unsupported: []);

    // Не-админ без непустого sub → 404, а не «все строки» и не исключение. Снимок непустой:
    // иначе Media() ответит 200 {configured:false} ДО проверки. Мутация: убрать
    // MediaSelfScope.HasOwner из проверки в Media() — вернётся 200, тест краснеет.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Media_НеАдминБезSub_404(string? sub)
    {
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var ctl = StatsController(Principal(sub), Snapshot(lastHour: [new ComfyDoneRow("image", "t2i", "", "completed", 60, 120, t0)]));

        var result = await ctl.Media(since: null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>("пустой sub — 404, не все строки");
    }

    // Пробельный sub gate пропускает, но строки с пробельным владельцем не совпадают с ним
    // «по равенству»: ответ пуст. Мутация: убрать guard IsNullOrWhiteSpace(myId) — строки
    // с OwnerId " " просочатся, тест краснеет.
    [Fact]
    public async Task Media_ПробельныйSub_СтрокиПробельногоВладельцаНеОтдаются()
    {
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var snap = Snapshot(
            running: [new ComfyJobRow("image", "t2i", false, " ", "s1", "p1", null, null, 40, true, 120, 80, null, t0)],
            queue: [new ComfyJobRow("audio", "tts", false, " ", "s2", "p2", null, null, null, false, 90, null, 30, null)],
            lastHour: [new ComfyDoneRow("image", "t2i", " ", "completed", 60, 120, t0)]);
        var ctl = StatsController(Principal(" "), snap);

        var result = await ctl.Media(since: null, CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<LocalModelStatsController.LocalModelMediaResponse>().Subject;
        body.Scope.Should().Be("self");
        body.Running.Should().BeEmpty();
        body.Queue.Should().BeEmpty();
        body.LastHour.Should().BeEmpty();
        body.QueueAhead.Should().BeNull("своих задач в очереди нет");
        body.Gpus.Should().BeNull("видеокарты не-админу не отдаются");
    }

    // ─── /history и /history/{id}/file ───────────────────────────────────────────

    private MediaHistoryController HistoryController(ClaimsPrincipal user)
    {
        var ctl = ActivatorUtilities.CreateInstance<MediaHistoryController>(_factory.Services);
        ctl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return ctl;
    }

    // Запись журнала (enqueued + finished) с заданным владельцем. Писать надо ДО первого Load индекса.
    private void WriteRecord(string promptId, string ownerId)
    {
        var dir = Path.Combine(_factory.TempDir, "local-media-history");
        Directory.CreateDirectory(dir);
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var origin = new LocalJobOrigin(
            OwnerId: ownerId, SessionId: "chat-1", Kind: "image", Op: "generate_image",
            Heavy: false, ProjectId: "proj-1", Source: "test-source", SourceJobId: "job-1");

        JsonObject Line(string type, string ts, string? model, string? prompt, string? outcome, int? runSeconds) => new()
        {
            ["v"] = 1, ["t"] = type, ["ts"] = ts, ["id"] = promptId,
            ["origin"] = JsonSerializer.SerializeToNode(origin, wire),
            ["model"] = model, ["prompt"] = prompt, ["params"] = null, ["card"] = null,
            ["outcome"] = outcome, ["runSeconds"] = runSeconds, ["error"] = null,
        };

        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        File.AppendAllText(Path.Combine(dir, day + ".jsonl"),
            Line("enqueued", "2026-10-15T10:00:00.0000000Z", "sdxl", "a cat", null, null).ToJsonString() + "\n" +
            Line("finished", "2026-10-15T10:00:47.0000000Z", null, null, "completed", 47).ToJsonString() + "\n");
    }

    private static IActionResult List(MediaHistoryController ctl) =>
        ctl.List(null, null, null, null, null, null, null, null, null);

    // Не-админ без непустого sub → 404 на /history (не «все записи»). В журнале есть запись
    // с пустым владельцем (Foreign): без проверки она попала бы в «свои».
    // Мутация: убрать MediaSelfScope.HasOwner из Enabled() — для "" вернётся 200, тест краснеет
    // (для null отсечёт соседняя строка Enabled: UserId is not { }).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void История_НеАдминБезSub_404(string? sub)
    {
        WriteRecord("p_foreign", ownerId: "");

        List(HistoryController(Principal(sub))).Should().BeOfType<NotFoundResult>();
    }

    // Не-админ без непустого sub → 404 на /history/{id}/file: чужой (Foreign) файл не отдаётся.
    // Мутация: убрать MediaSelfScope.HasOwner из Enabled() — "" совпадёт с OwnerId "" записи,
    // запрос дойдёт до выдачи файла, тест краснеет.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Файл_НеАдминБезSub_404(string? sub)
    {
        WriteRecord("p_foreign", ownerId: "");

        var result = await HistoryController(Principal(sub)).File("p_foreign");

        result.Should().BeOfType<NotFoundResult>();
    }

    // Контроль: проверка не «глушит всё» — не-админ с настоящим sub видит свою запись.
    [Fact]
    public void История_НеАдминСSub_ВидитСвоюЗапись()
    {
        WriteRecord("p_own", ownerId: "u-own");
        WriteRecord("p_foreign", ownerId: "");

        var result = List(HistoryController(Principal("u-own")));

        var page = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<MediaHistoryPage>().Subject;
        page.Scope.Should().Be("self");
        page.Records.Select(r => r.Id).Should().Equal("p_own");
    }
}
