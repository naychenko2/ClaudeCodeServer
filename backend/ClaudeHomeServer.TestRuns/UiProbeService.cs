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
//    честный отказ ДО запуска браузера (а не таймаут каждой страницы);
//  • процесс — headless Chromium из node_modules проекта через `node --eval` в среде проекта
//    (ILauncherFactory.ForProject: local или песочница); учётка — переменными окружения процесса,
//    не аргументами;
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

    public UiProbeService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline)
        : this(launchers, options, pipeline, standProbe: null) { }

    // Для тестов: своя проверка «стенд жив»
    internal UiProbeService(ILauncherFactory launchers, TestRunsOptions options, PhasePipeline pipeline,
        Func<Uri, CancellationToken, Task<bool>>? standProbe)
    {
        _launchers = launchers;
        _options = options;
        _pipeline = pipeline;
        _standProbe = standProbe ?? ProbeStandAsync;
    }

    public TestRunsOptions Options => _options;

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
                + "запущенный стенд и сам его не поднимает. Подними стенд (start_stand) и повтори вызов.");

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
            var lines = new List<string>();
            var errors = new Queue<string>();
            var gate = new object();
            void OnLine(string line)
            {
                lock (gate)
                {
                    if (line.StartsWith(UiProbeScript.ResultMarker, StringComparison.Ordinal)) lines.Add(line);
                    else
                    {
                        errors.Enqueue(line);
                        if (errors.Count > FailureTailLines) errors.Dequeue();
                    }
                }
            }
            var outcome = await _pipeline.RunProcessAsync(launcher, spec, OnLine, limit.Token);
            if (outcome.Aborted)
                return new UiProbeResult(null, null, "замер прерван", ct.IsCancellationRequested, !ct.IsCancellationRequested);
            string[] tail;
            string[] output;
            lock (gate) { tail = [.. errors]; output = [.. lines]; }
            if (UiProbeReport.FindResultLine(output) is not { } json)
                return new UiProbeResult(null, null,
                    $"Скрипт замера не вернул результат (код выхода {outcome.ExitCode?.ToString() ?? "—"})."
                    + (tail.Length == 0 ? "" : "\n" + string.Join('\n', tail)), false, false);
            try
            {
                var report = UiProbeReport.Build(json, request.Origin + request.Path, request.MinTargetPx);
                return new UiProbeResult(null, report.ToJsonString(new JsonSerializerOptions { WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), null, false, false);
            }
            catch (FormatException e)
            {
                return new UiProbeResult(null, null, e.Message, false, false);
            }
        }
    }

    // node --eval <скрипт> <json>: скрипт целиком одним аргументом (ArgumentList, без shell)
    internal static ProcessSpec BuildSpec(UiProbeRequest request, string turnId)
    {
        var input = new JsonObject
        {
            ["url"] = request.Origin + request.Path,
            ["viewports"] = new JsonArray(request.Viewports.Select(v => (JsonNode)v).ToArray()),
            ["height"] = ViewportHeight,
            ["max"] = UiProbeScript.MaxItems,
            ["selector"] = request.Selector,
        };
        var env = new Dictionary<string, string> { ["NO_COLOR"] = "1" };
        if (request.User is not null)
        {
            env["UIPROBE_USER"] = request.User;
            env["UIPROBE_PASS"] = request.Password ?? "";
        }
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
