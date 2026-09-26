using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.Protocol;
using ModelContextProtocol.Protocol;
using Sbroenne.WindowsMcp.Automation;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Точка входа инструментов в гейт <see cref="HandsPolicy"/>. Пока <see cref="Configure"/> не
/// вызван, белый список пуст и своих окон нет — закрыто по умолчанию.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HandsGate
{
    private static readonly JsonSerializerOptions AppsJson = new(JsonSerializerDefaults.Web);

    private static WindowsHandsSystem? s_system;
    private static HandsPolicy s_policy = new(() => null, new NoWindows());

    public static HandsPolicy Policy => s_policy;

    /// <summary>Job программ рук; до <see cref="Configure"/> запуск невозможен.</summary>
    public static WindowsHandsSystem? System => s_system;

    /// <param name="appsFile">Путь к <c>hands-apps.json</c> машины — его передаёт агент при запуске моста.</param>
    public static void Configure(string? appsFile)
    {
        s_system = new WindowsHandsSystem();
        s_policy = new HandsPolicy(() => ReadApps(appsFile), s_system);
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

    private static HandsAppsFile? ReadApps(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<HandsAppsFile>(File.ReadAllText(path), AppsJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class NoWindows : IHandsWindowSystem
    {
        public int? GetWindowProcessId(long hwnd) => null;
        public bool IsProcessInAppsJob(int processId) => false;
        public string? GetProcessImagePath(int processId) => null;
    }
}
