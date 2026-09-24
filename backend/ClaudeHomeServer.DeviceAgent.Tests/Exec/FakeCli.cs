using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Exec;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>Источник аренды для тестов: считает выдачи и возвраты.</summary>
internal sealed class TestCliSource(string? executablePath, string? problem = null) : ICliLeaseSource
{
    private int _open;

    public int Acquired { get; private set; }
    public int Open => Volatile.Read(ref _open);

    public ICliHandle? TryAcquire(out string? reason)
    {
        if (executablePath is null)
        {
            reason = problem;
            return null;
        }
        reason = null;
        Acquired++;
        Interlocked.Increment(ref _open);
        return new Handle(executablePath, () => Interlocked.Decrement(ref _open));
    }

    private sealed class Handle(string path, Action release) : ICliHandle
    {
        private int _disposed;
        public string ExecutablePath => path;
        public string Version => "9.9.9-test";
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }
}

/// <summary>
/// Фейковый CLI для Unix: пишет свой env и argv (с содержимым файлов-аргументов) в рабочий
/// каталог, кладёт файлы сессии в профиль, как настоящий CLI, порождает внука (долгий
/// sleep) и отвечает эхом на строки stdin.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class FakeUnixCli
{
    public const string Script = """
        #!/bin/sh
        env > cli-env.txt
        : > cli-argv.txt
        for a in "$@"; do
          printf '%s\n' "$a" >> cli-argv.txt
          if [ -f "$a" ]; then cat "$a" > "cli-file-$(basename "$a")"; fi
        done
        mkdir -p "$CLAUDE_CONFIG_DIR/sessions"
        echo '{}' > "$CLAUDE_CONFIG_DIR/sessions/$$.json"
        echo k > "$CLAUDE_CONFIG_DIR/sessions/$$.0123abcd.key"
        sleep 600 &
        echo $! > grandchild.pid
        echo $$ > cli.pid
        echo '{"type":"system","subtype":"init"}'
        if [ "$1" = "--burst" ]; then
          i=0
          while [ $i -lt "$2" ]; do echo "line-$i"; i=$((i+1)); done
          exit 0
        fi
        while IFS= read -r line; do
          if [ "$line" = "exit" ]; then exit 3; fi
          echo "echo:$line"
        done
        exit 0
        """;

    public static string Write(string dir)
    {
        var path = Path.Combine(dir, "claude");
        File.WriteAllText(path, Script.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
