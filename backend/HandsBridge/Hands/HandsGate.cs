using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
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

    public static HandsPolicy Policy => s_policy;

    /// <summary>Job программ рук; до <see cref="Configure"/> запуск невозможен.</summary>
    public static WindowsHandsSystem? System => s_system;

    /// <param name="browserProfile">
    /// Профиль Chrome браузерной руки (<c>--browser-profile</c>); его родитель — корень профилей
    /// агента, в который <c>app</c> не пускает <c>user-data-dir</c>. Путь не полный — запрета нет,
    /// а браузер откажет сам.
    /// </param>
    public static void Configure(string? browserProfile = null)
    {
        s_system = new WindowsHandsSystem();
        var profilesRoot = BrowserProfilesRoot(browserProfile);
        s_policy = new HandsPolicy(s_system, profilesRoot);
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

    public static CallToolResult Deny(string tool, string reason)
    {
        HandsLog.Write($"гейт: отказ {tool}: {reason}");
        return new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { success = false, error = "hands_policy", tool, message = reason }),
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
