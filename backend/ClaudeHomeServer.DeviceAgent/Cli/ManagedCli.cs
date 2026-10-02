using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.DeviceAgent.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Cli;

public enum HarnessState { NotReady, Installing, Ready }

/// <summary>
/// Состояние управляемой копии. <see cref="ActiveVersion"/> агент шлёт в hello как
/// <c>CliVersion</c>; <see cref="Problem"/> — причина «харнес не готов» для отказа хода.
/// </summary>
public sealed record HarnessStatus(
    HarnessState State,
    string? RequiredVersion,
    string? ActiveVersion,
    string? Problem,
    DateTimeOffset? NextAttemptAt);

/// <summary>
/// Управляемая копия claude CLI в каталоге агента (ADR-016 §3). CLI пользователя не
/// используется и не трогается: копия живёт в <c>{root}/versions/{version}/</c>, в PATH не
/// попадает, версию задаёт сервер (<c>DeviceHelloAck.RequiredCliVersion</c>).
///
/// Установка атомарная: скачивание в <c>{root}/staging/</c> со сверкой SHA256 и размера по
/// манифесту выпуска, затем один rename каталога в <c>versions/</c>. Манифесту верим только
/// после проверки его GPG-подписи закреплённым ключом Anthropic, бинарю на Windows — ещё и
/// после Authenticode: без них HTTPS + SHA256 из того же канала ничего не доказывают.
/// Каталог версии либо есть целиком и проверенным, либо его нет; прерванная установка
/// оставляет только мусор в staging, который вычищается при старте.
///
/// Ход берёт копию арендой (<see cref="TryAcquire"/>): смена версии переводит на новую
/// только СЛЕДУЮЩИЕ ходы, живой ход дорабатывает на своей, а старый каталог удаляется,
/// когда его последняя аренда закрыта.
///
/// Сбой установки (нет сети, битая целостность) — состояние «не готов» с причиной и
/// повтор по экспоненциальному бэкоффу, а не вечный ретрай.
/// </summary>
public sealed partial class ManagedCli
{
    public const string NotReadyPrefix = "Агент устройства не готов";

    internal static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan BackoffMax = TimeSpan.FromHours(1);

    private const string InstallRecordFile = "install.json";

