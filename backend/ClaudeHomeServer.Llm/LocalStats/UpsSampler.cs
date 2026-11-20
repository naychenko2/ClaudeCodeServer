using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// ИБП фермы для вкладки «Стенды»: читается у upsd (NUT) по TCP командой LIST VAR — без логина,
// только чтение. Без Ups:Host блок выключен (сводка фермы отдаёт ups = null).
public sealed class UpsOptions
{
    public const string Section = "Ups";

    // Адрес upsd; пусто — ИБП не опрашивается.
    public string? Host { get; set; }
    public int Port { get; set; } = 3493;
    // Имя ИБП в ups.conf.
    public string Name { get; set; } = "ups";
    // Подпись карточки.
    public string Title { get; set; } = "ИБП";
    // Активная мощность по паспорту, Вт: нагрузку в % переводим в ватты; null — не считаем.
    public int? RatedW { get; set; }
}

// Снимок ИБП. null — переменной у ИБП нет (протокол Megatec, например, не отдаёт battery.runtime).
public sealed record UpsSample
{
    public DateTimeOffset At { get; init; }
    // Флаги ups.status как есть: «OL», «OB», «OB LB», «OL CHRG»…
    public string? Status { get; init; }
    public int? LoadPerc { get; init; }
    public int? ChargePerc { get; init; }
    public double? BatteryV { get; init; }
    public double? BatteryNominalV { get; init; }
    public double? InputV { get; init; }
    public double? InputHz { get; init; }
    public double? OutputV { get; init; }
    public int? TempC { get; init; }
}

public interface IUpsSampler
{
    // false — Ups:Host не задан.
    bool Enabled { get; }
    UpsOptions Options { get; }
    UpsSample? Current { get; }
    // "ok" | "stale" | "down"; снимков ещё нет — "down".
    string Status { get; }
    // Начало нештатного статуса, unix-мс; null — статус ok.
    long? Since { get; }
    // Кольцо в бакеты по step, максимум в бакете; null — разрыв.
    IReadOnlyList<int?> Aggregate(Func<UpsSample, int?> selector, TimeSpan window, TimeSpan step);
}

// Разбор ответа upsd на LIST VAR.
public static class NutVarParser
{
    private static readonly Regex VarLine = new(@"^VAR (\S+) (\S+) ""(.*)""$", RegexOptions.Compiled);

    // Переменные ИБП ups; ответ без END LIST VAR или с ERR — null.
    public static Dictionary<string, string>? Parse(string text, string ups)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var ended = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("ERR ", StringComparison.Ordinal)) return null;
            if (line == $"END LIST VAR {ups}") { ended = true; break; }
            var m = VarLine.Match(line);
            if (m.Success && m.Groups[1].Value == ups)
                vars[m.Groups[2].Value] = m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return ended ? vars : null;
    }

    public static UpsSample ToSample(IReadOnlyDictionary<string, string> v, DateTimeOffset at) => new()
    {
        At = at,
        Status = v.TryGetValue("ups.status", out var s) && s.Length > 0 ? s : null,
        LoadPerc = Int(v, "ups.load"),
        ChargePerc = Int(v, "battery.charge"),
        BatteryV = Num(v, "battery.voltage"),
        BatteryNominalV = Num(v, "battery.voltage.nominal"),
        InputV = Num(v, "input.voltage"),
        InputHz = Num(v, "input.frequency"),
        OutputV = Num(v, "output.voltage"),
        TempC = Int(v, "ups.temperature"),
    };

    private static double? Num(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Int(IReadOnlyDictionary<string, string> v, string key) =>
        Num(v, key) is { } d ? (int)Math.Round(d) : null;
}

public sealed class UpsSampler : BackgroundService, IUpsSampler
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    // Последний удачный опрос старше — stale (три пропуска подряд).
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly ILogger<UpsSampler> _log;
    private readonly object _gate = new();
    private readonly List<UpsSample> _ring = [];
    private UpsSample? _current;
    private bool _down = true;
    private DateTimeOffset? _downSince;

    // Чтение ответа upsd; тест подменяет.
    internal Func<CancellationToken, Task<string>> ReadFunc { get; set; }

    public UpsSampler(IOptions<UpsOptions> options, TimeProvider time, ILogger<UpsSampler>? log = null)
    {
        Options = options.Value;
        _time = time;
        _log = log ?? NullLogger<UpsSampler>.Instance;
        ReadFunc = ReadUpsdAsync;
    }

    public UpsOptions Options { get; }
    public bool Enabled => !string.IsNullOrWhiteSpace(Options.Host);

    public UpsSample? Current { get { lock (_gate) return _current; } }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_down || _current is null) return "down";
                return _time.GetUtcNow() - _current.At > StaleAfter ? "stale" : "ok";
            }
        }
    }

    public long? Since
    {
        get
        {
            lock (_gate)
            {
                if (_down || _current is null) return _downSince?.ToUnixTimeMilliseconds();
                return _time.GetUtcNow() - _current.At > StaleAfter ? _current.At.ToUnixTimeMilliseconds() : null;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                await Task.Delay(PollInterval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Один опрос: ответ разобран — снимок в кольцо; сбой или ERR — down (хост живёт).
    internal async Task PollOnceAsync(CancellationToken ct = default)
    {
        Dictionary<string, string>? vars = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReadTimeout);
            vars = NutVarParser.Parse(await ReadFunc(cts.Token), Options.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ИБП {Ups}: upsd не ответил", Options.Name);
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (vars is null)
            {
                if (!_down) _downSince = now;
                _downSince ??= now;
                _down = true;
                return;
            }
            var sample = NutVarParser.ToSample(vars, now);
            _ring.Add(sample);
            while (_ring.Count > 0 && now - _ring[0].At > HistoryWindow)
                _ring.RemoveAt(0);
            _current = sample;
            _down = false;
            _downSince = null;
        }
    }

    private async Task<string> ReadUpsdAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Options.Host!, Options.Port, ct);
        await using var stream = tcp.GetStream();
        var cmd = Encoding.ASCII.GetBytes($"LIST VAR {Options.Name}\n");
        await stream.WriteAsync(cmd, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sb.Append(line).Append('\n');
            if (line.StartsWith("END LIST VAR", StringComparison.Ordinal) || line.StartsWith("ERR ", StringComparison.Ordinal))
                break;
        }
        await stream.WriteAsync("LOGOUT\n"u8.ToArray(), ct);
        return sb.ToString();
    }

    public IReadOnlyList<int?> Aggregate(Func<UpsSample, int?> selector, TimeSpan window, TimeSpan step)
    {
        List<UpsSample> ring;
        lock (_gate)
            ring = [.. _ring];
        var start = _time.GetUtcNow() - window;
        var count = (int)Math.Ceiling(window.TotalSeconds / step.TotalSeconds);
        var maxes = new int?[count];
        foreach (var s in ring)
        {
            var idx = (int)Math.Floor((s.At - start).TotalSeconds / step.TotalSeconds);
            if (idx < 0 || idx >= count || selector(s) is not { } v) continue;
            if (maxes[idx] is null || v > maxes[idx]) maxes[idx] = v;
        }
        return maxes;
    }
}
