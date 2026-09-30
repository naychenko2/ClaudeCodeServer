using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Сторож обновлений claude CLI. Новые модели Claude приходят в продукт только с новой
// версией CLI (маппинг алиаса `sonnet` → конкретной модели зашит в сам CLI), поэтому раз
// в сутки сверяем версию на хосте с npm `latest` и, если хост отстал, шлём админам одно
// уведомление на каждую новую версию. Уведомление — только админам с включённым флагом
// claude-cli-update-watch; версия помечается «уведомлённой» лишь при ≥1 доставке, иначе
// флаг, включённый после первой проверки, молчал бы до следующего релиза CLI.
//
// Состояние (data/claude-cli-update.json) переживает рестарт — повтор после перезапуска
// не шлётся. Сеть и CLI недоступны → статус «неизвестно», без уведомления и исключений.
// Смотрит только CLI хоста: у песочницы container-владельцев своя копия.
public sealed class ClaudeCliUpdateWatcher : BackgroundService
{
    public const string HttpClientName = "claude-cli-npm";
    internal const string LatestUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";
    private const string StateFileName = "claude-cli-update.json";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Status(string? Current, string? Latest, bool? UpdateAvailable, DateTime? CheckedAt);

    private sealed class State
    {
        public string? Current { get; set; }
        public string? Latest { get; set; }
        public DateTime? CheckedAt { get; set; }
        public string? LastNotifiedVersion { get; set; }
    }

    private readonly IHttpClientFactory _http;
    private readonly IUserStore _users;
    private readonly IFeatureFlagGate _flags;
    private readonly INotificationSender _notifications;
    private readonly ILogger<ClaudeCliUpdateWatcher> _log;
    private readonly Func<Task<string?>> _refreshCurrent;
    private readonly string _statePath;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private State _state;

    public ClaudeCliUpdateWatcher(IHttpClientFactory http, IUserStore users, IFeatureFlagGate flags,
        INotificationSender notifications, IConfiguration config, ILogger<ClaudeCliUpdateWatcher> log)
        : this(http, users, flags, notifications, log, ClaudeCliVersion.RefreshAsync,
            Path.Combine(SubsystemHostingExtensions.ResolveDataDir(config), StateFileName),
            config.GetValue("ClaudeCliUpdate:Interval", TimeSpan.FromHours(24)))
    {
    }

    // Для тестов: подменный опрос версии и путь состояния во временной папке
    internal ClaudeCliUpdateWatcher(IHttpClientFactory http, IUserStore users, IFeatureFlagGate flags,
        INotificationSender notifications, ILogger<ClaudeCliUpdateWatcher> log,
        Func<Task<string?>> refreshCurrent, string statePath, TimeSpan interval)
    {
        _http = http;
        _users = users;
        _flags = flags;
        _notifications = notifications;
        _log = log;
        _refreshCurrent = refreshCurrent;
        _statePath = statePath;
        _interval = interval;
        _state = Load(statePath, log);
    }

    // Снимок для эндпоинта — сеть не трогает
    public Status GetStatus()
    {
        var s = _state;
        return new Status(s.Current, s.Latest, UpdateAvailable(s.Current, s.Latest), s.CheckedAt);
    }

    // Перечитать только локальную версию (после `claude update` строка в UI сразу показывает
    // актуальность); npm не опрашивается
    public async Task<Status> RefreshCurrentAsync(CancellationToken ct = default)
    {
        var current = await _refreshCurrent();
        await _lock.WaitAsync(ct);
        try
        {
            if (current is not null && current != _state.Current)
            {
                _state.Current = current;
                Save();
            }
        }
        finally { _lock.Release(); }
        return GetStatus();
    }

    // Одна проверка: версия хоста + npm latest → при отставании уведомление админам
    public async Task CheckOnceAsync(CancellationToken ct = default)
    {
        var current = await _refreshCurrent();
        var latest = await FetchLatestAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (current is not null) _state.Current = current;
            if (latest is not null) _state.Latest = latest;
            _state.CheckedAt = DateTime.UtcNow;

            if (UpdateAvailable(current, latest) == true && latest != _state.LastNotifiedVersion
                && await NotifyAdminsAsync(current!, latest!) > 0)
                _state.LastNotifiedVersion = latest;

            Save();
        }
        finally { _lock.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(_interval);
            do
            {
                try { await CheckOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Проверка обновлений claude CLI не удалась"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // null — сравнить нельзя (что-то неизвестно или не парсится)
    internal static bool? UpdateAvailable(string? current, string? latest) =>
        Version.TryParse(current, out var c) && Version.TryParse(latest, out var l) ? l > c : null;

    private async Task<string?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var client = _http.CreateClient(HttpClientName);
            using var resp = await client.GetAsync(LatestUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                && Version.TryParse(v.GetString(), out _) ? v.GetString() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "npm registry не ответил на запрос версии claude CLI");
            return null;
        }
    }

    // Сколько админов получили уведомление; отказ одному не рвёт рассылку остальным
    private async Task<int> NotifyAdminsAsync(string current, string latest)
    {
        var delivered = 0;
        foreach (var admin in _users.GetAll().Where(u => u.Role == "admin"))
        {
            try
            {
                if (!_flags.IsEnabled(admin.Id, FeatureFlagKeys.ClaudeCliUpdateWatch)) continue;
                await _notifications.SendAsync(admin.Id, new CreateNotificationRequest
                {
                    Kind = "info",
                    Type = "claude_cli_update",
                    Title = "Доступна новая версия claude CLI",
                    Body = $"На сервере {current}, вышла {latest}. Новые модели Claude приходят только с обновлением CLI: выполните claude update на сервере.",
                    // Раздел «Модели и расход», вкладка «Расход» — там строка с версиями.
                    // Форма «/…»: service worker превращает её в /#models для web-push
                    Url = "/models",
                    Tag = "Claude CLI",
                    Source = "Claude CLI",
                }, sendPush: true);
                delivered++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Не удалось отправить уведомление об обновлении claude CLI админу {AdminId}", admin.Id);
            }
        }
        return delivered;
    }

    private static State Load(string path, ILogger log)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Состояние сторожа claude CLI не прочиталось — начинаем с чистого");
        }
        return new State();
    }

    // Под _lock: атомарная запись tmp + move
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось сохранить состояние сторожа claude CLI");
        }
    }
}
