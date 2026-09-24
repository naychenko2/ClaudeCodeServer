using System.Text;
using Microsoft.Extensions.Logging;
using ClaudeHomeServer.DeviceAgent.Processes;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>
/// Временный каталог хода: файлы spec (MCP-конфиг, системный промпт) материализуются здесь
/// и удаляются по концу хода вместе с каталогом. На Unix — каталог 0700, файлы 0600: в
/// них адрес хода в сайдкаре, другим пользователям машины он ни к чему.
/// </summary>
internal sealed class TurnWorkspace : IDisposable
{
    private readonly ILogger _log;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    private TurnWorkspace(string directory, ILogger log)
    {
        Directory = directory;
        _log = log;
    }

    public string Directory { get; }

    public static TurnWorkspace Create(string root, string turnId, ILogger log)
    {
        if (!IsSafeSegment(turnId)) throw new ExecRefusedException($"недопустимый идентификатор хода «{turnId}»");

        System.IO.Directory.CreateDirectory(root);
        var dir = Path.Combine(root, turnId);
        if (System.IO.Directory.Exists(dir)) TurnWorkspaceCleanup.TryDelete(dir, log);

        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(dir);
        else System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new TurnWorkspace(dir, log);
    }

    /// <summary>Пишет файлы spec, подставляя адрес хода в сайдкаре вместо плейсхолдера.</summary>
    public void Materialize(IEnumerable<ExecFile> files, string sidecarTurnUrl)
    {
        foreach (var file in files)
        {
            if (!IsSafeSegment(file.Id)) throw new ExecRefusedException($"недопустимый идентификатор файла «{file.Id}»");
            if (!IsSafeSegment(file.Name)) throw new ExecRefusedException($"имя файла spec «{file.Name}» — не просто имя");
            if (_files.ContainsKey(file.Id)) throw new ExecRefusedException($"файл spec «{file.Id}» повторяется");

            // Два файла с одним именем не затирают друг друга: имя — с префиксом id
            var path = Path.Combine(Directory, file.Id + "-" + file.Name);
            var content = file.Content.Replace(ExecSpawnRules.SidecarPlaceholder, sidecarTurnUrl, StringComparison.Ordinal);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(path, options))
                stream.Write(new UTF8Encoding(false).GetBytes(content));
            _files[file.Id] = path;
        }
    }

    /// <summary>Аргументы с путями вместо плейсхолдеров файлов; неизвестный плейсхолдер — отказ.</summary>
    public List<string> ResolveArgs(IEnumerable<string> args)
    {
        var result = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith(ExecSpawnRules.FilePrefix, StringComparison.Ordinal)
                && arg.EndsWith(ExecSpawnRules.FileSuffix, StringComparison.Ordinal))
            {
                var id = arg[ExecSpawnRules.FilePrefix.Length..^ExecSpawnRules.FileSuffix.Length];
                if (!_files.TryGetValue(id, out var path))
                    throw new ExecRefusedException($"аргумент ссылается на файл spec «{id}», которого нет");
                result.Add(path);
            }
            else
            {
                result.Add(arg);
            }
        }
        return result;
    }

    public void Dispose() => TurnWorkspaceCleanup.TryDelete(Directory, _log);

    // Только имя: без разделителей, без «.» и «..», без управляющих символов
    internal static bool IsSafeSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value is not ("." or "..")
        && value.IndexOfAny(['/', '\\', ':', '\0']) < 0
        && !value.Any(char.IsControl)
        && value == value.Trim();
}

/// <summary>Ход не запущен: причина — готовый текст для человека.</summary>
internal sealed class ExecRefusedException(string message) : Exception(message);
