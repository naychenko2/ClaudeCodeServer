using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using Microsoft.Extensions.Hosting;
using static ClaudeHomeServer.Services.Llm.Claude.ClaudeCliChangelog;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Сторож обновлений claude CLI. Новые модели Claude приходят в продукт только с новой
// версией CLI (маппинг алиаса `sonnet` → конкретной модели зашит в сам CLI), поэтому раз
// в сутки сверяем версию на хосте с npm `latest` и, если хост отстал, шлём админам одно
// уведомление на каждую новую версию. Уведомление — только админам с включённым флагом
// claude-cli-update-watch; версия помечается «уведомлённой» лишь при ≥1 доставке, иначе
// флаг, включённый после первой проверки, молчал бы до следующего релиза CLI.
//
// При отставании подтягивается CHANGELOG.md claude-code: список изменений пропущенных
// версий и новые модели (их называет заголовок уведомления). Файл качается вне _lock —
// refresh из UI не ждёт загрузку, — а результат сужается по актуальному current уже под
// локом. Нет сети или секции latest — работаем как без списка и перекачиваем в следующий раз.
//
// Состояние (data/claude-cli-update.json) переживает рестарт — повтор после перезапуска
// не шлётся. Сеть и CLI недоступны → статус «неизвестно», без уведомления и исключений.
// Смотрит только CLI хоста: у песочницы container-владельцев своя копия.
public sealed class ClaudeCliUpdateWatcher : BackgroundService
{
    public const string HttpClientName = "claude-cli-npm";
    public const string ChangelogClientName = "claude-cli-changelog";
    internal const string LatestUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";
    private const string StateFileName = "claude-cli-update.json";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Status(string? Current, string? Latest, bool? UpdateAvailable, DateTime? CheckedAt,
        IReadOnlyList<NewModel> NewModels, IReadOnlyList<VersionChanges> Changes, int HiddenCount, bool Truncated);

    private sealed class State
    {
        public string? Current { get; set; }
        public string? Latest { get; set; }
        public DateTime? CheckedAt { get; set; }
        public string? LastNotifiedVersion { get; set; }
        // Дайджест CHANGELOG; ссылка заменяется целиком (record'ы неизменяемые) — GetStatus
        // читает без лока
        public Digest? Digest { get; set; }
        // Пара «current..latest», для которой получен дайджест (даже неполный)
        public string? DigestPair { get; set; }
        // Пара, для которой дайджест ПОЛНЫЙ (нашлась секция latest) — перекачка не нужна
        public string? DigestFor { get; set; }
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
        var d = s.Digest;
        return new Status(s.Current, s.Latest, UpdateAvailable(s.Current, s.Latest), s.CheckedAt,
            d?.NewModels ?? [], d?.Versions ?? [], d?.HiddenCount ?? 0, d?.Truncated ?? false);
    }

    // Перечитать только локальную версию (после `claude update` строка в UI сразу показывает
    // актуальность, список изменений сужается); ни npm, ни CHANGELOG не запрашиваются
    public async Task<Status> RefreshCurrentAsync(CancellationToken ct = default)
    {
        var current = await _refreshCurrent();
        await _lock.WaitAsync(ct);
        try
        {
            if (current is not null && current != _state.Current)
            {
                var wasComplete = _state.DigestFor is not null && _state.DigestFor == _state.DigestPair;
                _state.Current = current;
                if (UpdateAvailable(current, _state.Latest) == true)
                {
                    _state.Digest = Since(_state.Digest, current);
                    _state.DigestPair = Pair(current, _state.Latest);
                    // Сужение полного дайджеста даёт полный — перекачка не нужна
                    _state.DigestFor = wasComplete ? _state.DigestPair : null;
                }
                else
                {
                    _state.Digest = null;
                    _state.DigestPair = null;
                    _state.DigestFor = null;
                }
                Save();
            }
        }
        finally { _lock.Release(); }
        return GetStatus();
    }

