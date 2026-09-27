using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Папка локального проекта на устройстве при создании и перепривязке (ADR-016 §5).
///
/// Агент с выдачей папки (<see cref="DeviceCapabilities.BindFolder"/>, решение владельца
/// 2026-09-27) сам создаёт папку и разрешает её — сервер лишь просит и передаёт имя проекта
/// для подписи корня; запретный список и выключатель автовыдачи живут только на агенте.
/// Старый агент — прежняя проверка операцией ретранслятора <see cref="RelayOperations.CheckPath"/>
/// (только чтение) с отказом и подсказкой команды. Своей политики корней у сервера нет.
/// </summary>
internal static class DeviceFolderCheck
{
    /// <summary>Сколько ждать ответа агента: вопрос один и мелкий, диалог не должен висеть минуту.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    public const string OfflineRefusal = "Устройство не в сети — проверить папку нельзя. Включите компьютер с агентом";

    private const string NoAnswerRefusal =
        "Устройство не ответило — проверить папку нельзя. Проверьте, что компьютер с агентом включён и в сети";

    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Итог: <see cref="Refusal"/> — текст отказа (400); <see cref="Warning"/> — проверка пропущена;
    /// <see cref="Notice"/> — агент создал или разрешил папку, строка для человека.
    /// </summary>
    public sealed record Verdict(string? Refusal, string? Warning, string? Notice = null);

    /// <summary>
    /// Выдача папки агентом, а если агент её не умеет — прежняя проверка (<see cref="CheckAsync"/>).
    /// </summary>
    public static async Task<Verdict> BindAsync(IDeviceFolderBindChannel? binder, IDeviceRelayChannel? relay, string ownerId,
        DeviceExecStatus device, string devicePath, string? projectName, CancellationToken ct)
    {
        if (!device.Online) return new(OfflineRefusal, null);
        if (binder is null || !device.HasCapability(DeviceCapabilities.BindFolder) || device.AgentOutdated)
            return await CheckAsync(relay, ownerId, device, devicePath, ct);

        IDeviceExecStream stream;
        try { stream = await binder.OpenBindFolderAsync(ownerId, device.DeviceId, ct); }
        catch (DeviceExecRefusedException e) when (e.Reason is DeviceExecRefusal.NoBindFolderCapability or DeviceExecRefusal.AgentOutdated)
        {
            return await CheckAsync(relay, ownerId, device, devicePath, ct);
        }
        catch (DeviceExecRefusedException e)
        {
            return new(e.Reason is DeviceExecRefusal.Offline or DeviceExecRefusal.NoResponse ? OfflineRefusal : e.Message, null);
        }

        await using (stream)
        {
            var reply = await ExchangeAsync(stream, new BindFolderRequest(devicePath, projectName), ct);
            if (reply is null) return new(NoAnswerRefusal, null);

            BindFolderResult? result = null;
            if (reply.Value.Head.Status == 200)
                try { result = JsonSerializer.Deserialize<BindFolderResult>(reply.Value.Body, RelayProtocol.Json); }
                catch (JsonException) { }
            if (result is null)
                return new($"Устройство «{device.DeviceName}» не смогло подготовить папку. Повторите попытку", null);

            var name = device.DeviceName;
            return result.Outcome switch
            {
                BindFolderOutcomes.Bound => new(null, null,
                    result.Created ? $"Папка «{devicePath}» создана и разрешена агенту на «{name}»"
                    : result.Added ? $"Папка «{devicePath}» разрешена агенту на «{name}»"
                    : null),
                // Автовыдача выключена на машине — прежний отказ с подсказкой команды
                BindFolderOutcomes.AutoOff => new(RefusalOf(new RelayPathCheck(result.Exists, result.IsDirectory, result.InsideRoots),
                    devicePath, name), null),
                BindFolderOutcomes.NotDirectory => new(NotDirectoryRefusal(devicePath, name), null),
                BindFolderOutcomes.Forbidden => new(
                    $"Агент на «{name}» не выдаёт эту папку: {result.Message}. Укажите другую папку", null),
                _ => new($"Агент на «{name}» не смог подготовить папку: {result.Message}", null),
            };
        }
    }

