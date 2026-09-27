using System.Diagnostics;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>Состояние моста: <see cref="Problem"/> — почему рук нет, текстом для человека.</summary>
internal sealed record HandsComponentCheck(bool Ready, string? Problem, string? Version);

/// <summary>
/// Мост рук на машине (ADR-016 §7): <c>HandsBridge.exe</c> едет в составе агента и лежит в
/// каталоге его версии (<c>versions/{v}/</c>) рядом с <c>ai-home-agent.exe</c>, как трей.
/// Отдельной установки и своей сверки нет: целостность моста — это целостность архива агента,
/// чей SHA-256 агент сверяет при самообновлении (<c>AgentUpdater</c>).
/// Машинного выключателя тоже нет (решение владельца 2026-09-27): руки выключаются тумблером
/// проекта.
/// </summary>
internal sealed class HandsComponent(string agentDirectory)
{
    public const string MissingText =
        "В каталоге агента устройства нет моста рук (" + HandsFiles.BridgeExe + ") — переустановите агента.";

    /// <summary>Мост рядом с исполняемым файлом этого агента.</summary>
    public static HandsComponent ForThisAgent() => new(AppContext.BaseDirectory);

    public string BridgePath { get; } = Path.Combine(agentDirectory, HandsFiles.BridgeExe);

    public HandsComponentCheck Check()
    {
        try
        {
            if (!File.Exists(BridgePath)) return new(false, MissingText, null);
            var version = FileVersionInfo.GetVersionInfo(BridgePath).ProductVersion;
            return new(true, null, string.IsNullOrWhiteSpace(version) ? null : version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, MissingText, null);
        }
    }

    public bool IsReady => File.Exists(BridgePath);
}
