using System.Diagnostics;
using System.Text.Json;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Windows.Execution;

/// <summary>
/// desktop_open: приложение, файл или ссылка строго из allow-list устройства.
///
/// Сам список и разбор цели — в ядре (<see cref="OpenAllowList"/>), здесь только запуск.
/// Оболочки из списка вычеркнуты, но это ГИГИЕНА И СЛЕДЫ, а не граница (ADR-008): мимо
/// списка едут .lnk и протокольные обработчики. Гарантий на этом строить нельзя.
/// </summary>
public sealed class OpenCall(Func<OpenAllowList> allowList)
{
    public DeviceCallResultBody Execute(JsonElement? args, CancellationToken ct)
    {
        if (!CallArgs.TryOpen(args, out var request, out var error))
            return DeviceCallResultBody.Refused(DesktopOutcomes.ProtocolError, error!);

        var decision = allowList().Evaluate(request.Target);
        if (!decision.Allowed)
            return DeviceCallResultBody.Refused(DesktopOutcomes.Denied, decision.Reason!);

        ct.ThrowIfCancellationRequested();

        try
        {
            Start(decision, request.Arguments);
        }
        catch (Exception ex)
        {
            // Запуск не состоялся: ни один шаг не применён, и об этом говорим прямо.
            return DeviceCallResultBody.Refused(DesktopOutcomes.ProtocolError,
                $"Windows не открыла цель «{decision.Target}»: {ex.Message}");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            opened = decision.Target,
            kind = decision.Kind.ToString().ToLowerInvariant(),
            arguments = request.Arguments
        });

        // Открытие — один шаг, и он применён. Что именно нарисовалось на экране, отсюда не
        // видно: подтверждать результат надо кадром, а не верой в код возврата ShellExecute.
        return new DeviceCallResultBody(DesktopOutcomes.Ok, 1,
            $"Цель открыта: {decision.Target}. Что появилось на экране — видно только кадром (desktop_screen).",
            Payload: payload);
    }

    private static void Start(OpenDecision decision, string? arguments)
    {
        var info = new ProcessStartInfo(decision.Target!)
        {
            // Через оболочку Windows: так открываются и ссылки, и документы своими
            // приложениями по умолчанию.
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(arguments) && decision.Kind != OpenTargetKind.Url)
            info.Arguments = arguments;

        // ShellExecute любит STA: из пула потоков часть обработчиков расширений оболочки
        // просто не заводится.
        RunSta(() =>
        {
            using var process = Process.Start(info);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
    }
}
