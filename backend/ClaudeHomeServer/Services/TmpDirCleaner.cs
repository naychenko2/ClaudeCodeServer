using ClaudeHomeServer.Services.Files;
namespace ClaudeHomeServer.Services;

// Уборка /tmp от временных каталогов тестов и сборки CCS. Тесты не всегда прибирают за
// собой (26.10.2026: 284 тыс. записей, ~45 ГБ), поэтому страхуемся таймером. Чистится
// ТОЛЬКО по префиксам из списка и только то, что старше порога; симлинки не разыменовываются.
public static class TmpDirCleaner
{
    public static readonly string[] Prefixes =
    [
        "team_wave_tests_", "task_exec_join_tests_", "task_stall_tests_", "task_exec_hold_tests_",
        "sess_status_tests_", "tool_call_leak_tests_", "task_device_wait_", "team_blocker_resolve_",
        "hub_leave_tests_", "smgr_", "voice_tests_", "task_report_tests_",
        "ccs-pluginpkg-", "ccs-plugins-", "ccs-ocr-", "ccs-manifest-",
        "gw_", "gw-device-", "hands-", "cgpp_", "brancher_dst_", "MSBuild",
    ];

    // Сокеты отладчика и диагностики привязаны к pid: у живого процесса они нужны сколько
    // бы ни жили, чистим только осиротевшие (pid мёртв).
    private static readonly string[] PidPrefixes = ["clr-debug-pipe-", "dotnet-diagnostic-"];

    public static bool Matches(string name) =>
        Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
        || PidPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>pid из имени сокета («dotnet-diagnostic-1234-…»); null — имя не такого вида.</summary>
    public static int? PidOf(string name)
    {
        foreach (var p in PidPrefixes)
        {
            if (!name.StartsWith(p, StringComparison.Ordinal)) continue;
            var digits = new string(name[p.Length..].TakeWhile(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, out var pid) ? pid : null;
        }
        return null;
    }

    public record Result(int Removed, int Failed, long Bytes);

    public static Result Clean(string tmpDir, DateTime nowUtc, TimeSpan maxAge, Func<int, bool>? pidAlive = null)
    {
        pidAlive ??= pid => Directory.Exists("/proc/" + pid);
        int removed = 0, failed = 0;
        long bytes = 0;
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(tmpDir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception) { return new Result(0, 0, 0); }

        foreach (var entry in entries)
        {
            if (!Matches(entry.Name)) continue;
            if (nowUtc - entry.LastWriteTimeUtc < maxAge) continue;
            if (PidOf(entry.Name) is { } pid && pidAlive(pid)) continue;
            try
            {
                if (entry is DirectoryInfo dir && dir.LinkTarget is null)
                {
                    bytes += WorktreeBuildArtifacts.SizeOf(dir.FullName);
                    dir.Delete(true);
                }
                else
                {
                    if (entry is FileInfo f && f.LinkTarget is null) bytes += f.Length;
                    entry.Delete();
                }
                removed++;
            }
            catch (Exception) { failed++; }
        }
        return new Result(removed, failed, bytes);
    }
}
