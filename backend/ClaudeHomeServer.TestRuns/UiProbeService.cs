using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Запрос замера: адрес стенда уже проверен тулсетом (loopback), Directory — каталог с node_modules
// пакета playwright (процесс запускается из него), Path — маршрут вместе с «#…» у hash-роутера
public sealed record UiProbeRequest(Project Project, string WorkingDirectory, string Directory, string SessionId,
    string Origin, string Path, IReadOnlyList<int> Viewports, int MinTargetPx, string? Selector,
    string? User, string? Password);

// Итог замера: Refusal — отказ до запуска; Json — готовый итог (числа по пяти классам); иначе
// Failure — почему замера нет (скрипт упал, «Стоп», потолок)
public sealed record UiProbeResult(string? Refusal, string? Json, string? Failure, bool Cancelled, bool TimedOut)
{
    public static UiProbeResult Refused(string reason) => new(reason, null, null, false, false);
}

// Замер интерфейса стенда цифрами (инструмент ui_probe, docs/research/local-models-frontend-tasks-plan-2026-10.md,
// шаг 1). Движок на общем конвейере фаз: слот BuildConcurrencyGate (очередь сборок — как у run_tests),
// потолок вместе с очередью, гашение ДЕРЕВА процесса по «Стоп».
//
// Инварианты:
//  • только читает стенд: файлов проекта не пишет, процессов стенда не порождает — нет стенда,
//    честный отказ ДО запуска браузера (а не таймаут каждой страницы): с хоста для local, из самой
//    среды проекта (первым делом скрипт) — для песочницы;
//  • замер страницы логина — не замер: вход не удался или страница ушла на вход — отказ;
//  • процесс — headless Chromium из node_modules проекта через `node --eval` в среде проекта
//    (ILauncherFactory.ForProject: local или песочница); учётка — переменными окружения процесса,
//    не аргументами;
//  • JWT стенда кэшируется (владелец, origin, user, пароль) до истечения с запасом: вход лимитирован
//    10/мин на IP, замер при каждом вызове упёрся бы в 429; 401 на кэшированном токене — один повторный
//    вход. Токен в итог для модели не попадает;
//  • пороги и ширины — параметры запроса, применяются в UiProbeReport.
public sealed class UiProbeService
{
    // Высота окна: замер про ширину экрана, высота — типичная для телефона и ноутбука
    private const int ViewportHeight = 800;

    // Хвост stderr на случай падения скрипта
    private const int FailureTailLines = 15;

    private readonly ILauncherFactory _launchers;
    private readonly TestRunsOptions _options;
    private readonly PhasePipeline _pipeline;
    private readonly Func<Uri, CancellationToken, Task<bool>> _standProbe;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<TokenKey, CachedToken> _tokens = new();
    // Single-flight входа: параллельные холодные вызовы одного ключа ждут первый и берут его токен из кэша
    private readonly ConcurrentDictionary<TokenKey, SemaphoreSlim> _logins = new();

    // Запас до истечения токена и срок жизни токена без exp
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TokenFallbackLifetime = TimeSpan.FromMinutes(10);

