using System.Runtime.InteropServices;
using static ConPtyBridge.ConPtyNative;

namespace ConPtyBridge;

/// <summary>
/// ConPTY-мост — Windows-аналог backend/pty-bridge/pty-bridge.c.
///
/// Сажает powershell.exe в псевдоконсоль (ConPTY) и релеит:
///   stdin (кадры протокола) → input-пайп ConPTY / ResizePseudoConsole;
///   output-пайп ConPTY → stdout (сырой VT UTF-8, без обёртки).
///
/// ПРОТОКОЛ stdin: [type:1][len:4 big-endian][payload]
///   type=0x00 — данные ввода; type=0x01 — resize (cols BE, rows BE). См. FrameParser.
///
/// Запуск: ConPtyBridge.exe [cols rows]. cwd/env наследуются от родителя.
/// Коды выхода: код ребёнка | 2 — ошибка инициализации | 64 — не Windows.
/// </summary>
internal static class Program
{
    // Сигналы между потоками: ребёнок умер / reader дочитал output до EOF
    private static readonly ManualResetEventSlim ChildExited = new(false);
    private static readonly ManualResetEventSlim OutputDrained = new(false);

    public static int Main(string[] args)
    {
        // ConPTY есть только на Windows (10 1809+); на других ОС мост бессмыслен
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("conpty-bridge: только Windows");
            return 64;
        }

        // Размер терминала с клампами — как в pty-bridge.c
        short cols = 80, rows = 24;
        if (args.Length >= 2)
        {
            _ = short.TryParse(args[0], out cols);
            _ = short.TryParse(args[1], out rows);
        }
        if (cols < 8) cols = 80;
        if (rows < 2) rows = 24;

        PseudoConsoleSession session;
        try
        {
            session = PseudoConsoleSession.Create(cols, rows);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"conpty-bridge: инициализация ConPTY: {ex.Message}");
            return 2;
        }

        using (session)
        {
            // Поток A (output-релей) стартует ДО запуска ребёнка: powershell при старте
            // пишет много VT, буфер пайпа мал — без активного чтения CreateProcess
            // и ClosePseudoConsole могут зависнуть (главная грабля ConPTY)
            var outputThread = new Thread(() => RelayOutput(session)) { IsBackground = true, Name = "conpty-output" };
            outputThread.Start();

            try
            {
                session.Spawn("powershell.exe -NoLogo");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"conpty-bridge: запуск шелла: {ex.Message}");
                return 2;
            }

            // Поток C — наблюдатель ребёнка
            var waitThread = new Thread(() =>
            {
                WaitForSingleObject(session.ChildProcess, Infinite);
                ChildExited.Set();
            }) { IsBackground = true, Name = "conpty-wait" };
            waitThread.Start();

            // Поток B (главный): stdin → кадры → input-пайп / resize.
            // Возврат означает stdin EOF (сервер закрыл ввод) или смерть ребёнка.
            RelayInput(session);

            if (!ChildExited.IsSet)
            {
                // Сценарий stdin EOF (StopAsync/Dispose на сервере или падение сервера):
                // закрытие псевдоконсоли — аналог SIGHUP, conhost завершает ребёнка
                session.CloseConsole();
                if (WaitForSingleObject(session.ChildProcess, 3000) != WaitObject0)
                    TerminateProcess(session.ChildProcess, 1);
            }
            else
            {
                // Сценарий «ребёнок умер» (юзер набрал exit): закрываем консоль,
                // чтобы conhost закрыл output-пайп и reader дочитал хвост до EOF
                session.CloseConsole();
            }

            // Дождаться, пока reader дочитает остаток вывода (EOF придёт после
            // CloseConsole); таймаут — страховка от зависшего conhost
            OutputDrained.Wait(TimeSpan.FromSeconds(2));

            GetExitCodeProcess(session.ChildProcess, out var exitCode);
            return unchecked((int)exitCode);
        }
    }

    /// <summary>Поток A: output-пайп ConPTY → stdout (сырой VT-поток).</summary>
    private static void RelayOutput(PseudoConsoleSession session)
    {
        try
        {
            using var pipe = new FileStream(session.OutputRead, FileAccess.Read, 0, isAsync: false);
            using var stdout = Console.OpenStandardOutput();
            var buf = new byte[65536];
            while (true)
            {
                int n;
                try { n = pipe.Read(buf, 0, buf.Length); }
                catch (IOException) { break; }       // пайп закрыт (ClosePseudoConsole)
                catch (ObjectDisposedException) { break; }
                if (n <= 0) break;                    // EOF
                stdout.Write(buf, 0, n);
                stdout.Flush();
            }
        }
        catch { /* stdout закрыт сервером — молча выходим, завершение решает главный поток */ }
        finally
        {
            OutputDrained.Set();
        }
    }

    /// <summary>Поток B (главный): stdin → FrameParser → input-пайп / resize.</summary>
    private static void RelayInput(PseudoConsoleSession session)
    {
        FileStream? inputPipe = null;
        try
        {
            inputPipe = new FileStream(session.InputWrite, FileAccess.Write, 0, isAsync: false);
            var pipe = inputPipe;
            var parser = new FrameParser(
                onData: data =>
                {
                    try
                    {
                        pipe.Write(data);
                        pipe.Flush();
                    }
                    catch (IOException) { /* ребёнок умер, пайп закрыт — дочитываем stdin вхолостую */ }
                    catch (ObjectDisposedException) { }
                },
                onResize: (c, r) => session.Resize(c, r));

            using var stdin = Console.OpenStandardInput();
            var buf = new byte[65536];
            while (!ChildExited.IsSet)
            {
                int n;
                try { n = stdin.Read(buf, 0, buf.Length); }
                catch (IOException) { break; }
                if (n <= 0) break; // stdin EOF — сервер закрыл ввод
                parser.Feed(buf.AsSpan(0, n));
            }
        }
        finally
        {
            inputPipe?.Dispose();
        }
    }
}
