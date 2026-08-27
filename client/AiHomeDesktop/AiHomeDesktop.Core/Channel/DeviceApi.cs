using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Core.Channel;

/// <summary>Чем кончилась доставка результата вызова.</summary>
public enum ResultDelivery
{
    /// <summary>Сервер принял результат.</summary>
    Accepted,

    /// <summary>Дубль — результат по этому callId уже принят. Досылать больше нечего.</summary>
    Duplicate,

    /// <summary>Сервер о вызове не помнит (истёк, отменён). Досылать нечего.</summary>
    UnknownCall,

    /// <summary>Отказ авторизации: токен устройства мёртв или отпечаток не тот.</summary>
    Forbidden,

    /// <summary>Не доехало: сеть. Запись остаётся в журнале до следующего подключения.</summary>
    Failed
}

/// <summary>Итог сопряжения. Ошибку показываем человеку как есть — её сочинил сервер.</summary>
public sealed record PairOutcome(bool Ok, DeviceCredentials? Credentials, string? Error);

/// <summary>
/// HTTP-половина канала устройства: сопряжение, результаты вызовов и сеанс рук.
///
/// Каждый запрос (кроме сопряжения) несёт ДВА заголовка: <c>Authorization: Device {токен}</c>
/// и <c>X-Device-Fingerprint</c>. Схема намеренно НЕ Bearer — это другой класс токена, и
/// DesktopDeviceAuthHandler на Bearer отвечает NoResult, то есть запрос уходит в общий
/// периметр и получает отказ. Отпечаток сверяется на каждом запросе: утёкший токен не
/// должен работать с чужой машины.
/// </summary>
public sealed class DeviceApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Учётные данные устройства. Меняются при сопряжении, поэтому не в DefaultRequestHeaders.</summary>
    public DeviceCredentials? Credentials { get; set; }

    /// <summary>
    /// Обменять код сопряжения на токен устройства. Ответ содержит ТОЛЬКО учётные данные
    /// самого устройства: API-ключ владельца и его JWT на клиент не уезжают никогда.
    /// </summary>
    public async Task<PairOutcome> PairAsync(
        Uri server, string code, string deviceName, string? clientVersion, CancellationToken ct = default)
    {
        if (!ServerAddress.IsSecureEnough(server))
            return new PairOutcome(false, null,
                "По открытому каналу сопряжение недоступно: нужен https (либо localhost)");

        var fingerprint = MachineFingerprint.OfThisMachine();
        var body = new
        {
            code = (code ?? "").Trim(),
            name = deviceName,
            fingerprint,
            clientVersion
        };

        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(server, "/api/devices/pair"), body, Json, ct);
            if (!response.IsSuccessStatusCode)
                return new PairOutcome(false, null, await ErrorTextAsync(response, ct));

            var payload = await response.Content.ReadFromJsonAsync<PairResponse>(Json, ct);
            if (payload is null || string.IsNullOrEmpty(payload.DeviceToken))
                return new PairOutcome(false, null, "Сервер не вернул токен устройства");

            return new PairOutcome(true, new DeviceCredentials(
                server.GetLeftPart(UriPartial.Authority),
                payload.DeviceId,
                payload.Name,
                payload.DeviceToken,
                payload.TokenVersion,
                fingerprint), null);
        }
        catch (Exception ex)
        {
            return new PairOutcome(false, null, $"Не удалось связаться с сервером: {ex.Message}");
        }
    }

    /// <summary>
    /// Отдать результат вызова. Мимо 32-КБ лимита сообщения хаба — потому что в кадре
    /// байты; потолок тела 8 МБ.
    /// </summary>
    public async Task<ResultDelivery> PostResultAsync(
        string callId, DeviceCallResultBody body, CancellationToken ct = default)
    {
        try
        {
            using var request = Request(HttpMethod.Post, $"/api/devices/calls/{callId}/result");
            request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, ct);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => ResultDelivery.Accepted,
                // 409 — ТОЛЬКО дубль: опоздание причиной отказа не является.
                HttpStatusCode.Conflict => ResultDelivery.Duplicate,
                HttpStatusCode.NotFound => ResultDelivery.UnknownCall,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ResultDelivery.Forbidden,
                _ => ResultDelivery.Failed
            };
        }
        catch (Exception)
        {
            return ResultDelivery.Failed;
        }
    }

    /// <summary>Забрать результат по callId — путь реконнекта: доехал ли он вообще.</summary>
    public async Task<DesktopCallResultView?> GetResultAsync(string callId, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, $"/api/devices/calls/{callId}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.OK) return null;
        return await response.Content.ReadFromJsonAsync<DesktopCallResultView>(Json, ct);
    }

    /// <summary>Очередь заявок владельца: имя чата, проекта и персоны — по ним человек и выбирает.</summary>
    public async Task<IReadOnlyList<DesktopHandsRequestView>> HandsRequestsAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "/api/devices/hands/requests");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return [];
        return await response.Content.ReadFromJsonAsync<List<DesktopHandsRequestView>>(Json, ct) ?? [];
    }

    /// <summary>Текущий сеанс рук этого устройства (null — руки никому не отданы).</summary>
    public async Task<DesktopHandsSessionView?> HandsCurrentAsync(CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "/api/devices/hands");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DesktopHandsSessionView>(Json, ct);
    }

    /// <summary>
    /// Начать сеанс рук. Единственная дверь: у веб-морды и у агента кнопки «начать» нет —
    /// сеанс стартует только отсюда, с самой машины.
    /// </summary>
    public async Task<(bool Started, DesktopHandsSessionView? Session, string? Message)> HandsStartAsync(
        string chatSessionId, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/api/devices/hands/start");
        request.Content = JsonContent.Create(new { chatSessionId }, options: Json);
        using var response = await http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
            return (true, await response.Content.ReadFromJsonAsync<DesktopHandsSessionView>(Json, ct), null);

        return (false, null, await ErrorTextAsync(response, ct));
    }

    /// <summary>
    /// Погасить сеанс. Повод называет клиент: «Стоп» человека или закрытие окна оболочки —
    /// жизнь в трее закрытием НЕ считается.
    /// </summary>
    public async Task HandsStopAsync(string reason, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/api/devices/hands/stop");
        request.Content = JsonContent.Create(new { reason }, options: Json);
        using var response = await http.SendAsync(request, ct);
        _ = response.IsSuccessStatusCode;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var credentials = Credentials
            ?? throw new InvalidOperationException("Устройство не сопряжено: токена нет");

        var request = new HttpRequestMessage(method, new Uri(new Uri(credentials.ServerUrl), path));
        // Схема Device, не Bearer: на Bearer обработчик устройства отвечает NoResult.
        request.Headers.TryAddWithoutValidation("Authorization", $"Device {credentials.DeviceToken}");
        request.Headers.TryAddWithoutValidation("X-Device-Fingerprint", credentials.Fingerprint);
        return request;
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            foreach (var name in (string[])["error", "message"])
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return value.GetString()!;
            return text;
        }
        catch (Exception)
        {
            return $"Сервер ответил {(int)response.StatusCode}";
        }
    }

    private sealed record PairResponse(string DeviceId, string Name, string DeviceToken, int TokenVersion);
}
