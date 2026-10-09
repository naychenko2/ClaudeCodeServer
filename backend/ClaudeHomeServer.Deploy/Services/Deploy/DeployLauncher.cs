using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Deploy;

/// <summary>Итог выкатки, как его пишет трей-раннер в deploy-status.json.</summary>
/// <remarks>
/// Формат чужой — менять его здесь нельзя, только читать. Result принимает семь значений:
/// running (идёт прямо сейчас), ok, blocked (раннер сознательно ничего не делал — причина в
/// Note), build-failed, rolled-back (сборка не поднялась, возвращена предыдущая), failed, error.
/// Времена — локальные строки «yyyy-MM-dd HH:mm:ss» без смещения, так их пишет раннер.
/// </remarks>
public sealed record DeployStatus(
    string? StartedAt,
    string? FinishedAt,
    string? Mode,
    string? Branch,
    int DirtyFiles,
    string? Head,
    int? DeployExitCode,
    string? Result,
    bool? ProductUp,
    string? Note);

/// <summary>
/// Ход идущей выкатки, как его пишет трей-раннер в deploy-progress.json. Формат тоже чужой.
/// Stage — checking | building | publishing | restarting | done; Percent — 0–100 по весам шагов
/// скрипта деплоя; StartedAt совпадает с полем deploy-status.json того же запуска.
/// </summary>
public sealed record DeployProgress(
    string? StartedAt,
    string? Stage,
    int Percent,
    string? Step,
    string? Line,
    string? UpdatedAt);

/// <summary>Можно ли запускать выкатку, и если нет — почему.</summary>
public sealed record DeployAvailability(bool CanLaunch, string? Reason);

/// <summary>
/// Выкатка боевого продукта по просьбе из веб-морды: проверки, сигнал трею и чтение итога.
///
/// Публикацию делает трей-раннер — мы только просим. Своей работы у сервиса ровно столько,
/// сколько нужно, чтобы не соврать пользователю: понять, есть ли кому принимать команду, и не
/// принять ли чужой прошлый итог за свой (см. ReadStatus и комментарий про StartedAt в
/// контроллере).
/// </summary>
public sealed class DeployLauncher(
    IOptions<TrayDeployOptions> options, ITrayGate tray, ILogger<DeployLauncher> log)
{
    private const string StatusFileName = "deploy-status.json";
    private const string ProgressFileName = "deploy-progress.json";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private TrayDeployOptions Opt => options.Value;

    public bool Enabled => Opt.Enabled;

    /// <summary>Итог последней выкатки: null — файла нет либо прочитать его не удалось.</summary>
    public DeployStatus? ReadStatus() => ReadJson<DeployStatus>(StatusPath);

    /// <summary>
    /// Ход выкатки, которая идёт прямо сейчас: null — выкатка не идёт, раннер старый и файла не
    /// пишет, либо файл остался от другого запуска. Сверка по StartedAt обязательна: хвост
    /// прошлой выкатки с её 100% показал бы полосу «готово» поверх только что начатой.
    /// </summary>
    public DeployProgress? ReadProgress(DeployStatus? status)
    {
        if (!string.Equals(status?.Result, "running", StringComparison.OrdinalIgnoreCase)) return null;

        var dir = Path.GetDirectoryName(StatusPath);
        var progress = ReadJson<DeployProgress>(
            string.IsNullOrEmpty(dir) ? ProgressFileName : Path.Combine(dir, ProgressFileName));
        return progress is not null && progress.StartedAt == status!.StartedAt ? progress : null;
    }

    // Файл хода раннер пишет рядом с файлом статуса — путь к нему от того же StatusPath.
    private string StatusPath => string.IsNullOrWhiteSpace(Opt.StatusPath)
        ? Path.Combine(AppContext.BaseDirectory, StatusFileName)
        : Opt.StatusPath;

    private T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            // Ловим ВСЁ, а не только JsonException: файл пишет другой процесс, и застать его на
            // середине записи — штатная ситуация, а не сбой.
            log.LogWarning(ex, "Не удалось прочитать {Path}.", path);
            return null;
        }
    }

    /// <summary>Проверяет предусловия запуска. Дорогого здесь ничего нет — зовётся и на GET.</summary>
    public DeployAvailability CanLaunch()
    {
        if (!Opt.Enabled) return new(false, "Выкатка из веб-морды выключена в конфиге сервера.");

        // Живость трея — обязательное предусловие, а не удобство: без неё сигнал уходил бы в
        // пустоту, а пользователь ждал бы выкатки, которой никто не делает.
        if (!tray.IsAlive(Opt.EventName))
            return new(false, "Трей-раннер не отвечает — выкатывать некому.");

        var status = ReadStatus();
        if (IsRunningNow(status))
            return new(false, $"Выкатка уже идёт (начата {status!.StartedAt}).");

        return new(true, null);
    }

    /// <summary>
    /// Просит трей опубликовать рабочее дерево «как есть». Вызывать ПОСЛЕ того, как ответ
    /// клиенту отправлен: продукт погаснет через секунду-другую после сигнала.
    /// </summary>
    public bool Signal() => tray.Signal(Opt.EventName);

    // «Выкатка идёт» — это running, начатый недавно. Повисший running означает, что трей умер
    // посреди работы: блокировать им новый запуск нельзя, иначе кнопка залипнет навсегда.
    private bool IsRunningNow(DeployStatus? status)
    {
        if (!string.Equals(status?.Result, "running", StringComparison.OrdinalIgnoreCase)) return false;

        var started = ParseStamp(status!.StartedAt);
        // Время не разобралось — считаем состояние неизвестным и не мешаем запуску: соврать
        // «уже идёт» из-за неразобранной строки хуже, чем пропустить лишний сигнал (двойной
        // запуск всё равно отобьёт сам трей своим Update.Busy).
        if (started is null) return false;

        return DateTime.Now - started.Value < TimeSpan.FromMinutes(Math.Max(1, Opt.StaleRunningMin));
    }

    /// <summary>Разбор времени из файла: локальное, без смещения и без «Z».</summary>
    public static DateTime? ParseStamp(string? stamp)
        => DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : null;
}
