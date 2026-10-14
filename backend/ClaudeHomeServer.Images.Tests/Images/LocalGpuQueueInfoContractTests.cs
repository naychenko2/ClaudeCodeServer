using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Контракт JSON снимка очереди GPU (LocalGpuQueueInfo, шаг 35, ревью): сериализуем теми же
// опциями, что REST (Program.cs AddJsonOptions) и SignalR (AddJsonProtocol) — JsonSerializerDefaults.Web
// (camelCase, без DefaultIgnoreCondition). Проверяем: downSince — ЧИСЛО unix-мс (не ISO-строка),
// percentExact присутствует, лишних полей (ownerId/sessionId/prompt/slug/promptId) нет, null-поля
// пишутся как null (правило модуля). Мутация: вернуть DownSince в DateTimeOffset? — downSince станет
// строкой и тест краснеет.
public class LocalGpuQueueInfoContractTests
{
    // Опции сериализации приложения: Web = camelCase + case-insensitive (REST и SignalR по умолчанию)
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    // unix-мс момента первого сбоя: 2026-10-14T03:00:00Z
    private const long DownSinceMs = 1_760_462_800_000L;

    [Fact]
    public void DownSince_Сериализуется_КакЧислоUnixMs_АНеIsoСтрока()
    {
        var info = new LocalGpuQueueInfo(
            Ahead: 0,
            StartInSeconds: null,
            Steps: new LocalGpuSteps(12, 25, 1),
            Percent: 48,
            PercentExact: true,
            RemainingSeconds: 30,
            Busy: null,
            DownSince: DownSinceMs);

        var json = JsonSerializer.Serialize(info, Wire);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // downSince — число, и именно наш unix-мс (не ISO-строка "2026-10-14T03:00:00+00:00")
        root.TryGetProperty("downSince", out var downSince).Should().BeTrue("camelCase-имя downSince");
        downSince.ValueKind.Should().Be(JsonValueKind.Number, "downSince — число unix-мс, не ISO-строка");
        downSince.GetInt64().Should().Be(DownSinceMs);

        // percentExact — присутствует (bool), camelCase
        root.TryGetProperty("percentExact", out var percentExact).Should().BeTrue();
        percentExact.ValueKind.Should().Be(JsonValueKind.True);

        // steps/percent/remainingSeconds — camelCase, значения на месте
        root.TryGetProperty("percent", out var percent).Should().BeTrue();
        percent.GetInt32().Should().Be(48);
        root.TryGetProperty("steps", out var steps).Should().BeTrue();
        steps.TryGetProperty("step", out var step).Should().BeTrue();
        step.GetInt32().Should().Be(12);
    }

    [Fact]
    public void Gpu_БезЛишнихПолей_OwnerId_SessionId_Prompt()
    {
        var info = new LocalGpuQueueInfo(
            Ahead: 2,
            StartInSeconds: 90,
            Steps: null,
            Percent: null,
            PercentExact: false,
            RemainingSeconds: null,
            Busy: new LocalGpuBusy("video", Heavy: true, RemainingSeconds: 45),
            DownSince: null);

        var json = JsonSerializer.Serialize(info, Wire);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Снимок очереди — только агрегат: ownerId/sessionId/prompt/slug/promptId/chatSessionId
        // живут в ComfyJobRow/ComfyOwnerSnapshot и в LocalGpuQueueInfo не должны попадать
        foreach (var extra in new[] { "ownerId", "sessionId", "prompt", "slug", "promptId", "chatSessionId" })
            root.TryGetProperty(extra, out _).Should().BeFalse($"лишнее поле {extra} не должно быть в снимке очереди");

        // busy — агрегат (kind/heavy/remainingSeconds), camelCase
        root.TryGetProperty("busy", out var busy).Should().BeTrue();
        busy.TryGetProperty("kind", out var kind).Should().BeTrue();
        kind.GetString().Should().Be("video");
        busy.TryGetProperty("heavy", out var heavy).Should().BeTrue();
        heavy.ValueKind.Should().Be(JsonValueKind.True);
    }

    [Fact]
    public void NullПоля_Пишутся_КакNull_ПоПравилуМодуля()
    {
        // Только downSince задан, остальное null: правило модуля (DefaultIgnoreCondition=Never)
        // — null-поля пишутся как null, а не выкидываются
        var info = new LocalGpuQueueInfo(
            Ahead: null,
            StartInSeconds: null,
            Steps: null,
            Percent: null,
            PercentExact: false,
            RemainingSeconds: null,
            Busy: null,
            DownSince: DownSinceMs);

        var json = JsonSerializer.Serialize(info, Wire);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("downSince", out var downSince).Should().BeTrue();
        downSince.ValueKind.Should().Be(JsonValueKind.Number);
        foreach (var field in new[] { "ahead", "startInSeconds", "steps", "percent", "remainingSeconds", "busy" })
        {
            root.TryGetProperty(field, out var v).Should().BeTrue($"null-поле {field} пишется как null");
            v.ValueKind.Should().Be(JsonValueKind.Null);
        }
        root.TryGetProperty("percentExact", out var percentExact).Should().BeTrue();
        percentExact.ValueKind.Should().Be(JsonValueKind.False);
    }

    // M2: running — camelCase bool (идущая задача), как соседи; не-nullable, дефолт false
    [Fact]
    public void Running_Сериализуется_КакCamelCaseBool()
    {
        var info = new LocalGpuQueueInfo(
            Ahead: null,
            StartInSeconds: null,
            Steps: null,
            Percent: null,
            PercentExact: false,
            RemainingSeconds: null,
            Busy: null,
            DownSince: null,
            Running: true);

        var json = JsonSerializer.Serialize(info, Wire);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("running", out var running).Should().BeTrue("camelCase-имя running");
        running.ValueKind.Should().Be(JsonValueKind.True, "running=true пишется как true");
    }
}
