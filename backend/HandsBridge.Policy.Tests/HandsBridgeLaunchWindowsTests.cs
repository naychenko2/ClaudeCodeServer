using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Настоящий запуск программ инструментом <c>app</c> (задача 0ab253d5): мост грузится в процесс
/// теста, процесс теста играет агента и мост. Только Windows — на Linux честный skip.
///
/// Процесс сидит в чужом Job без breakaway (так агента держит планировщик задач), а внутри него —
/// Job хода. Боевой сбой: вложенный Job моста был общим на все запуски, и программа, рождённая в
/// другой иерархии job (Win11-notepad — упакованное приложение), закрывала его для всех
/// следующих: AssignProcessToJobObject → ERROR_ACCESS_DENIED, «could not be put under hands
/// control». Здесь другая иерархия создаётся детерминированно: между запусками процесс теста
/// уходит в ещё один вложенный Job, и следующая программа рождается уже в нём.
/// Гейт моста — статика процесса, поэтому тесты в одной коллекции с рантайм-сторожем гейта.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class HandsBridgeLaunchWindowsTests
{
    private const string System32 = @"C:\Windows\System32\";

    [SkippableFact]
    public async Task Programs_from_different_job_hierarchies_are_all_under_control_in_foreign_job()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "запуск программ руками — только Windows");
        using var gate = HandsTestGate.Start();

        var first = await Launch(System32 + "PING.EXE");
        Assert.False(first.IsError, first.Text);

        // Следующие программы рождаются в другой иерархии job, как упакованное приложение
        gate.EnterNestedJob();

        var second = await Launch(System32 + "PING.EXE");
        Assert.False(second.IsError, second.Text);
        var third = await Launch(System32 + "PING.EXE");
        Assert.False(third.IsError, third.Text);
    }

    [SkippableFact]
    public async Task Program_that_exits_right_away_is_reported_as_hand_off_stub()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "запуск программ руками — только Windows");
        using var gate = HandsTestGate.Start();

        // whoami выходит сразу и окон не создаёт — ровно как заглушка, передавшая запуск
        var result = await HandsBridgeGateRuntimeTests.Call("Sbroenne.WindowsMcp.Tools.AppTool", new()
        {
            ["programPath"] = System32 + "whoami.exe",
            ["timeoutMs"] = 3000,
        });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("handed the launch off to another process", result.Text);
        Assert.DoesNotContain("could not be put under hands control", result.Text);
    }

    private static Task<(bool IsError, string Text)> Launch(string program) =>
        HandsBridgeGateRuntimeTests.Call("Sbroenne.WindowsMcp.Tools.AppTool", new()
        {
            ["programPath"] = program,
            ["arguments"] = "-n 30 127.0.0.1",
            ["waitForWindow"] = false,
        });

    /// <summary>
    /// Процесс теста: чужой Job без breakaway → Job хода (его держит агент для KillTree) → гейт моста.
    /// Dispose гасит запущенные программы (закрывает Job моста).
    /// </summary>
    private sealed class HandsTestGate : IDisposable
    {
        private readonly List<nint> _jobs = [];

        public static HandsTestGate Start()
        {
            var gate = new HandsTestGate();
            // Флаги 0: breakaway запрещён, как у Job планировщика задач (замер на стенде)
            gate.Enter(null);
            var turnJob = @"Local\AiHome.Turn.test." + Guid.NewGuid().ToString("N");
            gate.Enter(turnJob);
            GateType.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, []);
            return gate;
        }

        public void EnterNestedJob() => Enter(null);

        private void Enter(string? name)
        {
            var job = CreateJobObjectW(0, name);
            Assert.NotEqual(0, job);
            Assert.True(AssignProcessToJobObject(job, GetCurrentProcess()), $"процесс теста не посажен в Job: {Marshal.GetLastPInvokeError()}");
            _jobs.Add(job);
        }

        public void Dispose()
        {
            var system = GateType.GetProperty("System", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            (system as IDisposable)?.Dispose();
            foreach (var job in _jobs)
                CloseHandle(job);
        }

        private static Type GateType =>
            HandsBridgeGateRuntimeTests.Bridge.Value.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
