using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Tests.Helpers;

// Фейковый онлайн-статус устройства для тестов фоновой работы (ADR-016, план §5): серверный
// проект всегда готов, локальный — по текущему Verdict.
public sealed class FakeProjectDeviceGate : IProjectDeviceGate
{
    public ProjectBackgroundVerdict Verdict { get; set; } = ProjectBackgroundVerdict.WaitDevice;
    public string Reason { get; set; } = ProjectCapabilities.DeviceOfflineReason;

    public void GoOnline() => Verdict = ProjectBackgroundVerdict.Ready;
    public void GoOffline() => Verdict = ProjectBackgroundVerdict.WaitDevice;

    public ProjectBackgroundGate Check(Project project) =>
        !ProjectCapabilities.IsDeviceBound(project)
            ? ProjectBackgroundGate.Ready
            : new ProjectBackgroundGate(Verdict, project.DeviceId,
                Verdict == ProjectBackgroundVerdict.Ready ? null : Reason);
}
