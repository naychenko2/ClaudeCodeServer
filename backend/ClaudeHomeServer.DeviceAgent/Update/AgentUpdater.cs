using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Supervision;
using ClaudeHomeServer.DeviceAgent.Versioning;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Update;

/// <summary>Откуда качать архив агента. Боевой — анонимная ручка <c>/agent/…</c> сервера.</summary>
internal interface IAgentArchiveSource
{
    /// <summary>Архив по пути относительно <c>/agent/</c>, как его прислал ack.</summary>
    Task<Stream> OpenAsync(string relativePath, CancellationToken ct);
}

internal sealed class HttpAgentArchiveSource(HttpClient http, Uri server) : IAgentArchiveSource
{
    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var response = await http.GetAsync(new Uri(server, "agent/" + relativePath), HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Самообновление агента между работой (agent-distribution AD-5, Р2, Р7–Р9).
///
/// Цель — ровно версия, которую назвал сервер в <see cref="DeviceHelloAck.AgentLatestVersion"/>,
/// даже если она ниже текущей: откатили сервер — откатывается и агент. Шаги:
/// 1. архив своего RID качается анонимной ручкой по пути из ack;
/// 2. размер и SHA-256 сверяются с ack — хеш пришёл по аутентифицированному хабу устройства,
///    поэтому архиву без совпадения не верим, даже если он пришёл по TLS;
/// 3. архив распаковывается в staging и одним переносом становится <c>versions/{v}</c>;
/// 4. переключение ждёт пустого <see cref="ActivityRegistry"/>, новые ходы при этом идут;
/// 5. <c>active</c> (на Linux и симлинк <c>current</c>) и автозапуск переставляются на версию;
/// 6. <see cref="RunAsync"/> возвращает true — агент выходит с кодом 75, супервизор поднимает
///    новую версию и откатывает её, если та не станет здоровой.
///
/// Версия, помеченная супервизором плохой, повторно не пробуется, пока не пройдут сутки или
/// сервер не назовёт другую. Сбой скачивания или проверки — <c>failed</c> с причиной и повтор
/// по бэкоффу; <c>active</c> при этом не трогается.
/// </summary>
internal sealed class AgentUpdater : Hosting.IAgentUpdates
{
    /// <summary>Потолок архива: размер приходит от сервера, но диск клиента не резиновый.</summary>
    public const long MaxArchiveSize = 1L << 30;

    private static readonly TimeSpan BadRecheck = TimeSpan.FromHours(1);

    private readonly AgentLayout _layout;
    private readonly VersionedDirectory _dirs;
    private readonly DeviceAgentVersion? _own;
    private readonly string _rid;
    private readonly IAgentArchiveSource _source;
    private readonly ActivityRegistry _activity;
    private readonly Action<string> _repointAutostart;
    private readonly Func<string?> _supervisorVersion;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Offer? _offer;
    private DeviceAgentUpdate _status = new(DeviceAgentUpdateStates.Idle);
    private string? _failedTarget;
    private int _failures;
    private DateTimeOffset? _nextAttemptAt;

    /// <param name="ownVersion">Версия этого процесса (InformationalVersion, <c>+sha</c> не мешает).</param>
    /// <param name="supervisorVersion">Версия, из которой запущен супервизор: её каталог уборка не трогает; null — не знаем, уборки нет.</param>
    public AgentUpdater(AgentLayout layout, string ownVersion, string rid, IAgentArchiveSource source,
        ActivityRegistry activity, Action<string> repointAutostart, Func<string?> supervisorVersion,
        TimeProvider? time = null, ILogger? log = null)
    {
        _layout = layout;
        _log = log ?? NullLogger.Instance;
        _dirs = new VersionedDirectory(layout.Root, _log);
        _own = DeviceAgentVersion.TryParse(ownVersion, out var own) ? own : null;
        _rid = rid;
        _source = source;
        _activity = activity;
        _repointAutostart = repointAutostart;
        _supervisorVersion = supervisorVersion;
        _time = time ?? TimeProvider.System;

        _dirs.ResetStaging();
        _activity.Changed += Wake;
    }

    /// <summary>Состояние для <see cref="DeviceHello.AgentUpdate"/>.</summary>
    public DeviceAgentUpdate Status { get { lock (_gate) return _status; } }

    /// <summary>Состояние поменялось — агент повторяет hello.</summary>
    public event Action<DeviceAgentUpdate>? Changed;

    /// <summary>Ответ сервера на hello: какую версию он называет и чем её проверять.</summary>
    public void OnAck(DeviceHelloAck ack)
    {
        var offer = DeviceAgentVersion.TryParse(ack.AgentLatestVersion?.Trim(), out var latest)
            ? new Offer(latest.ToString(), ack.AgentArchiveSha256?.Trim(), ack.AgentArchiveSize, ack.AgentArchivePath?.Trim())
            : null;
        lock (_gate)
        {
            if (_offer == offer) return;
            _offer = offer;
            if (offer?.Version != _failedTarget)
            {
                _failedTarget = null;
                _failures = 0;
                _nextAttemptAt = null;
            }
        }
        Wake();
    }

    /// <summary>Фоновый цикл: true — новая версия стала активной, агенту пора выйти с кодом 75.</summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await StepAsync(ct)) return true;
                TimeSpan wait;
                lock (_gate)
                    wait = _nextAttemptAt is { } at ? Max(at - _time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
                await _wake.WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return false;
    }

    /// <summary>Один шаг к цели: true — переключились. Ожидание работы и повторы — снаружи, в <see cref="RunAsync"/>.</summary>
    internal async Task<bool> StepAsync(CancellationToken ct)
    {
        Offer? offer;
        lock (_gate)
        {
            offer = _offer;
            if (_nextAttemptAt is { } at && _time.GetUtcNow() < at) return false;
        }

        if (offer is null || _own is not null && offer.Version == _own.ToString())
        {
            SetStatus(new DeviceAgentUpdate(DeviceAgentUpdateStates.Idle));
            return false;
        }
        var target = offer.Version;

        if (_layout.IsBad(target, _time.GetUtcNow()))
        {
            Fail(target, $"версия {target} не запустилась на этом устройстве и помечена плохой — повтор через сутки или на другой версии",
                BadRecheck, countFailure: false);
            return false;
        }

        if (!_layout.IsInstalled(target))
        {
            SetStatus(new DeviceAgentUpdate(DeviceAgentUpdateStates.Downloading, target));
            try
            {
                await InstallAsync(offer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                _log.LogWarning(e, "Обновление агента до {Version} не установлено", target);
                Fail(target, Describe(target, e), backoff: null, countFailure: true);
                return false;
            }
        }

        var holding = _activity.Describe();
        if (holding is not null || !_activity.TrySeal())
        {
            SetStatus(new DeviceAgentUpdate(DeviceAgentUpdateStates.WaitingIdle, target,
                $"обновление ждёт: {holding ?? "идёт работа"}"));
            return false;
        }

        try
        {
            SwitchTo(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _activity.Unseal();
            _log.LogWarning(e, "Не переключился на версию агента {Version}", target);
            Fail(target, $"не переставлен указатель активной версии {target}: {e.Message}", backoff: null, countFailure: true);
            return false;
        }
        return true;
    }

    private async Task InstallAsync(Offer offer, CancellationToken ct)
    {
        var (sha, size, file) = Validate(offer);
        var work = _dirs.CreateStaging(offer.Version);
        try
        {
            var archive = Path.Combine(work, file);
            await DownloadVerifiedAsync(offer.Path!, archive, size, sha, ct);

            var unpacked = Directory.CreateDirectory(Path.Combine(work, "unpacked")).FullName;
            await ExtractAsync(archive, unpacked, ct);
            var exe = Path.Combine(unpacked, SupervisorContract.ExecutableName);
            if (!File.Exists(exe))
                throw new AgentUpdateException($"в архиве нет {SupervisorContract.ExecutableName}");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserRead | UnixFileMode.UserExecute);

            _dirs.Commit(unpacked, offer.Version);
            _log.LogInformation("Версия агента {Version} ({Rid}) скачана и проверена", offer.Version, _rid);
        }
        finally
        {
            _dirs.TryDeleteDirectory(work);
        }
    }

    // Путь из ack — ровно {версия}/{свой RID}/{имя архива}: в URL и на диск ничего иного не попадает
    private (string Sha, long Size, string File) Validate(Offer offer)
    {
        if (offer.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit))
            throw new AgentUpdateException($"сервер не прислал SHA-256 архива под {_rid}");
        if (offer.Size is not ({ } size and > 0 and <= MaxArchiveSize))
            throw new AgentUpdateException($"сервер не прислал допустимый размер архива под {_rid}");
        var prefix = $"{offer.Version}/{_rid}/";
        if (offer.Path is not { } path || !path.StartsWith(prefix, StringComparison.Ordinal))
            throw new AgentUpdateException($"сервер не раздаёт архив агента {offer.Version} под {_rid}");
        var file = path[prefix.Length..];
        if (file is "" or "." or ".." || file.IndexOfAny(['/', '\\', ':']) >= 0 || !IsArchive(file))
            throw new AgentUpdateException($"недопустимое имя архива агента «{file}»");
        return (sha, size, file);
    }

    private static bool IsArchive(string file) =>
        file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadVerifiedAsync(string relativePath, string path, long size, string sha, CancellationToken ct)
    {
        await using var source = await _source.OpenAsync(relativePath, ct);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > size)
                throw new AgentUpdateException($"архив больше заявленного сервером ({size} байт)");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total != size)
            throw new IOException($"скачивание оборвалось: получено {total} из {size} байт");

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
            throw new AgentUpdateException($"SHA-256 архива не совпал с присланным сервером (ожидался {sha}, получен {actual})");
    }

