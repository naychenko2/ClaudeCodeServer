using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Локальная среда: процессы запускаются на машине сервера (историческое поведение).
public sealed class LocalProcessRunner : IProcessLauncher
{
    public static readonly LocalProcessRunner Instance = new();

    public bool IsSandboxed => false;
    public bool TargetIsWindows => OperatingSystem.IsWindows();
    public IPathMapper Paths => IdentityPathMapper.Instance;
    public string ClaudeCliCommand => Llm.Claude.ClaudeCliLocator.FindClaudeExecutable();
    public string HostTempDir => Path.GetTempPath();
    public string? McpApiUrlOverride => null;

    public Process Start(ProcessSpec spec)
    {
        var psi = BuildStartInfo(spec);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
        if (!process.Start())
            throw new InvalidOperationException($"Не удалось запустить {spec.FileName}");
        if (spec.Track) ProcessRegistry.Register(process);
        return process;
    }

    // Сборка ProcessStartInfo вынесена из Start, чтобы правила окружения (что наследуем,
    // что выкидываем) можно было проверить тестом, не запуская процессов: сам запуск
    // непереносим между Windows и linux-раннером CI.
    public static ProcessStartInfo BuildStartInfo(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(spec.FileName),
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (spec.WorkingDirectory is not null)
            psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.StdioEncoding is { } enc)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (spec.RedirectStdin) psi.StandardInputEncoding = enc;
        }
        // RawArguments — сырая строка ВМЕСТО экранированного списка (см. ProcessSpec):
        // ArgumentList экранирует " как \", что ломает cmd /s /c с вложенными кавычками
        if (spec.RawArguments is { } raw) psi.Arguments = raw;
        else foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        // Сначала выкидываем унаследованное (psi.Environment — копия окружения хоста),
        // потом кладём оверрайды: осознанный Env всегда сильнее системной переменной.
        if (spec.ClearEnv is not null)
            foreach (var k in spec.ClearEnv)
                if (psi.Environment.Remove(k)) WarnClearedOnce(k);
        if (spec.Env is not null)
            foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        return psi;
    }

    /// <summary>
    /// Имя исполняемого файла для запуска.
    ///
    /// На Windows `npm`, `npx`, `yarn`, `pnpm` — это .cmd-обёртки, а Process.Start с
    /// UseShellExecute=false расширения из PATHEXT не подставляет: команда «npm» просто
    /// не находится («Не удается найти указанный файл»). Из-за этого с хоста не стартовал
    /// НИ ОДИН сервис, найденный разбором package.json.
    ///
    /// Разворачиваем имя в полный путь сами. Не нашли — возвращаем как было, чтобы
    /// ошибка осталась прежней и понятной, а не подменялась нашей.
    /// </summary>
    public static string ResolveExecutable(string fileName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fileName)) return fileName;
        // Путь (хоть относительный) и имя с расширением ОС разбирает сама
        if (fileName.Contains('/') || fileName.Contains('\\') || Path.HasExtension(fileName))
            return fileName;

        return FindInPath(fileName,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT")) ?? fileName;
    }

    /// <summary>
    /// Поиск команды по каталогам PATH с подстановкой расширений PATHEXT — как это делает
    /// cmd: в каждом каталоге перебираются все расширения, и лишь потом берётся следующий.
    /// Значения приходят параметрами, чтобы правило проверялось тестом на любой ОС.
    /// </summary>
    public static string? FindInPath(string fileName, string? path, string? pathext)
    {
        var exts = (string.IsNullOrWhiteSpace(pathext) ? ".COM;.EXE;.BAT;.CMD" : pathext)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dirs = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawDir in dirs)
        {
            // Записи PATH нередко приходят в кавычках — Path.Combine на них спотыкается
            var dir = rawDir.Trim('"');
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir, fileName + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { break; }   // мусорная запись в PATH
            }
        }
        return null;
    }

    // Факт выброса системной переменной — в лог, по одному разу на ключ за жизнь процесса.
    // Без этого «вчера работало, сегодня не логинится» на машине с ANTHROPIC_API_KEY в
    // окружении превращается в гадание: снаружи видно только отказ авторизации CLI.
    // Значение НЕ печатаем — это секрет. Вернуть прежнее наследование: Claude:InheritSystemEnv=true.
    private static readonly HashSet<string> _warnedKeys = [];
    private static void WarnClearedOnce(string key)
    {
        lock (_warnedKeys)
        {
            if (!_warnedKeys.Add(key)) return;
        }
        Console.WriteLine($"[exec] системная переменная {key} не пущена в процесс claude "
            + "(маршрут задаёт сервер; вернуть наследование — Claude:InheritSystemEnv=true)");
    }

    public void Kill(Process process, string? turnId = null)
    {
        // Всё дерево: claude порождает node-процессы MCP-серверов
        try { process.Kill(entireProcessTree: true); }
        catch { /* процесс уже завершился */ }
    }
}
