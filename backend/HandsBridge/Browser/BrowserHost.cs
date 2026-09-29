using System.Diagnostics;
using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;
using ClaudeHomeServer.HandsBridge.Browser.Launch;
using Microsoft.Win32;

namespace ClaudeHomeServer.HandsBridge.Browser;

/// <summary>
/// Итог <see cref="BrowserHost.AcquireAsync"/>: браузер либо отказ для модели (по-английски, как
/// остальные отказы моста). <see cref="Restarted"/> — прежний Chrome умер (окно закрыли руками),
/// поднят новый: ссылки снимка устарели.
/// </summary>
internal sealed record BrowserAcquire(CdpBrowser? Browser, bool Restarted, string? Refusal);

/// <summary>
/// Один Chrome на процесс моста в профиле проекта (<c>--browser-profile</c>): ленивый старт на
/// первом <c>browser_*</c>, перезапуск после смерти, закрытие при выходе моста. Нет Chrome или
/// профиль занят — отказ инструмента, мост и остальные руки работают.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserHost : IAsyncDisposable
{
    /// <summary>Текст модели после перезапуска: состояние снимка сброшено.</summary>
    public const string RestartedNotice =
        "The browser was restarted (its window had been closed): earlier snapshot refs are gone, take a fresh browser_snapshot.";

    private const string NoProfileRefusal =
        "The browser is not available: the device agent did not pass a browser profile to the hands bridge (update the AI Home agent).";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly string? _profileDirectory;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private ChromeProcess? _process;
    private CdpConnection? _connection;
    private CdpBrowser? _browser;
    private bool _disposed;

    public BrowserHost(string? profileDirectory) => _profileDirectory = profileDirectory;

    /// <summary>Хост моста; до <see cref="Configure"/> профиля нет и браузер отказывает.</summary>
    public static BrowserHost Shared { get; private set; } = new(null);

    public static void Configure(string? profileDirectory) => Shared = new BrowserHost(profileDirectory);

    public async Task<BrowserAcquire> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_browser is not null && _process is { HasExited: false } && _connection is { IsClosed: false })
                return new BrowserAcquire(_browser, false, null);

            var restarted = _process is not null;
            if (restarted)
            {
                HandsLog.Write($"браузер: Chrome {_process!.ProcessId} умер ({await DescribeCloseAsync()}), поднимаем заново");
                await StopAsync(graceful: false);
            }

            var refusal = await StartAsync(cancellationToken);
            return refusal is null ? new BrowserAcquire(_browser, restarted, null) : new BrowserAcquire(null, false, refusal);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_profileDirectory))
        {
            HandsLog.Write("браузер: агент не передал --browser-profile");
            return NoProfileRefusal;
        }

        var chromePath = FindChrome();
        if (chromePath is null)
        {
            HandsLog.Write("браузер: Chrome не найден (App Paths, ProgramFiles, LOCALAPPDATA, CHROME_PATH)");
            return ChromeLocator.NotFoundRefusal;
        }

        // Каталог профиля создаёт мост на первом browser_*, а не агент на каждом ходу
        Directory.CreateDirectory(_profileDirectory);
        var state = ChromeProfileLock.Check(_profileDirectory, ChromeProcess.MessageWindowExists);
        if (state.Busy)
        {
            HandsLog.Write($"браузер: профиль «{_profileDirectory}» занят другим Chrome — {state.Detail}");
            return ChromeProfileLock.BusyRefusal;
        }

        var version = ChromeFileVersion(chromePath);
        try
        {
            _process = ChromeProcess.Start(chromePath, _profileDirectory);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            HandsLog.Write($"браузер: запуск '{chromePath}' ({ex.Message}) {HandsLog.Win32(ex.NativeErrorCode)}");
            return $"Chrome {version} could not be started: {ex.Message}";
        }

        _connection = new CdpConnection(_process);
        _browser = new CdpBrowser(_connection);
        try
        {
            // Ключ --remote-debugging-io-pipes недокументирован: если новая версия Chrome его
            // не поймёт, труба оборвётся на первой же команде — отказ с версией в тексте
            var info = await _browser.GetVersionAsync(cancellationToken).WaitAsync(HandshakeTimeout, cancellationToken);
            await _browser.DenyDownloadsAsync(cancellationToken);
            HandsLog.Write($"браузер: Chrome {_process.ProcessId} запущен ({info.Product}), профиль «{_profileDirectory}»");
            return null;
        }
        catch (Exception ex) when (ex is CdpException or TimeoutException)
        {
            HandsLog.Write($"браузер: Chrome {version} не ответил по трубе отладки: {ex.Message}; {state.Detail}");
            await StopAsync(graceful: false);
            return $"Chrome {version} did not answer on the debugging pipe ({ex.Message}). " +
                   "This Chrome version may not support --remote-debugging-io-pipes.";
        }
    }

    private async Task<string> DescribeCloseAsync() =>
        _connection is { IsClosed: true } ? await _connection.Closed : "процесс вышел";

    private async Task StopAsync(bool graceful)
    {
        if (graceful && _browser is not null && _connection is { IsClosed: false })
        {
            try
            {
                await _browser.CloseAsync(CloseTimeout);
            }
            catch (Exception ex) when (ex is CdpException or TimeoutException)
            {
                // Не закрылся штатно — закроют труба и Job ниже
            }
        }

        if (_connection is not null)
            await _connection.DisposeAsync();
        if (_process is not null)
            await _process.DisposeAsync();
        _connection = null;
        _browser = null;
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopAsync(graceful: true);
        }
        finally
        {
            _sync.Release();
        }
    }

    private static string? FindChrome()
    {
        var appPaths = new List<string?>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                appPaths.Add(key?.GetValue(null) as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Ветку не прочитать — ищем дальше по каталогам установки
            }
        }

        return ChromeLocator.Find(appPaths, Environment.GetEnvironmentVariable, File.Exists);
    }

    private static string ChromeFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "(unknown version)";
        }
        catch (FileNotFoundException)
        {
            return "(unknown version)";
        }
    }
}
