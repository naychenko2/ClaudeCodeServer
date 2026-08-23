using System.Drawing;
using System.Text.Json;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;
using AiHomeDesktop.Windows.Imaging;
using AiHomeDesktop.Windows.Interop;

namespace AiHomeDesktop.Windows.Execution;

/// <summary>
/// desktop_screen: кадр окна (по умолчанию), экрана целиком или области.
///
/// Кадр — недоверенные данные, и заворачивает их в контейнер MCP-сервер; здесь задача
/// другая: снять именно то, что просили, уложиться в бюджет протокола и на любой осечке
/// вернуть честный исход, а не пустую картинку.
/// </summary>
public sealed class ScreenCall(FrameBudget budget)
{
    private readonly FrameBudget _budget = budget;

    public DeviceCallResultBody Execute(JsonElement? args, CancellationToken ct)
    {
        if (!CallArgs.TryScreen(args, out var request, out var error))
            return DeviceCallResultBody.Refused(DesktopOutcomes.ProtocolError, error!);

        // Снапшотов UIA эта версия клиента не делает вовсе, поэтому любой snapshotId
        // относится к тому, чего на устройстве нет. Честный исход протокола — snapshot_stale.
        if (request.SnapshotId is not null)
            return DeviceCallResultBody.Refused(DesktopOutcomes.SnapshotStale,
                "Кадр привязан к снапшоту, которого на устройстве нет: снапшоты окон эта версия "
                + "клиента AI Home Desktop не делает. Сними кадр без snapshotId.");

        switch (DesktopState.Current())
        {
            case DesktopInputState.Locked:
                return DeviceCallResultBody.Refused(DesktopOutcomes.SessionLocked,
                    "Экран устройства заблокирован — снять с него нечего. "
                    + "Попроси человека разблокировать компьютер.");
            case DesktopInputState.Secure:
                return DeviceCallResultBody.Refused(DesktopOutcomes.SecureDesktop,
                    "На устройстве открыт защищённый рабочий стол (запрос UAC или экран входа) — "
                    + "его содержимое не снимается по устройству Windows, а не по нашему запрету.");
        }

        ct.ThrowIfCancellationRequested();

        return request.Scope switch
        {
            "screen" => CaptureScreen(request, ct),
            "region" => CaptureRegion(request, ct),
            _ => CaptureWindow(request, ct)
        };
    }

    private DeviceCallResultBody CaptureWindow(ScreenRequest request, CancellationToken ct)
    {
        var pick = WindowMatch.Select(WindowInventory.List(), request.Window, WindowInventory.Foreground());
        if (pick.Window is null)
            return DeviceCallResultBody.Refused(pick.Outcome!, pick.Message!);

        ct.ThrowIfCancellationRequested();
        var window = pick.Window;

        // HWND в чистой логике окна — просто число (так её гоняет тест); WinAPI ждёт nint.
        using var bitmap = ScreenGrabber.CaptureWindow((nint)window.Handle);
        var frame = FrameEncoder.Encode(bitmap, _budget);

        return Ok(frame, new
        {
            scope = "window",
            window = new { title = window.Title, process = window.ProcessName }
        });
    }

    private DeviceCallResultBody CaptureScreen(ScreenRequest request, CancellationToken ct)
    {
        var screens = ScreenGrabber.Screens();
        var number = request.Screen ?? screens.FirstOrDefault(s => s.Primary)?.Number ?? 1;
        var screen = screens.FirstOrDefault(s => s.Number == number);
        if (screen is null)
            return DeviceCallResultBody.Refused(DesktopOutcomes.WindowNotAvailable,
                $"Экрана {number} на устройстве нет: экранов всего {screens.Count}.");

        ct.ThrowIfCancellationRequested();
        using var bitmap = ScreenGrabber.CaptureScreen(screen);
        var frame = FrameEncoder.Encode(bitmap, _budget);

        return Ok(frame, new
        {
            scope = "screen",
            screen = new { number = screen.Number, width = screen.Bounds.Width, height = screen.Bounds.Height }
        });
    }

    private DeviceCallResultBody CaptureRegion(ScreenRequest request, CancellationToken ct)
    {
        var region = request.Region!;
        var wanted = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var visible = Rectangle.Intersect(wanted, ScreenGrabber.VirtualBounds());
        if (visible.Width <= 0 || visible.Height <= 0)
            return DeviceCallResultBody.Refused(DesktopOutcomes.WindowNotAvailable,
                $"Область {wanted.Width}×{wanted.Height} в точке ({wanted.X}, {wanted.Y}) целиком "
                + "лежит за пределами экранов устройства.");

        ct.ThrowIfCancellationRequested();
        using var bitmap = ScreenGrabber.CaptureRegion(visible);
        var frame = FrameEncoder.Encode(bitmap, _budget);

        // Область могли обрезать края экранов — говорим об этом прямо, иначе модель решит,
        // что видит всё, что просила.
        var clipped = visible != wanted;
        return Ok(frame, new
        {
            scope = "region",
            region = new { x = visible.X, y = visible.Y, width = visible.Width, height = visible.Height },
            clipped
        }, clipped);
    }

    /// <summary>
    /// Успешный кадр. Форма payload — { image: { data, mimeType, width, height, scaled }, ... }:
    /// MCP-сервер вытаскивает image в отдельный image-блок, всё остальное печатает текстом.
    /// </summary>
    private DeviceCallResultBody Ok(EncodedFrame frame, object details, bool partial = false)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            image = new
            {
                data = Convert.ToBase64String(frame.Data),
                mimeType = frame.MimeType,
                width = frame.Width,
                height = frame.Height,
                scaled = frame.Scaled,
                sourceWidth = frame.SourceWidth,
                sourceHeight = frame.SourceHeight
            },
            details,
            capturedAt = DateTimeOffset.Now.ToString("O")
        });

        var scale = frame.Scaled
            ? $" Кадр уменьшен с {frame.SourceWidth}×{frame.SourceHeight} — бюджет кадра протокола."
            : "";

        // Шаг у кадра ровно один, и он применён: индекс возвращается в любом исходе.
        return new DeviceCallResultBody(DesktopOutcomes.Ok, 1,
            $"Кадр снят: {frame.Width}×{frame.Height}.{scale}", partial, payload);
    }
}
