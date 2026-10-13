using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.Protocol;
using ModelContextProtocol.Protocol;
using Sbroenne.WindowsMcp.Automation;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Точка входа инструментов в гейт <see cref="HandsPolicy"/>. Пока <see cref="Configure"/> не
/// вызван, запускать некуда и вводить некуда — закрыто по умолчанию.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HandsGate
{
    private static WindowsHandsSystem? s_system;
    private static HandsPolicy s_policy = new(new NoWindows());
    private static HandsActivity s_activity = new(NoHandsActivitySignal.Instance);
    private static HandsMachineHold s_hold = new(new InProcessHandsMachineMutex());
    private static IHandsActivitySignal s_busy = NoHandsActivitySignal.Instance;

    public static HandsPolicy Policy => s_policy;

    /// <summary>Первое действие рук за ход — сигнал агенту для плашки.</summary>
    public static HandsActivity Activity => s_activity;

    /// <summary>Job программ рук; до <see cref="Configure"/> запуск невозможен.</summary>
    public static WindowsHandsSystem? System => s_system;

    /// <param name="browserProfile">
    /// Профиль Chrome браузерной руки (<c>--browser-profile</c>); его родитель — корень профилей
    /// агента, в который <c>app</c> не пускает <c>user-data-dir</c>. Путь не полный — запрета нет,
    /// а браузер откажет сам.
    /// </param>
    /// <param name="activityEvent">
    /// Имя события хода (<c>--activity-event</c>), которое мост поднимает при первом действии рук.
    /// Нет имени — сигнала нет, агент плашку не зажжёт.
    /// </param>
    /// <param name="busyEvent">
    /// Событие хода «руки заняты» (<c>--busy-event</c>): мост поднимает его на каждый отказ по замку
    /// машины, агент доносит серверу «занято» для бейджа. Нет имени — бейдж о занятости не узнает.
    /// </param>
    /// <param name="turnEndEvent">
    /// Событие «ход окончен» (<c>--turn-end-event</c>), которое поднимает агент: мост отпускает замок
    /// машины. Нет имени — замок держится до смерти моста.
    /// </param>
    public static void Configure(string? browserProfile = null, string? activityEvent = null,
        string? busyEvent = null, string? turnEndEvent = null)
    {
        s_system = new WindowsHandsSystem();
        var profilesRoot = BrowserProfilesRoot(browserProfile);
        s_policy = new HandsPolicy(s_system, profilesRoot);
        s_activity = new HandsActivity(string.IsNullOrWhiteSpace(activityEvent)
            ? NoHandsActivitySignal.Instance
            : new NamedEventActivitySignal(activityEvent));
        s_busy = string.IsNullOrWhiteSpace(busyEvent)
            ? NoHandsActivitySignal.Instance
            : new NamedEventActivitySignal(busyEvent, "руки заняты другим ходом");
        var hold = s_hold = new HandsMachineHold(new NamedHandsMachineMutex(HandsMachineLock.Name) { Log = HandsLog.Write });
        if (!string.IsNullOrWhiteSpace(turnEndEvent))
            NamedTurnEndWatch.Start(turnEndEvent, () =>
            {
                if (hold.Release()) HandsLog.Write("конец хода: замок рук отпущен");
            });
    }

    /// <summary>
    /// Инструмент прошёл гейт и сейчас подействует. Действие ввода или управления окнами и
    /// программами берёт замок машины (<see cref="HandsMachineHold"/>): занят другим ходом — отказ
    /// этого вызова, ход идёт дальше. Первое действие, которое трогает машину, поднимает сигнал хода.
    /// Зовётся сразу после проверки гейта — отказ гейта сюда не доходит.
    /// </summary>
    /// <param name="action">Действие в snake_case (<c>window_management</c>, <c>screenshot_control</c>).</param>
    /// <returns>null — действовать можно; иначе ответ инструмента «руки заняты».</returns>
    public static CallToolResult? Acted(string tool, string? action = null)
    {
        if (!s_hold.TryHold(tool, action))
        {
            s_busy.Raise();
            return Deny(tool, HandsMachineHold.BusyMessage, HandsEndReason.Busy);
        }
        s_activity.Acted(tool, allowed: true, action);
        return null;
    }

    private static string? BrowserProfilesRoot(string? browserProfile)
    {
        if (string.IsNullOrWhiteSpace(browserProfile))
            return null;
        var root = HandsAppPaths.TryNormalize(browserProfile) is { } profile ? Path.GetDirectoryName(profile) : null;
        if (root is null)
            HandsLog.Write($"гейт: путь профиля браузера «{browserProfile}» не полный — запрет user-data-dir для app не действует");
        return root;
    }

    /// <summary>hwnd корня, записанный в идентификатор элемента; null — id неизвестен.</summary>
    public static long? ElementWindow(string elementId) =>
        ElementIdGenerator.TryResolveWindowHandle(elementId, out var hwnd) ? hwnd : null;

    /// <summary>Отказ гейта в форме ответа инструмента.</summary>
    public static CallToolResult Deny(string tool, HandsDecision decision) =>
        Deny(tool, decision.Reason ?? "Not allowed.");

    /// <param name="error">Код ошибки в ответе: отказ гейта или <see cref="HandsEndReason.Busy"/>.</param>
    public static CallToolResult Deny(string tool, string reason, string error = "hands_policy")
    {
        HandsLog.Write($"гейт: отказ {tool}: {reason}");
        return new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { success = false, error, tool, message = reason }),
                },
            ],
            IsError = true,
        };
    }

    private sealed class NoWindows : IHandsWindowSystem
    {
        public int? GetWindowProcessId(long hwnd) => null;
        public string? GetProcessImagePath(int processId) => null;
    }
}
