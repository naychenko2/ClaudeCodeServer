using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Эндпоинты сеанса рук (<c>/api/devices/hands*</c>) под токеном устройства.
///
/// HttpClient приходит снаружи уже с заголовками <c>Authorization: Device {токен}</c> и
/// <c>X-Device-Fingerprint</c>: токен принадлежит сопряжению, а не сеансу, и здесь ему делать
/// нечего. Отказ старта — это ответ 409 { outcome, message }, а не исключение: человеку его
/// показывают текстом.
/// </summary>
public sealed class HandsApiClient(HttpClient http) : IHandsApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HandsRequest>> RequestsAsync(CancellationToken ct = default)
    {
        var res = await http.GetAsync("api/devices/hands/requests", ct);
        res.EnsureSuccessStatusCode();
        var items = await res.Content.ReadFromJsonAsync<List<RequestDto>>(Json, ct) ?? [];
        return [.. items.Select(i => new HandsRequest(i.ChatSessionId, i.Chat, i.Project, i.Persona, i.RequestedAt))];
    }

    public async Task<HandsSessionInfo?> CurrentAsync(CancellationToken ct = default)
    {
        var res = await http.GetAsync("api/devices/hands", ct);
        res.EnsureSuccessStatusCode();
        return await ReadSessionAsync(res, ct);
    }

    public async Task<HandsStartOutcome> StartAsync(string chatSessionId, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/devices/hands/start", new { chatSessionId }, Json, ct);

        // Отказ — штатный ответ сервера: чат исчез, грань выключили, руки заняты другим чатом.
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var refusal = await res.Content.ReadFromJsonAsync<RefusalDto>(Json, ct);
            return new HandsStartOutcome(false, refusal?.Outcome,
                refusal?.Message ?? "Сервер отказал в старте сеанса и причины не назвал.");
        }

        res.EnsureSuccessStatusCode();
        var session = await ReadSessionAsync(res, ct);
        return session is null
            ? new HandsStartOutcome(false, null, "Сервер ответил на старт сеанса пустотой.")
            : new HandsStartOutcome(true, null,
                $"Сеанс чата «{session.ChatTitle}» начат на этом компьютере.", session);
    }

    public async Task<bool> StopAsync(string reason, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/devices/hands/stop", new { reason }, Json, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<StoppedDto>(Json, ct);
        return body?.Stopped ?? false;
    }

    // Сеанса нет — сервер отвечает 204 либо телом null: и то и другое означает «руки свободны».
    private static async Task<HandsSessionInfo?> ReadSessionAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        var dto = await res.Content.ReadFromJsonAsync<SessionDto>(Json, ct);
        if (dto is null || string.IsNullOrEmpty(dto.ChatSessionId)) return null;

        return new HandsSessionInfo(dto.ChatSessionId, dto.Chat, dto.Device,
            dto.StartedAt, dto.ExpiresAt, dto.IdleDeadlineAt, dto.HardDeadlineAt);
    }

    private sealed record RequestDto(
        [property: JsonPropertyName("chatSessionId")] string ChatSessionId,
        [property: JsonPropertyName("chat")] string? Chat,
        [property: JsonPropertyName("project")] string? Project,
        [property: JsonPropertyName("persona")] string? Persona,
        [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);

    private sealed record SessionDto(
        [property: JsonPropertyName("chatSessionId")] string ChatSessionId,
        [property: JsonPropertyName("chat")] string? Chat,
        [property: JsonPropertyName("device")] string? Device,
        [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("idleDeadlineAt")] DateTimeOffset IdleDeadlineAt,
        [property: JsonPropertyName("hardDeadlineAt")] DateTimeOffset HardDeadlineAt);

    private sealed record RefusalDto(
        [property: JsonPropertyName("outcome")] string? Outcome,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record StoppedDto([property: JsonPropertyName("stopped")] bool Stopped);
}
