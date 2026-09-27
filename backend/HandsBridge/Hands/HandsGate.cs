using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using ModelContextProtocol.Protocol;
using Sbroenne.WindowsMcp.Automation;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Точка входа инструментов в гейт <see cref="HandsPolicy"/>. Пока <see cref="Configure"/> не
/// вызван, своих окон нет и запускать некуда — закрыто по умолчанию.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HandsGate
{
    private static WindowsHandsSystem? s_system;
    private static HandsPolicy s_policy = new(new NoWindows());

    public static HandsPolicy Policy => s_policy;

    /// <summary>Job программ рук; до <see cref="Configure"/> запуск невозможен.</summary>
    public static WindowsHandsSystem? System => s_system;

    /// <param name="turnJob">Имя Job хода — его передаёт агент при подключении моста к ходу.</param>
    public static void Configure(string? turnJob)
    {
        s_system = new WindowsHandsSystem(turnJob);
        s_policy = new HandsPolicy(s_system);
    }

    /// <summary>hwnd корня, записанный в идентификатор элемента; null — id неизвестен.</summary>
    public static long? ElementWindow(string elementId) =>
        ElementIdGenerator.TryResolveWindowHandle(elementId, out var hwnd) ? hwnd : null;

    /// <summary>Отказ гейта в форме ответа инструмента.</summary>
    public static CallToolResult Deny(string tool, HandsDecision decision) =>
        Deny(tool, decision.Reason ?? "Not allowed.");

    public static CallToolResult Deny(string tool, string reason) =>
        new()
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

    private sealed class NoWindows : IHandsWindowSystem
    {
        public int? GetWindowProcessId(long hwnd) => null;
        public bool IsProcessInTurnJob(int processId) => false;
        public string? GetProcessImagePath(int processId) => null;
    }
}
