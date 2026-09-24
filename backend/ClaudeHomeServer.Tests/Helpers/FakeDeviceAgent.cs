using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Агент устройства для интеграционных тестов раннера (сторож G3): принимает spawn, материализует
/// файлы spec во временном каталоге хода, запускает настоящий процесс (фейковый CLI на node) с
/// окружением, собранным с нуля, и гоняет stdio через кадры. Всё, что агент принял, он
/// записывает в свой журнал в каталоге устройства — так сканер видит и «проводные» данные.
/// </summary>
internal sealed class FakeDeviceAgent(string deviceDir, string fakeCliScript)
{
    public const string SidecarUrl = "http://127.0.0.1:9";

    public ConcurrentQueue<string> ReceivedControl { get; } = new();

    public async Task RunAsync(InProcessExecStream stream)
    {
        var first = await stream.FromServer.ReadAsync();
        var controlText = Encoding.UTF8.GetString(first.Payload.Span);
        Record(controlText);
        var control = DeviceExecJson.Deserialize<DeviceExecControl>(first.Payload.Span)!;
        var spawn = control.Spawn!;

        var turnDir = Path.Combine(deviceDir, "agent", "turns", control.TurnId);
        Directory.CreateDirectory(turnDir);
        var paths = new Dictionary<string, string>();
        foreach (var f in spawn.Files)
        {
            var path = Path.Combine(turnDir, f.Name);
            File.WriteAllText(path, f.Content.Replace(DeviceExecPlaceholders.Sidecar, SidecarUrl));
            paths[DeviceExecPlaceholders.File(f.Id)] = path;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spawn.WorkingDirectory,
        };
        psi.ArgumentList.Add(fakeCliScript);
        foreach (var a in spawn.Args) psi.ArgumentList.Add(paths.GetValueOrDefault(a, a));
        // Окружение CLI — с нуля, по allow-list агента (план, задача 2.2)
        psi.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "SYSTEMROOT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) psi.Environment[name] = v;
        psi.Environment["HOME"] = Path.Combine(deviceDir, "home");
        psi.Environment["ANTHROPIC_BASE_URL"] = SidecarUrl;
        psi.Environment["ANTHROPIC_AUTH_TOKEN"] = "sidecar-stub";
        foreach (var (k, v) in spawn.Env) psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        var stdout = PumpAsync(process.StandardOutput.BaseStream, DeviceExecFrameChannel.Stdout, stream);
        var stderr = PumpAsync(process.StandardError.BaseStream, DeviceExecFrameChannel.Stderr, stream);

        var input = Task.Run(async () =>
        {
            await foreach (var frame in stream.FromServer.ReadAllAsync())
            {
                switch (frame.Channel)
                {
                    case DeviceExecFrameChannel.Stdin:
                        await process.StandardInput.BaseStream.WriteAsync(frame.Payload);
                        await process.StandardInput.BaseStream.FlushAsync();
                        break;
                    case DeviceExecFrameChannel.StdinEof:
                        process.StandardInput.Close();
                        break;
                    case DeviceExecFrameChannel.Control:
                        Record(Encoding.UTF8.GetString(frame.Payload.Span));
                        try { process.Kill(entireProcessTree: true); } catch { }
                        break;
                }
            }
        });

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        await stream.DeviceExitAsync(process.ExitCode);
        _ = input;
    }

    private void Record(string text)
    {
        ReceivedControl.Enqueue(text);
        var log = Path.Combine(deviceDir, "agent", "agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        lock (ReceivedControl) File.AppendAllText(log, text + "\n");
    }

    private static async Task PumpAsync(Stream source, DeviceExecFrameChannel channel, InProcessExecStream stream)
    {
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await source.ReadAsync(buffer)) > 0)
            await stream.DeviceSendAsync(channel, buffer.AsMemory(0, n));
    }

    /// <summary>
    /// Фейковый CLI: пишет свой env и argv (и содержимое файлов-аргументов) в рабочий каталог,
    /// печатает init, отвечает эхом на каждую строку stdin и выходит по его концу.
    /// </summary>
    public const string FakeCliSource = """
        import fs from 'node:fs';
        const argv = process.argv.slice(2);
        const files = {};
        for (const a of argv) { try { if (fs.statSync(a).isFile()) files[a] = fs.readFileSync(a, 'utf8'); } catch {} }
        fs.writeFileSync('cli-dump.json', JSON.stringify({ env: process.env, argv, files }));
        process.stdout.write(JSON.stringify({ type: 'system', subtype: 'init' }) + '\n');
        let buf = '';
        process.stdin.on('data', d => {
          buf += d.toString('utf8');
          let i;
          while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i); buf = buf.slice(i + 1);
            process.stdout.write(JSON.stringify({ type: 'echo', line }) + '\n');
          }
        });
        process.stdin.on('end', () => process.exit(0));
        """;
}
