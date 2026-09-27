using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>
/// Выдача папки локального проекта (решение владельца 2026-09-27, ADR-016 §5): при создании и
/// перепривязке проекта сервер просит агента операцией <see cref="BindFolderProtocol.Operation"/>,
/// и агент сам создаёт папку и добавляет в корни машины ровно её.
///
/// Коридор узкий: папка уже годится (каталог под корнями) — ничего не меняется; иначе путь
/// проходит <see cref="AgentForbiddenPaths"/> (лексически и реальным путём), создаётся, если
/// его нет, и добавляется в корни с подписью проекта; общий на запись каталог не добавляется
/// (<see cref="AgentRootsStore.Add"/>). Выключатель — <c>roots auto off</c> на машине: тогда
/// ответ <see cref="BindFolderOutcomes.AutoOff"/>, и сервер отказывает, как раньше. Каждая
/// выдача пишется в лог агента.
/// </summary>
internal sealed class ProjectFolderBinder(
    AgentRootsStore roots, AgentPathPolicy policy, AgentForbiddenContext forbidden, ILogger? log = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    // Созданная папка — только для владельца на запись: иначе её не примет AgentRootsStore.Add
    private const UnixFileMode CreatedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public BindFolderResult Bind(BindFolderRequest request)
    {
        var path = request.Path;
        // Сетевой путь отбивается до CheckRoot: иначе агент сам постучится на чужой сервер
        if (AgentForbiddenPaths.ShapeRefusalOf(path) is { } shape)
            return Refused(BindFolderOutcomes.Forbidden, shape);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Refused(BindFolderOutcomes.Refused, "Нужен абсолютный путь этой машины");

        try
        {
            var check = policy.CheckRoot(path);
            // Папка уже годится: выдавать нечего, запретный список судит только выдачу
            if (check.IsDirectory && check.InsideRoots) return new BindFolderResult(BindFolderOutcomes.Bound);
            if (check.Exists && !check.IsDirectory)
                return Refused(BindFolderOutcomes.NotDirectory, "По этому пути лежит файл, а не папка");
            if (!roots.AutoEnabled)
                return new BindFolderResult(BindFolderOutcomes.AutoOff,
                    Exists: check.Exists, IsDirectory: check.IsDirectory, InsideRoots: check.InsideRoots);

            if (AgentForbiddenPaths.Check(path, forbidden) is { } refusal)
            {
                _log.LogWarning("Выдача папки {Path} отклонена: {Refusal}", path, refusal);
                return Refused(BindFolderOutcomes.Forbidden, refusal);
            }

            var created = false;
            if (!check.Exists)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(check.RealPath);
                else Directory.CreateDirectory(check.RealPath, CreatedMode);
                created = true;
            }

            // Между проверкой и созданием по дороге могла появиться ссылка — сверка ещё раз
            var real = AgentPathPolicy.RealPath(path);
            if (AgentForbiddenPaths.Check(real, forbidden) is { } late)
            {
                _log.LogWarning("Выдача папки {Path} отклонена после создания: {Refusal}", path, late);
                return Refused(BindFolderOutcomes.Forbidden, late);
            }

            var added = false;
            if (!check.InsideRoots)
            {
                roots.AddAuto(real, request.ProjectName);
                added = true;
            }
            _log.LogWarning("Автовыдача папки проекта «{Project}»: {Path} (создана: {Created}, добавлена в корни: {Added})",
                request.ProjectName, real, created, added);
            return new BindFolderResult(BindFolderOutcomes.Bound, Created: created, Added: added);
        }
        catch (SharedRootException e) { return Refused(BindFolderOutcomes.Refused, e.Message); }
        catch (UnauthorizedAccessException e) { return Refused(BindFolderOutcomes.Refused, e.Message); }
        catch (IOException e) { return Refused(BindFolderOutcomes.Refused, e.Message); }
    }

    private static BindFolderResult Refused(string outcome, string message) => new(outcome, Message: message);

    /// <summary>Обслуживает канал одной выдачи: кадр запроса → заголовок, тело, конец.</summary>
    public async Task RunAsync(ExecLink link, CancellationToken ct)
    {
        await using var owned = link;
        try
        {
            var request = await ReadRequestAsync(link, ct);
            var result = request is null
                ? Refused(BindFolderOutcomes.Refused, "Нет запроса выдачи папки")
                : Bind(request);
            var body = JsonSerializer.SerializeToUtf8Bytes(result, RelayProtocol.Json);
            await link.SendAsync(DeviceExecFrameChannel.Info,
                JsonSerializer.SerializeToUtf8Bytes(new RelayResponseHead(200, "application/json; charset=utf-8", body.LongLength), RelayProtocol.Json), ct);
            await link.SendAsync(DeviceExecFrameChannel.Stdout, body, ct);
            await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(0)), ct);
            await link.DrainAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            _log.LogWarning(e, "Выдача папки {ExecId} не обслужена", link.ExecId);
        }
    }

    private static async Task<BindFolderRequest?> ReadRequestAsync(ExecLink link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RelayProtocol.RequestTimeout);
        try
        {
            await foreach (var frame in link.ReadAllAsync(timeout.Token))
            {
                if (frame.Channel != DeviceExecFrameChannel.Control) return null;
                try { return JsonSerializer.Deserialize<BindFolderRequest>(frame.Payload.Span, RelayProtocol.Json); }
                catch (JsonException) { return null; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return null;
    }

    /// <summary>
    /// Боевой контекст запретного списка: профиль (лексически и реальным путём), каталоги
    /// агента (конфиг, данные, установка) и точки монтирования машины.
    /// </summary>
    public static AgentForbiddenContext ContextForCurrentMachine(AgentPaths paths)
    {
        var homes = new List<string>();
        foreach (var home in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetEnvironmentVariable("HOME"),
                     Environment.GetEnvironmentVariable("USERPROFILE"),
                 })
        {
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) continue;
            homes.Add(Path.GetFullPath(home));
            try { homes.Add(AgentPathPolicy.RealPath(home)); } catch (IOException) { }
        }

        var agent = new List<string> { paths.ConfigDirectory, paths.DataDirectory, AppContext.BaseDirectory };
        try { agent.Add(Supervision.AgentLayout.Resolve(paths).Root); } catch (IOException) { }

        return new AgentForbiddenContext(homes.Distinct().ToList(), agent.Distinct().ToList(), MountPoints());
    }

    // Точки монтирования: на Linux — /proc/self/mounts (пробелы в пути — \040), иначе корни дисков
    private static List<string> MountPoints()
    {
        var result = new List<string>();
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mounts"))
            {
                foreach (var line in File.ReadLines("/proc/self/mounts"))
                {
                    var fields = line.Split(' ');
                    if (fields.Length > 1)
                        result.Add(fields[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\"));
                }
            }
            result.AddRange(DriveInfo.GetDrives().Select(d => d.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result.Distinct().ToList();
    }
}
