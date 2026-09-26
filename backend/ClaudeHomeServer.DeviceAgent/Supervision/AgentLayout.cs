using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ClaudeHomeServer.DeviceAgent.Hosting;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Раскладка установленных версий агента (Р7): распакованные версии, указатели
/// <c>active</c>/<c>previous</c>, маркеры <c>healthy</c>/<c>bad</c>, на Linux — симлинк
/// <c>current</c>, на который смотрит unit <c>systemd --user</c>. Имена файлов — из
/// замороженного <see cref="SupervisorContract"/>.
/// </summary>
internal sealed partial class AgentLayout(string root)
{
    /// <summary>Сколько версия, помеченная плохой, не пробуется повторно (Р7).</summary>
    public static readonly TimeSpan BadFor = TimeSpan.FromHours(24);

    public string Root { get; } = root;
    public string VersionsDir => Path.Combine(Root, SupervisorContract.VersionsDirName);
    public string ActiveFile => Path.Combine(Root, SupervisorContract.ActiveFileName);
    public string PreviousFile => Path.Combine(Root, SupervisorContract.PreviousFileName);
    /// <summary>Linux: симлинк на активную версию — его и запускает unit автозапуска.</summary>
    public string CurrentLink => Path.Combine(Root, "current");
    public string SupervisorPidFile => Path.Combine(Root, "supervisor.pid");
    public string SupervisorLockFile => Path.Combine(Root, "supervisor.lock");
    public string LogDirectory => Path.Combine(Root, "logs");

    public string VersionDir(string version) => Path.Combine(VersionsDir, Checked(version));
    public string ExeOf(string version) => Path.Combine(VersionDir(version), SupervisorContract.ExecutableName);
    public string HealthyOf(string version) => Path.Combine(VersionDir(version), SupervisorContract.HealthyFileName);
    public string BadOf(string version) => Path.Combine(VersionDir(version), SupervisorContract.BadFileName);

    /// <summary>
    /// Корень установки: если этот процесс запущен из <c>{root}/versions/{v}/</c> (так его
    /// поднимают автозапуск и скрипт установки) — этот root; иначе каталог данных агента.
    /// От места бинаря, а не от окружения: запись Run переменных не несёт, а у
    /// <c>systemd --user</c> они бывают другими, чем в терминале установки.
    /// </summary>
    public static AgentLayout Resolve(AgentPaths paths) =>
        OwnVersionRoot() is { } root ? new AgentLayout(root) : new AgentLayout(paths.DataDirectory);

    /// <summary>Версия, из каталога которой запущен процесс; null — запущен не из versions/.</summary>
    public static string? OwnVersion() => OwnVersionDir()?.Name;

    private static string? OwnVersionRoot() => OwnVersionDir()?.Parent?.Parent?.FullName;

    private static DirectoryInfo? OwnVersionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return dir.Parent?.Name == SupervisorContract.VersionsDirName && IsValidVersion(dir.Name) ? dir : null;
    }

    public string? ReadActive() => ReadVersion(ActiveFile);
    public string? ReadPrevious() => ReadVersion(PreviousFile);

    public bool IsInstalled(string version) => File.Exists(ExeOf(version));
    public bool IsHealthy(string version) => File.Exists(HealthyOf(version));

    /// <summary>Версия помечена плохой меньше <see cref="BadFor"/> назад.</summary>
    public bool IsBad(string version, DateTimeOffset now)
    {
        var file = BadOf(version);
        if (!File.Exists(file)) return false;
        var marked = DateTimeOffset.TryParse(File.ReadAllText(file).Trim(), null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : new DateTimeOffset(File.GetLastWriteTimeUtc(file));
        return now - marked < BadFor;
    }

    public void MarkBad(string version, DateTimeOffset now) => WriteAtomic(BadOf(version), now.UtcDateTime.ToString("O"));

    public void ClearMarkers(string version)
    {
        File.Delete(HealthyOf(version));
        File.Delete(BadOf(version));
    }

    /// <summary>
    /// Сделать версию активной: прошлая активная уходит в <c>previous</c>, на Linux
    /// переставляется симлинк <c>current</c>. Каждый указатель меняется атомарно.
    /// </summary>
    public void SetActive(string version)
    {
        Checked(version);
        var current = ReadActive();
        if (current is not null && current != version) WriteAtomic(PreviousFile, current);
        WriteAtomic(ActiveFile, version);
        if (!OperatingSystem.IsWindows()) PointCurrentLink(version);
    }

    /// <summary>Откат: активной становится <paramref name="version"/>, previous не трогается.</summary>
    public void RollbackTo(string version)
    {
        WriteAtomic(ActiveFile, Checked(version));
        if (!OperatingSystem.IsWindows()) PointCurrentLink(version);
    }

    public static bool IsValidVersion(string? version) =>
        version is { Length: > 0 and <= 64 } && version != "." && version != ".." && VersionName().IsMatch(version);

    /// <summary>Атомарная запись: .tmp + замена, как указатель active у ManagedCli.</summary>
    public static void WriteAtomic(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    [UnsupportedOSPlatform("windows")]
    private void PointCurrentLink(string version)
    {
        // Симлинк рядом и rename поверх: у unit не бывает мгновения без current
        var tmp = CurrentLink + ".tmp";
        File.Delete(tmp);
        File.CreateSymbolicLink(tmp, Path.Combine(SupervisorContract.VersionsDirName, version));
        if (rename(tmp, CurrentLink) != 0)
            throw new IOException($"симлинк {CurrentLink} не переставлен (errno {Marshal.GetLastPInvokeError()})");
    }

    private string? ReadVersion(string file)
    {
        if (!File.Exists(file)) return null;
        var text = File.ReadAllText(file).Trim();
        // Указатель — единственное, что попадает в путь: мусор в нём равен отсутствию версии
        return IsValidVersion(text) ? text : null;
    }

    private static string Checked(string version) =>
        IsValidVersion(version) ? version : throw new ArgumentException($"недопустимое имя версии «{version}»", nameof(version));

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+_-]*$")]
    private static partial Regex VersionName();

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(string oldpath, string newpath);
}
