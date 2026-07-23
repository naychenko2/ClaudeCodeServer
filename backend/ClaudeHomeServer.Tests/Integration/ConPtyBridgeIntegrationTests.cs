using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Integration;

/// <summary>
/// Интеграционные тесты ConPTY-моста с реальной псевдоконсолью: гоняют
/// ConPtyBridge.exe (лежит в bin тестов через ProjectReference) и общаются с ним
/// кадровым протоколом «с другой стороны» — независимая проверка протокола.
/// На не-Windows (Linux CI) честно скипаются через SkippableFact.
/// </summary>
[Collection("ConPty")]
public class ConPtyBridgeIntegrationTests
{
    private static string BridgePath => Path.Combine(AppContext.BaseDirectory, "ConPtyBridge.exe");

    private static void RequireWindowsAndBridge()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ConPTY есть только на Windows");
        Skip.IfNot(File.Exists(BridgePath), "ConPtyBridge.exe не найден в bin тестов");
    }

    /// <summary>
    /// Обвязка живого моста: запуск exe, отправка кадров, аккумуляция stdout.
    /// PowerShell в псевдоконсоли поднимается секунды (профиль юзера) — таймауты щедрые.
    /// </summary>
    private sealed class BridgeHarness : IDisposable
    {
        public Process Process { get; }
        private readonly StringBuilder _output = new();
        private readonly object _lock = new();

        public BridgeHarness(int cols = 100, int rows = 30)
        {
            Process = Process.Start(new ProcessStartInfo
            {
                FileName = BridgePath,
                ArgumentList = { cols.ToString(), rows.ToString() },
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            _ = Task.Run(async () =>
            {
                var buf = new byte[65536];
                var s = Process.StandardOutput.BaseStream;
                while (true)
                {
                    var n = await s.ReadAsync(buf);
                    if (n <= 0) break;
                    lock (_lock) _output.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            });
        }

        public string Output { get { lock (_lock) return _output.ToString(); } }

        public void SendData(string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var frame = new byte[5 + payload.Length];
            frame[0] = 0x00;
            frame[1] = (byte)((payload.Length >> 24) & 0xFF);
            frame[2] = (byte)((payload.Length >> 16) & 0xFF);
            frame[3] = (byte)((payload.Length >> 8) & 0xFF);
            frame[4] = (byte)(payload.Length & 0xFF);
            payload.CopyTo(frame, 5);
            Process.StandardInput.BaseStream.Write(frame);
            Process.StandardInput.BaseStream.Flush();
        }

        public void SendResize(int cols, int rows)
        {
            Process.StandardInput.BaseStream.Write(new byte[]
            {
                0x01, 0, 0, 0, 4,
                (byte)((cols >> 8) & 0xFF), (byte)(cols & 0xFF),
                (byte)((rows >> 8) & 0xFF), (byte)(rows & 0xFF),
            });
            Process.StandardInput.BaseStream.Flush();
        }

        public bool WaitForOutput(string marker, int timeoutMs = 10_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Output.Contains(marker)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        /// <summary>Дождаться готовности шелла (промпт «PS » отрисован).</summary>
        public void WaitForPrompt() => WaitForOutput("PS ").Should().BeTrue("powershell должен подняться и отрисовать промпт");

        public void CloseStdin() => Process.StandardInput.BaseStream.Close();

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch { /* уже завершился */ }
            Process.Dispose();
        }
    }

    [SkippableFact]
    public void Echo_исполняется_и_вывод_содержит_VT_последовательности()
    {
        RequireWindowsAndBridge();
        using var h = new BridgeHarness();
        h.WaitForPrompt();

        h.SendData("echo pty-marker-42\r");

        h.WaitForOutput("pty-marker-42").Should().BeTrue("команда должна исполниться по Enter");
        // \x1b[ — доказательство, что это ConPTY (VT-рендер), а не голый пайп
        h.Output.Should().Contain("\x1b[");
    }

    [SkippableFact]
    public void Backspace_редактирует_строку_до_исполнения()
    {
        RequireWindowsAndBridge();
        using var h = new BridgeHarness();
        h.WaitForPrompt();

        // «echo abXX» + два DEL стирают XX + «cd» → исполняется «echo abcd».
        // Это главный смысл моста: line discipline живая, \x7f реально стирает
        h.SendData("echo abXX");
        Thread.Sleep(300);
        h.SendData("\x7f\x7f");
        Thread.Sleep(300);
        h.SendData("cd\r");

        h.WaitForOutput("abcd").Should().BeTrue("backspace должен стереть XX, итог — echo abcd");
    }

    [SkippableFact]
    public void Resize_меняет_ширину_консоли_шелла()
    {
        RequireWindowsAndBridge();
        using var h = new BridgeHarness();
        h.WaitForPrompt();

        h.SendResize(97, 33);
        Thread.Sleep(500);
        // Маркер-обёртка [W-...-W]: голое число нашлось бы в VT-последовательностях
        h.SendData("\"[W-$($Host.UI.RawUI.WindowSize.Width)-W]\"\r");

        h.WaitForOutput("[W-97-W]").Should().BeTrue("после resize-кадра шелл должен видеть ширину 97");
    }

    [SkippableFact]
    public void Закрытие_stdin_завершает_мост_и_шелл_без_сирот()
    {
        RequireWindowsAndBridge();
        using var h = new BridgeHarness();
        h.WaitForPrompt();

        h.CloseStdin();

        // Мост обязан сам погасить шелл (ClosePseudoConsole = аналог SIGHUP) и выйти
        h.Process.WaitForExit(5000).Should().BeTrue("мост должен завершиться сам после stdin EOF");
    }
}