    // Пароль в ключе: смена пароля не должна проходить по токену, выданному со старым
    private readonly record struct TokenKey(string? OwnerId, string Origin, string User, string Password);
    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    public UiProbeService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline)
        : this(launchers, options, pipeline, standProbe: null) { }

    // Для тестов: своя проверка «стенд жив» и свои часы
    internal UiProbeService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline,
        Func<Uri, CancellationToken, Task<bool>>? standProbe, TimeProvider? time = null)
    {
        _launchers = launchers;
        _options = options;
        _pipeline = pipeline;
        _standProbe = standProbe ?? ProbeStandAsync;
        _time = time ?? TimeProvider.System;
    }

    public TestRunsOptions Options => _options;

    // Пакеты, один из которых обязан лежать в node_modules дерева (из них скрипт берёт chromium)
    public static IReadOnlyList<string> RequiredPackages => UiProbeScript.Packages;

    // Любой HTTP-ответ — стенд жив; отказ соединения или тайм-аут — лежит. Без прокси
    private static readonly HttpClient StandClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<bool> ProbeStandAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await StandClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<UiProbeResult> RunAsync(UiProbeRequest request, Action<TestRunProgress>? progress,
        CancellationToken ct)
    {
        var launcher = _launchers.ForProject(request.Project);
        var origin = new Uri(request.Origin);
        // Из песочницы стенд хоста не виден по loopback хоста — проба с хоста там врала бы
        if (!launcher.IsSandboxed && !await _standProbe(origin, ct))
            return UiProbeResult.Refused($"Стенд {request.Origin} не отвечает — ui_probe только читает уже "
                + "запущенный стенд и сам его не поднимает. Попроси поднять стенд (start_stand) и повтори вызов.");

        using var limit = PhasePipeline.Ceiling(_options.CeilingSeconds, ct);
        var turnId = ProcessTurnIds.New();
        var spec = BuildSpec(request, turnId);
        var slot = await _pipeline.AcquireAsync(spec,
            busy => progress?.Invoke(new TestRunProgress("queued", $"ждёт очереди сборок (занято {busy})")), limit.Token);
        if (slot is null)
            // «Погашен» здесь было бы враньём: процесса не было
            return new UiProbeResult(null, null, "оборвано в очереди сборок, замер не начинался",
                ct.IsCancellationRequested, !ct.IsCancellationRequested);

        using (slot)
        {
            progress?.Invoke(new TestRunProgress("probe", $"замер · {string.Join(", ", request.Viewports)} px"));
            var key = request.User is null ? (TokenKey?)null
                : new TokenKey(request.Project.OwnerId, request.Origin, request.User, request.Password ?? "");
            SemaphoreSlim? flight = null;
            if (key is { } fk && CachedTokenFor(fk) is null)
            {
                flight = _logins.GetOrAdd(fk, _ => new SemaphoreSlim(1, 1));
                try { await flight.WaitAsync(limit.Token); }
                catch (OperationCanceledException)
                {
                    return new UiProbeResult(null, null, "замер прерван в ожидании входа",
                        ct.IsCancellationRequested, !ct.IsCancellationRequested);
                }
            }
            try
            {
                var cached = key is { } k ? CachedTokenFor(k) : null;
                var run = await RunScriptAsync(launcher, request, turnId, cached, limit.Token, ct);
                // 401 на кэшированном токене — токен отозван или истёк: один повторный вход
                if (cached is not null && run.Json is not null && Has401(run.Json))
                {
                    _tokens.TryRemove(key!.Value, out _);
                    progress?.Invoke(new TestRunProgress("probe", "токен стенда не принят — повторный вход"));
                    run = await RunScriptAsync(launcher, request, turnId, null, limit.Token, ct);
                    cached = null;
                }
                if (run.Result is { } early) return early;
                var json = run.Json!;
                try
                {
                    // Кэш пополняет только настоящий вход: запуск по кэшированному токену срок не продлевает
                    if (cached is null && key is { } kk && run.Token is { } fresh)
                        _tokens[kk] = new CachedToken(fresh, ExpiryOf(fresh));
                    var url = request.Origin + request.Path;
                    if (UiProbeReport.RefusalOf(json, url, request.Origin) is { } refusal) return UiProbeResult.Refused(refusal);
                    var report = UiProbeReport.Build(json, url, request.MinTargetPx, _options.MaxResultBytes);
                    return new UiProbeResult(null, UiProbeReport.Serialize(report), null, false, false);
                }
                catch (FormatException e)
                {
                    return new UiProbeResult(null, null, e.Message, false, false);
                }
            }
            finally { flight?.Release(); }
        }
    }

    // Исход одного запуска: Result — готовый исход без JSON (прервано, нет результата); иначе Json.
    // Token — токен, которого скрипт добился входом (при кэшированном его нет)
    private sealed record ScriptRun(string? Json, UiProbeResult? Result, string? Token);

    // Один запуск скрипта под turnId вызова — тем же, что у слота очереди: гашение по TurnId в песочнице
    private async Task<ScriptRun> RunScriptAsync(IProcessLauncher launcher, UiProbeRequest request, string turnId,
        string? token, CancellationToken limit, CancellationToken ct)
    {
        var spec = BuildSpec(request, turnId, token);
        var lines = new List<string>();
        var errors = new Queue<string>();
        string? fresh = null;
        var gate = new object();
        void OnLine(string line)
        {
            lock (gate)
            {
                // Токен — первым делом и мимо любого накопления вывода: ни в хвост отказа, ни в итог
                if (line.StartsWith(UiProbeScript.TokenMarker, StringComparison.Ordinal))
                {
                    if (line[UiProbeScript.TokenMarker.Length..].Trim() is { Length: > 0 } t) fresh = t;
                }
                else if (line.StartsWith(UiProbeScript.ResultMarker, StringComparison.Ordinal)) lines.Add(line);
                else
                {
                    errors.Enqueue(line);
                    if (errors.Count > FailureTailLines) errors.Dequeue();
                }
            }
        }
        var outcome = await _pipeline.RunProcessAsync(launcher, spec, OnLine, limit);
        if (outcome.Aborted)
            return new(null, new UiProbeResult(null, null, "замер прерван", ct.IsCancellationRequested, !ct.IsCancellationRequested), null);
        string[] tail;
        string[] output;
        string? issued;
        lock (gate) { tail = [.. errors]; output = [.. lines]; issued = fresh; }
        if (UiProbeReport.FindResultLine(output) is { } json) return new(json, null, issued);
        return new(null, new UiProbeResult(null, null,
            $"Скрипт замера не вернул результат (код выхода {outcome.ExitCode?.ToString() ?? "—"})."
            + (tail.Length == 0 ? "" : "\n" + string.Join('\n', tail)), false, false), null);
    }

    private string? CachedTokenFor(TokenKey key)
    {
        if (!_tokens.TryGetValue(key, out var cached)) return null;
        if (cached.ExpiresAt > _time.GetUtcNow()) return cached.Token;
        _tokens.TryRemove(key, out _);
        return null;
    }

    private static bool Has401(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["viewports"] is JsonArray viewports && viewports.OfType<JsonObject>()
                .Any(vp => vp["failed"] is JsonArray failed && failed.OfType<JsonObject>()
                    .Any(f => f["status"] is JsonValue s && s.TryGetValue<int>(out var code) && code == 401));
        }
        catch (JsonException) { return false; }
    }

    // exp из JWT минус запас; нет exp или токен не разобрался — короткий срок по умолчанию
    internal DateTimeOffset ExpiryOf(string jwt)
    {
        var now = _time.GetUtcNow();
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length == 3)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                if (JsonNode.Parse(Convert.FromBase64String(payload))?["exp"] is JsonValue exp && exp.TryGetValue<long>(out var unix))
                    return DateTimeOffset.FromUnixTimeSeconds(unix) - TokenMargin;
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { /* не JWT */ }
        return now + TokenFallbackLifetime;
    }

    // node --eval <скрипт> <json>: скрипт целиком одним аргументом (ArgumentList, без shell)
    internal static ProcessSpec BuildSpec(UiProbeRequest request, string turnId, string? token = null,
        int settleMs = UiProbeScript.SettleTimeoutMs)
    {
        var input = new JsonObject
        {
            ["url"] = request.Origin + request.Path,
            ["viewports"] = new JsonArray(request.Viewports.Select(v => (JsonNode)v).ToArray()),
            ["height"] = ViewportHeight,
            ["max"] = UiProbeScript.MaxItems,
            ["selector"] = request.Selector,
            ["settleMs"] = settleMs,
        };
        var env = new Dictionary<string, string> { ["NO_COLOR"] = "1" };
        if (request.User is not null)
        {
            env["UIPROBE_USER"] = request.User;
            env["UIPROBE_PASS"] = request.Password ?? "";
        }
        if (token is not null) env["UIPROBE_TOKEN"] = token;
        return new ProcessSpec
        {
            FileName = "node",
            Args = ["--eval", UiProbeScript.Source, input.ToJsonString()],
            WorkingDirectory = TestRunService.UpperDriveLetter(request.Directory),
            Env = env,
            StdioEncoding = new UTF8Encoding(false),
            RedirectStdin = false,
            Heavy = true,
            Track = true,
            TurnId = turnId,
            SessionId = request.SessionId,
        };
    }
}