    // Оба распаковщика отказываются писать за пределы каталога назначения
    private static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: false, ct);
            return;
        }
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
    }

    private void SwitchTo(string target)
    {
        var from = _layout.ReadActive();
        _layout.SetActive(target);
        try { _repointAutostart(target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Не страшно: автозапуск поднимет прежний супервизор, а тот прочитает active
            _log.LogWarning("Автозапуск не переписан на {Version}: {Error}", target, e.Message);
        }
        _log.LogInformation("Активная версия агента: {From} → {Version}, выхожу с кодом {Code}",
            from ?? "нет", target, SupervisorContract.SwitchExitCode);
        Sweep(target);
    }

    // Остаются активная, прошлая (откат), своя и версия супервизора; супервизор неизвестен —
    // не трогаем ничего: удалить каталог, из которого он запущен, дороже лишнего места на диске
    private void Sweep(string target)
    {
        if (_supervisorVersion() is not { } supervisor) return;
        var keep = new HashSet<string>(StringComparer.Ordinal) { target, supervisor };
        if (_layout.ReadPrevious() is { } previous) keep.Add(previous);
        if (AgentLayout.OwnVersion() is { } own) keep.Add(own);
        if (_own is not null) keep.Add(_own.ToString());
        _dirs.Sweep(keep.Contains);
    }

    private void Fail(string target, string reason, TimeSpan? backoff, bool countFailure)
    {
        lock (_gate)
        {
            if (_failedTarget != target) _failures = 0;
            _failedTarget = target;
            if (countFailure) _failures++;
            _nextAttemptAt = _time.GetUtcNow() + (backoff ?? ManagedCli.BackoffFor(Math.Max(_failures, 1)));
        }
        SetStatus(new DeviceAgentUpdate(DeviceAgentUpdateStates.Failed, target, reason));
    }

    private void SetStatus(DeviceAgentUpdate status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        try { Changed?.Invoke(status); }
        catch (Exception e) { _log.LogWarning(e, "Подписчик состояния обновления упал"); }
    }

    private static string Describe(string version, Exception e) => e switch
    {
        AgentUpdateException => $"обновление агента до {version} не прошло проверку: {e.Message}",
        HttpRequestException { StatusCode: { } code } => $"сервер не отдал архив агента {version} ({(int)code})",
        HttpRequestException or TimeoutException or TaskCanceledException =>
            $"нет доступа к серверу для скачивания агента {version}: {e.Message}",
        InvalidDataException => $"архив агента {version} не распаковался: {e.Message}",
        IOException => $"установка агента {version} прервана: {e.Message}",
        UnauthorizedAccessException => $"нет прав на каталог агента для установки {version}: {e.Message}",
        _ => $"обновление агента до {version} не удалось: {e.Message}",
    };

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record Offer(string Version, string? Sha256, long? Size, string? Path);
}

/// <summary>Архив обновления не прошёл проверку: размер, хеш, путь или содержимое.</summary>
internal sealed class AgentUpdateException(string message) : Exception(message);
