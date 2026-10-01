using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Отмена сессии (DisposeAsync → _cts.Cancel) при молчащем stdout не должна выглядеть как
// срабатывание сторожа: связанный Task.Delay завершается Canceled, WhenAny отдавал его, и цикл
// слал «Модель не отвечает более 60 мин» (инцидент: ошибки ровно в момент switch-release.sh).
// Побочно такая ошибка подавляла маркер «Сервер был перезапущен во время хода».
public class ClaudeSessionReadLoopCancelTests
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;
    private static readonly MethodInfo ReadLoopMethod =
        typeof(ClaudeSession).GetMethod("ReadLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task ОтменаСессииПриМолчащемStdout_ОшибкиСторожаНет()
    {
        var messages = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: m => { lock (messages) messages.Add(m); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        var session = new ClaudeSession(new Session(), context);

        // Живой процесс с молчащим stdout; завершится сам, если тест упадёт раньше уборки
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardInput = true;
        using var process = Process.Start(psi)!;

        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        CliRunType.GetField("TurnGotEvent")!.SetValue(run, true); // активный ход, result не пришёл

        using var cts = new CancellationTokenSource();
        // Вызов синхронно доходит до первого await (WhenAny) — к моменту возврата цикл уже ждёт
        var loop = (Task)ReadLoopMethod.Invoke(session, [run, cts.Token])!;
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        // Финализация может прислать собственную ошибку смерти процесса — это другой путь; проверяем сторожа
        messages.OfType<ErrorMessage>().Should().NotContain(e => e.Text.Contains("не отвечает"),
            "отмена сессии — не тишина модели");
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
    }
}
