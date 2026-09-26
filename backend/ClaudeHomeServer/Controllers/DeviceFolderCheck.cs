using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Проверка папки локального проекта на устройстве при создании и перепривязке (ADR-016):
/// без неё путь к несуществующей папке всплывал только отказом первого хода. Спрашивает агента
/// операцией ретранслятора <see cref="RelayOperations.CheckPath"/> (только чтение), корни
/// судит сам агент — своей политикой, той же, что у хода. Своей политики корней у сервера нет.
/// </summary>
internal static class DeviceFolderCheck
{
    /// <summary>Сколько ждать ответа агента: вопрос один и мелкий, диалог не должен висеть минуту.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);

    public const string OfflineRefusal = "Устройство не в сети — проверить папку нельзя. Включите компьютер с агентом";

    private const string NoAnswerRefusal =
        "Устройство не ответило — проверить папку нельзя. Проверьте, что компьютер с агентом включён и в сети";

    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>Итог: <see cref="Refusal"/> — текст отказа (400); <see cref="Warning"/> — проверка пропущена.</summary>
    public sealed record Verdict(string? Refusal, string? Warning);

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
            var request = new RelayRequest(RelayOperations.CheckPath, ProjectId: "", RootPath: devicePath);
            try { await stream.SendAsync(DeviceExecFrameChannel.Control, JsonSerializer.SerializeToUtf8Bytes(request, RelayProtocol.Json), ct); }
            catch (ObjectDisposedException) { return new(NoAnswerRefusal, null); }

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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(NoAnswerRefusal, null); }
            catch (JsonException) { return new(NoAnswerRefusal, null); }

            if (head is null) return new(NoAnswerRefusal, null);
            // 400 у ретранслятора — «операция не поддерживается»: агент старше проверки
            if (head.Status == 400) return Skipped(device);

            RelayPathCheck? check = null;
            if (head.Status == 200)
                try { check = JsonSerializer.Deserialize<RelayPathCheck>(body.ToArray(), RelayProtocol.Json); }
                catch (JsonException) { }
            if (check is null)
                return new($"Устройство «{device.DeviceName}» не смогло проверить папку. Повторите попытку", null);

            return new(RefusalOf(check, devicePath, device.DeviceName), null);
        }
    }

    private static string? RefusalOf(RelayPathCheck check, string path, string deviceName) =>
        !check.Exists ? $"Папки «{path}» нет на устройстве «{deviceName}». Создайте её или укажите другую"
        : !check.IsDirectory ? $"«{path}» на устройстве «{deviceName}» — файл, а не папка. Укажите папку"
        : !check.InsideRoots ? $"Папка вне разрешённых на устройстве «{deviceName}». Добавьте корень: ai-home-agent roots add \"{path}\""
        : null;

    private static Verdict Skipped(DeviceExecStatus device) => new(null,
        $"Агент устройства «{device.DeviceName}» не умеет проверять папку (старая версия): проект сохранён без проверки. Обновите агента");
}