    // Одна проверка: версия хоста + npm latest (+ CHANGELOG при отставании) → уведомление админам
    public async Task CheckOnceAsync(CancellationToken ct = default)
    {
        var current = await _refreshCurrent();
        var latest = await FetchLatestAsync(ct);

        // 1. Под локом: обновить версии и решить, нужна ли загрузка CHANGELOG
        string? fetchCurrent = null, fetchLatest = null;
        await _lock.WaitAsync(ct);
        try
        {
            if (current is not null) _state.Current = current;
            if (latest is not null) _state.Latest = latest;
            _state.CheckedAt = DateTime.UtcNow;
            if (UpdateAvailable(_state.Current, _state.Latest) == true)
            {
                if (_state.DigestFor != Pair(_state.Current, _state.Latest))
                    (fetchCurrent, fetchLatest) = (_state.Current, _state.Latest);
            }
            else
            {
                _state.Digest = null;
                _state.DigestPair = null;
                _state.DigestFor = null;
            }
            Save();
        }
        finally { _lock.Release(); }

        // 2. Вне лока: CHANGELOG (до 30 с) — refresh из UI его не ждёт
        (Digest Digest, bool HasLatest)? fetched = null;
        if (fetchCurrent is not null)
            fetched = await FetchChangelogAsync(fetchCurrent, fetchLatest!, ct);

        // 3. Под локом: применить к АКТУАЛЬНОМУ состоянию (refresh мог сменить current) и уведомить
        await _lock.WaitAsync(ct);
        try
        {
            var pair = Pair(_state.Current, _state.Latest);
            if (fetched is { } f)
            {
                _state.Digest = Since(f.Digest, _state.Current);
                _state.DigestPair = pair;
                // Неполный (CHANGELOG ещё без секции latest) — перекачаем на следующей проверке
                _state.DigestFor = f.HasLatest ? pair : null;
            }
            else if (fetchCurrent is not null && _state.DigestPair != pair)
            {
                // Дайджест про другие версии врал бы; частичный своей пары сохраняем
                _state.Digest = null;
                _state.DigestPair = null;
            }

            if (UpdateAvailable(_state.Current, _state.Latest) == true
                && _state.Latest != _state.LastNotifiedVersion
                && await NotifyAdminsAsync(_state.Current!, _state.Latest!, _state.Digest?.NewModels ?? []) > 0)
                _state.LastNotifiedVersion = _state.Latest;

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

    private static string Pair(string? current, string? latest) => $"{current}..{latest}";

    // Заголовок и текст уведомления: модели называются в заголовке, пока их одна-две —
    // push на телефоне режет длинный заголовок
    internal static (string Title, string Body) NotificationText(string current, string latest, IReadOnlyList<NewModel> models)
    {
        var tail = $"На сервере {current}, последняя {latest}: выполните claude update на сервере.";
        if (models.Count == 0)
            return ("Доступна новая версия claude CLI",
                $"На сервере {current}, вышла {latest}. Новые модели Claude приходят только с обновлением CLI: выполните claude update на сервере.");

        var title = models.Count switch
        {
            1 => $"Вышла Claude {models[0].Name} — обновите claude CLI",
            2 => $"Вышли Claude {models[0].Name} и {models[1].Name} — обновите claude CLI",
            _ => $"Вышли новые модели Claude ({models.Count}) — обновите claude CLI",
        };
        var list = string.Join(", ", models.Select(m => $"{m.Name} (CLI {m.CliVersion})"));
        return (title, $"{list}. {tail}");
    }

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

    // null — сеть или разбор не удались; дайджест тогда остаётся прежним/пустым
    private async Task<(Digest Digest, bool HasLatest)?> FetchChangelogAsync(string current, string latest, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var client = _http.CreateClient(ChangelogClientName);
            using var resp = await client.GetAsync(SourceUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return Parse(await resp.Content.ReadAsStringAsync(cts.Token), current, latest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "CHANGELOG claude-code не получен — список изменений пропущен");
            return null;
        }
    }

    // Сколько админов получили уведомление; отказ одному не рвёт рассылку остальным
    private async Task<int> NotifyAdminsAsync(string current, string latest, IReadOnlyList<NewModel> models)
    {
        var (title, body) = NotificationText(current, latest, models);
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
                    Title = title,
                    Body = body,
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