    private readonly VersionedDirectory _dirs;
    private readonly ICliDistribution _distribution;
    private readonly CliManifestVerifier _manifestVerifier;
    private readonly IExecutableSignatureCheck _executableCheck;
    private readonly CliPlatform _platform;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);

    private bool _requiredKnown;
    private string? _required;
    private string? _active;
    private string? _activeExecutable;
    private bool _installing;
    private string? _problem;
    private int _failures;
    private DateTimeOffset? _nextAttemptAt;

    /// <param name="manifestVerifier">По умолчанию — закреплённый ключ Anthropic.</param>
    /// <param name="executableCheck">По умолчанию — Authenticode на Windows, ничего на прочих ОС.</param>
    public ManagedCli(string rootDirectory, ICliDistribution distribution, CliPlatform platform,
        TimeProvider? time = null, ILogger<ManagedCli>? logger = null,
        CliManifestVerifier? manifestVerifier = null, IExecutableSignatureCheck? executableCheck = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _dirs = new VersionedDirectory(rootDirectory, _logger);
        _distribution = distribution;
        _manifestVerifier = manifestVerifier ?? CliManifestVerifier.Anthropic;
        _executableCheck = executableCheck ?? ExecutableSignatureCheck.ForCurrentOs();
        _platform = platform;
        _time = time ?? TimeProvider.System;

        _dirs.ResetStaging();
        LoadActive();
    }

    /// <summary>Любая смена состояния: агент пересылает hello, когда поменялась активная версия.</summary>
    public event Action<HarnessStatus>? Changed;

    /// <summary>Фактическая версия копии для hello (null — копии нет).</summary>
    public string? ActiveVersion { get { lock (_gate) return _active; } }

    public HarnessStatus Status { get { lock (_gate) return BuildStatus(); } }

    /// <summary>
    /// Версия из ответа сервера на hello. Сама не качает — будит <see cref="RunAsync"/>
    /// (или ждёт явного <see cref="EnsureAsync"/>). Смена требуемой версии сбрасывает бэкофф.
    /// </summary>
    public void SetRequiredVersion(string? version)
    {
        var normalized = Normalize(version);
        lock (_gate)
        {
            if (_requiredKnown && _required == normalized) return;
            _requiredKnown = true;
            _required = normalized;
            _problem = normalized is not null && !IsValidVersion(normalized)
                ? $"сервер прислал недопустимую версию CLI «{normalized}»"
                : null;
            _failures = 0;
            _nextAttemptAt = null;
        }
        Wake();
        RaiseChanged();
    }

    /// <summary>
    /// Одна попытка привести копию к требуемой версии: переключиться на уже стоящую либо
    /// скачать. До <see cref="HarnessStatus.NextAttemptAt"/> после сбоя сеть не трогается.
    /// </summary>
    public async Task<HarnessStatus> EnsureAsync(CancellationToken ct = default)
    {
        await _installLock.WaitAsync(ct);
        try
        {
            string target;
            lock (_gate)
            {
                if (_required is null || !IsValidVersion(_required) || _active == _required)
                {
                    SweepObsolete();
                    return BuildStatus();
                }
                target = _required;

                if (TryReadInstalled(target) is { } installed)
                {
                    SwitchTo(target, installed);
                    return AfterChange();
                }
                if (_nextAttemptAt is { } at && _time.GetUtcNow() < at)
                    return BuildStatus();

                _installing = true;
                _problem = null;
            }
            RaiseChanged();

            try
            {
                var executable = await InstallAsync(target, ct);
                lock (_gate)
                {
                    _installing = false;
                    _failures = 0;
                    _nextAttemptAt = null;
                    if (_required == target) SwitchTo(target, executable);
                    else SweepObsolete();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate) _installing = false;
                RaiseChanged();
                throw;
            }
            catch (Exception e)
            {
                lock (_gate)
                {
                    _installing = false;
                    _failures++;
                    _nextAttemptAt = _time.GetUtcNow() + BackoffFor(_failures);
                    _problem = Describe(target, e);
                }
                _logger.LogWarning(e, "Управляемая копия CLI {Version} не установлена", target);
            }
            return AfterChange();
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>Фоновый цикл агента: ставит требуемую версию и повторяет по бэкоффу.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var status = await EnsureAsync(ct);
            var wait = status.NextAttemptAt is { } at && status.State != HarnessState.Ready
                ? Max(at - _time.GetUtcNow(), TimeSpan.Zero)
                : Timeout.InfiniteTimeSpan;
            await _wake.WaitAsync(wait, ct);
        }
    }

    /// <summary>
    /// Копия для хода. null — харнес не готов, <paramref name="problem"/> — причина для
    /// отказа хода. Аренду держат до конца хода: пока она открыта, её каталог не удаляется.
    /// </summary>
    public CliLease? TryAcquire(out string? problem)
    {
        lock (_gate)
        {
            var status = BuildStatus();
            if (status.State != HarnessState.Ready || _active is null || _activeExecutable is null)
            {
                problem = status.Problem;
                return null;
            }
            _leases[_active] = _leases.GetValueOrDefault(_active) + 1;
            problem = null;
            return new CliLease(this, _active, _activeExecutable);
        }
    }

    internal void Release(string version)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(version, out var count)) return;
            if (count <= 1) _leases.Remove(version);
            else _leases[version] = count - 1;
            SweepObsolete();
        }
    }

    internal static TimeSpan BackoffFor(int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(BackoffBase.Ticks * factor, BackoffMax.Ticks));
        return delay;
    }

    /// <summary>
    /// Версия идёт в имя каталога и в URL — только строгий semver, никаких разделителей пути.
    /// </summary>
    public static bool IsValidVersion(string version) => VersionPattern().IsMatch(version);

    [GeneratedRegex(@"^\d{1,6}\.\d{1,6}\.\d{1,6}(?:-[0-9A-Za-z][0-9A-Za-z.]{0,63})?$")]
    private static partial Regex VersionPattern();

    private static string? Normalize(string? version)
    {
        var v = version?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private async Task<string> InstallAsync(string version, CancellationToken ct)
    {
        var signed = await _distribution.GetManifestAsync(version, ct);
        _manifestVerifier.Verify(signed.Manifest, signed.Signature);
        var manifest = CliManifest.Parse(signed.Manifest);
        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
            throw new CliIntegrityException($"манифест выдан для версии {manifest.Version}, запрошена {version}");
        if (!manifest.Platforms.TryGetValue(_platform.Key, out var build))
            throw new CliUnavailableException($"в выпуске CLI {version} нет сборки для платформы {_platform.Key}");
        ValidateBuild(build);

        var staging = _dirs.CreateStaging(version);
        var done = false;
        try
        {
            var binaryPath = Path.Combine(staging, build.Binary);
            await DownloadVerifiedAsync(version, build, binaryPath, ct);
            _executableCheck.Verify(binaryPath);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(binaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var record = new InstallRecord(version, _platform.Key, build.Binary, build.Sha256.ToLowerInvariant(), build.Size);
            await File.WriteAllTextAsync(Path.Combine(staging, InstallRecordFile), JsonSerializer.Serialize(record), ct);

            // Каталог без годной записи установки — остаток старой ошибки, а не копия: Commit его снесёт
            var final = _dirs.Commit(staging, version);
            done = true;
            _logger.LogInformation("Управляемая копия CLI {Version} ({Platform}) установлена", version, _platform.Key);
            return Path.Combine(final, build.Binary);
        }
        finally
        {
            if (!done) _dirs.TryDeleteDirectory(staging);
        }
    }

    private async Task DownloadVerifiedAsync(string version, CliPlatformBuild build, string path, CancellationToken ct)
    {
        await using var source = await _distribution.OpenBinaryAsync(version, _platform.Key, build.Binary, ct);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > build.Size)
                throw new CliIntegrityException($"бинарь больше заявленного в манифесте ({build.Size} байт)");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total != build.Size)
            throw new IOException($"скачивание оборвалось: получено {total} из {build.Size} байт");

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, build.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CliIntegrityException($"SHA256 не совпал с манифестом (ожидался {build.Sha256}, получен {actual})");

        await target.FlushAsync(ct);
        target.Flush(flushToDisk: true);
    }

    private void ValidateBuild(CliPlatformBuild build)
    {
        var name = build.Binary;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name != Path.GetFileName(name))
            throw new CliIntegrityException($"недопустимое имя бинаря в манифесте: «{name}»");
        // На Windows запускаем сам .exe: обёртка .cmd пошла бы через cmd /c, а он не проксирует stdin.
        if (_platform.IsWindows && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new CliIntegrityException($"для Windows ожидается .exe, в манифесте «{name}»");
        if (build.Sha256.Length != 64 || !build.Sha256.All(Uri.IsHexDigit))
            throw new CliIntegrityException("в манифесте нет корректной SHA256 бинаря");
        if (build.Size <= 0)
            throw new CliIntegrityException("в манифесте нет размера бинаря");
    }

    private void LoadActive()
    {
        var version = _dirs.ReadActive();
        if (version is null || !IsValidVersion(version)) return;
        if (TryReadInstalled(version) is { } executable)
        {
            _active = version;
            _activeExecutable = executable;
        }
    }

    /// <summary>Путь к бинарю проверенной установки версии или null, если её нет целиком.</summary>
    private string? TryReadInstalled(string version)
    {
        try
        {
            var dir = VersionDir(version);
            var recordPath = Path.Combine(dir, InstallRecordFile);
            if (!File.Exists(recordPath)) return null;
            var record = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(recordPath));
            if (record is null || record.Version != version || record.Platform != _platform.Key) return null;
            if (record.Binary != Path.GetFileName(record.Binary)) return null;
            var binary = Path.Combine(dir, record.Binary);
            var info = new FileInfo(binary);
            return info.Exists && info.Length == record.Size ? binary : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Под _gate.
    private void SwitchTo(string version, string executable)
    {
        var previous = _active;
        _active = version;
        _activeExecutable = executable;
        _problem = null;
        try
        {
            _dirs.WriteActive(version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Не записан указатель активной копии CLI");
        }
        _logger.LogInformation("Активная копия CLI: {Previous} → {Version}", previous ?? "нет", version);
        SweepObsolete();
    }

    // Под _gate. Удаляет всё, кроме активной, требуемой и арендованных ходами версий;
    // не удалилось (на Windows файл ещё держит процесс) — попробуем при следующей уборке.
    private void SweepObsolete()
    {
        // Не зная требуемой версии, чужие копии не трогаем: вдруг сервер попросит именно её.
        if (!_requiredKnown) return;
        _dirs.Sweep(name => name == _active || name == _required || _leases.ContainsKey(name));
    }

    private HarnessStatus AfterChange()
    {
        HarnessStatus status;
        lock (_gate) status = BuildStatus();
        RaiseChanged(status);
        return status;
    }

    // Под _gate.
    private HarnessStatus BuildStatus()
    {
        if (_required is not null && _active == _required && _problem is null)
            return new HarnessStatus(HarnessState.Ready, _required, _active, null, null);

        if (_installing)
            return new HarnessStatus(HarnessState.Installing, _required, _active,
                $"{NotReadyPrefix}: идёт установка CLI {_required}", null);

        string reason;
        if (!_requiredKnown) reason = "версия CLI от сервера ещё не получена";
        else if (_required is null) reason = "на сервере не задана версия CLI для устройств";
        else if (_problem is not null)
            reason = _nextAttemptAt is { } at ? $"{_problem}; повтор не раньше {at:HH:mm:ss} UTC" : _problem;
        else reason = $"на устройстве нет CLI {_required}";

        return new HarnessStatus(HarnessState.NotReady, _required, _active, $"{NotReadyPrefix}: {reason}", _nextAttemptAt);
    }

    private string Describe(string version, Exception e) => e switch
    {
        CliIntegrityException => $"проверка целостности CLI {version} не прошла, копия не используется: {e.Message}",
        CliUnavailableException => e.Message,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            $"выпуска CLI {version} нет в {_distribution.Name}",
        HttpRequestException or SocketException or TimeoutException or TaskCanceledException =>
            $"нет доступа к {_distribution.Name} для установки CLI {version}: {e.Message}",
        IOException => $"установка CLI {version} прервана: {e.Message}",
        UnauthorizedAccessException => $"нет прав на каталог агента для установки CLI {version}: {e.Message}",
        _ => $"установка CLI {version} не удалась: {e.Message}",
    };

    private string VersionDir(string version) => _dirs.VersionDir(version);

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void RaiseChanged(HarnessStatus? status = null)
    {
        var handler = Changed;
        if (handler is null) return;
        status ??= Status;
        try { handler(status); }
        catch (Exception e) { _logger.LogWarning(e, "Подписчик состояния CLI упал"); }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record InstallRecord(string Version, string Platform, string Binary, string Sha256, long Size);
}

/// <summary>Аренда копии на ход: пока не закрыта, каталог этой версии не удаляется.</summary>
public sealed class CliLease : IDisposable
{
    private readonly ManagedCli _owner;
    private int _disposed;

    internal CliLease(ManagedCli owner, string version, string executablePath)
    {
        _owner = owner;
        Version = version;
        ExecutablePath = executablePath;
    }

    public string Version { get; }

    /// <summary>Полный путь к бинарю: на Windows — <c>claude.exe</c>, запускать напрямую.</summary>
    public string ExecutablePath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(Version);
    }
}

/// <summary>Нужной сборки в раздаче нет (платформа не выпускается).</summary>
public sealed class CliUnavailableException(string message) : Exception(message);