    public static async Task<Verdict> CheckAsync(IDeviceRelayChannel? relay, string ownerId,
        DeviceExecStatus device, string devicePath, CancellationToken ct)
    {
        // Офлайн проверить нельзя, а молча пропустить — ровно тот дефект, который чиним
        if (!device.Online) return new(OfflineRefusal, null);
        // Старый агент операции не знает: совместимость важнее, проект создаётся без проверки
        if (relay is null || !device.HasCapability(DeviceCapabilities.Relay) || device.AgentOutdated)
            return Skipped(device);

        IDeviceExecStream stream;
        try { stream = await relay.OpenRelayAsync(ownerId, device.DeviceId, ct); }
        catch (DeviceExecRefusedException e) when (e.Reason is DeviceExecRefusal.NoRelayCapability or DeviceExecRefusal.AgentOutdated)
        {
            return Skipped(device);
        }
        catch (DeviceExecRefusedException e)
        {
            return new(e.Reason is DeviceExecRefusal.Offline or DeviceExecRefusal.NoResponse ? OfflineRefusal : e.Message, null);
        }

        await using (stream)
        {
            var reply = await ExchangeAsync(stream, new RelayRequest(RelayOperations.CheckPath, ProjectId: "", RootPath: devicePath), ct);
            if (reply is null) return new(NoAnswerRefusal, null);
            // 400 у ретранслятора — «операция не поддерживается»: агент старше проверки
            if (reply.Value.Head.Status == 400) return Skipped(device);

            RelayPathCheck? check = null;
            if (reply.Value.Head.Status == 200)
                try { check = JsonSerializer.Deserialize<RelayPathCheck>(reply.Value.Body, RelayProtocol.Json); }
                catch (JsonException) { }
            if (check is null)
                return new($"Устройство «{device.DeviceName}» не смогло проверить папку. Повторите попытку", null);

            return new(RefusalOf(check, devicePath, device.DeviceName), null);
        }
    }

    // Запрос — кадр Control, ответ — заголовок Info, тело Stdout, конец Exit; null — ответа нет
    private static async Task<(RelayResponseHead Head, byte[] Body)?> ExchangeAsync<T>(IDeviceExecStream stream, T request, CancellationToken ct)
    {
        try { await stream.SendAsync(DeviceExecFrameChannel.Control, JsonSerializer.SerializeToUtf8Bytes(request, RelayProtocol.Json), ct); }
        catch (ObjectDisposedException) { return null; }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);
        RelayResponseHead? head = null;
        var body = new MemoryStream();
        try
        {
            await foreach (var frame in stream.ReadAllAsync(timeout.Token))
            {
                if (frame.Channel == DeviceExecFrameChannel.Info)
                    head = JsonSerializer.Deserialize<RelayResponseHead>(frame.Payload.Span, RelayProtocol.Json);
                else if (frame.Channel == DeviceExecFrameChannel.Stdout && body.Length + frame.Payload.Length <= MaxBodyBytes)
                    body.Write(frame.Payload.Span);
                else if (frame.Channel == DeviceExecFrameChannel.Exit)
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }

        return head is null ? null : (head, body.ToArray());
    }

    private static string? RefusalOf(RelayPathCheck check, string path, string deviceName) =>
        !check.Exists ? $"Папки «{path}» нет на устройстве «{deviceName}». Создайте её или укажите другую"
        : !check.IsDirectory ? NotDirectoryRefusal(path, deviceName)
        : !check.InsideRoots ? $"Папка вне разрешённых на устройстве «{deviceName}». Добавьте корень: ai-home-agent roots add \"{path}\""
        : null;

    private static string NotDirectoryRefusal(string path, string deviceName) =>
        $"«{path}» на устройстве «{deviceName}» — файл, а не папка. Укажите папку";

    private static Verdict Skipped(DeviceExecStatus device) => new(null,
        $"Агент устройства «{device.DeviceName}» не умеет проверять папку (старая версия): проект сохранён без проверки. Обновите агента");
}
