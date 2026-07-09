using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Утренний бриф-агент (флаг daily-briefing). Собирает из уже готовых источников:
// просроченные и сегодняшние задачи, изменённые сегодня заметки, git-активность
// проектов владельца за сутки — прогоняет через one-shot Claude и пишет
// структурированный план дня в дневниковую заметку (секция «## Утренний бриф»),
// затем шлёт тост + web-push. Работает on-demand (BriefingController) и по расписанию
// (утренний хук в TaskSchedulerService, идемпотентность — по дате в briefing-state.json).
public sealed class DailyBriefingService
{
    private const string Header = "## Утренний бриф";

    private readonly TaskManager _tasks;
    private readonly NotesService _notes;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly FeatureFlagService _flags;
    private readonly Llm.OneShotClaudeRunner _runner;
    private readonly PushService _push;
    private readonly IHubContext<SessionHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<DailyBriefingService> _log;

    // userId → последняя локальная дата (yyyy-MM-dd), на которую бриф уже сделан.
    // Защита от повторной генерации по расписанию в течение дня; переживает рестарт.
    private readonly string _statePath;
    private readonly Dictionary<string, string> _lastBriefed;
    private readonly Lock _stateLock = new();

    public DailyBriefingService(
        TaskManager tasks, NotesService notes, ProjectManager projects, UserStore users,
        FeatureFlagService flags, Llm.OneShotClaudeRunner runner, PushService push,
        IHubContext<SessionHub> hub, IConfiguration config, ILogger<DailyBriefingService> log)
    {
        _tasks = tasks;
        _notes = notes;
        _projects = projects;
        _users = users;
        _flags = flags;
        _runner = runner;
        _push = push;
        _hub = hub;
        _config = config;
        _log = log;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _statePath = Path.Combine(dataDir, "briefing-state.json");
        _lastBriefed = JsonFileStore.Load<Dictionary<string, string>>(_statePath) ?? new();
    }

    // On-demand генерация (кнопка): бриф на дату (локальная дата клиента; пусто — сегодня
    // в таймзоне юзера), запись в дневник, возврат обновлённой заметки.
    public async Task<NoteDetail> GenerateAsync(string userId, string? date, CancellationToken ct = default)
    {
        var tz = TaskDueCalculator.ResolveTimeZone(_users.GetById(userId)?.TimeZone);
        var day = string.IsNullOrWhiteSpace(date)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).ToString("yyyy-MM-dd")
            : date!.Trim();

        var saved = await BuildAndWriteAsync(userId, tz, day, ct);
        MarkBriefed(userId, day);
        return saved;
    }

    // Хук планировщика: если у юзера включён флаг, наступило утро в его таймзоне и
    // сегодня бриф ещё не делали — запустить генерацию. Гейт синхронный и мгновенный;
    // тяжёлая работа (git-сбор + LLM, десятки секунд) уходит в фон, чтобы НЕ блокировать
    // тик планировщика (напоминания/автозапуски остальных пользователей). Идемпотентность
    // обеспечивается меткой ДО запуска (одна попытка в день, переживает рестарт).
    public Task MaybeRunScheduledAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_flags.IsEnabled(user.Id, FeatureFlagKeys.DailyBriefing)) return Task.CompletedTask;

        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var hour = _config.GetValue("Briefing:Hour", 8);
        if (local.Hour < hour) return Task.CompletedTask; // ещё не утро в таймзоне пользователя

        var day = local.ToString("yyyy-MM-dd");
        if (AlreadyBriefed(user.Id, day)) return Task.CompletedTask;
        MarkBriefed(user.Id, day); // помечаем сразу — не спамим LLM и не даём дублей между тиками

        _ = Task.Run(async () =>
        {
            try
            {
                var note = await BuildAndWriteAsync(user.Id, tz, day, ct);
                await NotifyAsync(user.Id, note.Id);
                _log.LogInformation("Утренний бриф отправлен пользователю {UserId} на {Day}", user.Id, day);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Не удалось сгенерировать утренний бриф пользователю {UserId}", user.Id);
            }
        }, ct);
        return Task.CompletedTask;
    }

    private async Task<NoteDetail> BuildAndWriteAsync(string userId, TimeZoneInfo tz, string day, CancellationToken ct)
    {
        var brief = await BuildBriefAsync(userId, tz, day, ct);
        var daily = _notes.GetOrCreateDaily(userId, day);
        var content = UpsertSection(daily.Content, Header, brief);
        return _notes.Update(userId, daily.Id, new UpdateNoteRequest(Content: content))
            ?? throw new InvalidOperationException("Дневниковая заметка не обновилась");
    }

    // Сбор данных + прогон через LLM → markdown-бриф
    private async Task<string> BuildBriefAsync(string userId, TimeZoneInfo tz, string day, CancellationToken ct)
    {
        // Задачи: только незавершённые, с датой; сравнение по строке ISO (yyyy-MM-dd)
        var open = _tasks.GetByOwner(userId)
            .Where(t => t.Status != TaskItemStatus.Done && t.DueDate is not null).ToList();
        var overdue = open.Where(t => string.CompareOrdinal(t.DueDate, day) < 0).ToList();
        var today = open.Where(t => t.DueDate == day).ToList();

        // Заметки, изменённые сегодня (кроме самого дневника — он и так меняется)
        var changedNotes = _notes.GetSummaries(userId, null, null)
            .Where(s => s.UpdatedAt.StartsWith(day, StringComparison.Ordinal))
            .Take(15).ToList();

        var git = await GitActivityAsync(userId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Ты — личный ассистент. Составь короткий полезный утренний бриф на день по-русски " +
                      "в формате markdown. Структура:");
        sb.AppendLine("- одно ориентирующее предложение о фокусе дня;");
        sb.AppendLine("- **Задачи** — что просрочено (выдели ⚠️), что на сегодня, предложи разумный порядок;");
        sb.AppendLine("- **Заметки** — если менялись, о чём коротко (названия оформляй ссылками [[Заголовок]]);");
        sb.AppendLine("- **Активность** — если есть коммиты, 1-2 фразы что происходило по проектам;");
        sb.AppendLine("- **План на день** — итоговый список из 3-5 конкретных шагов.");
        sb.AppendLine("Без воды и без вступления «вот ваш бриф». Пропускай пустые разделы. " +
                      "Если данных почти нет — сделай очень короткий бриф и мягко предложи запланировать день.");
        sb.AppendLine();

        sb.AppendLine("== Задачи ==");
        if (overdue.Count == 0 && today.Count == 0)
            sb.AppendLine("Нет задач с сроком на сегодня или ранее.");
        else
        {
            if (overdue.Count > 0)
            {
                sb.AppendLine("Просроченные:");
                foreach (var t in overdue.Take(20)) sb.AppendLine(FormatTask(t));
            }
            if (today.Count > 0)
            {
                sb.AppendLine("На сегодня:");
                foreach (var t in today.Take(20)) sb.AppendLine(FormatTask(t));
            }
        }
        sb.AppendLine();

        sb.AppendLine("== Заметки, изменённые сегодня ==");
        if (changedNotes.Count == 0) sb.AppendLine("Нет.");
        else foreach (var n in changedNotes) sb.AppendLine($"- {n.Title} ({n.SourceLabel})");
        sb.AppendLine();

        sb.AppendLine("== Git-активность за сутки ==");
        sb.AppendLine(git.Length == 0 ? "Нет коммитов." : git);

        var model = _runner.NormalizeModel(_config["Briefing:Model"] ?? "haiku");
        return await _runner.RunAsync(sb.ToString(), model, ct: ct);
    }

    private static string FormatTask(TaskItem t)
    {
        var due = t.DueTime is null ? t.DueDate : $"{t.DueDate} {t.DueTime}";
        var prio = t.Priority is TaskItemPriority.High or TaskItemPriority.Urgent ? $" [{t.Priority}]" : "";
        return $"- {t.Title} (срок {due}){prio}";
    }

    // git log за сутки по проектам владельца. Не git-репо / ошибка — пропускаем проект.
    private async Task<string> GitActivityAsync(string userId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var p in _projects.GetByOwner(userId).Take(12))
        {
            var lines = await RunGitLogAsync(p.RootPath, ct);
            if (lines.Count == 0) continue;
            sb.AppendLine($"Проект «{p.Name}»:");
            foreach (var l in lines.Take(8)) sb.AppendLine($"  - {l}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<List<string>> RunGitLogAsync(string rootPath, CancellationToken ct)
    {
        if (!Directory.Exists(rootPath)) return [];
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add("--since=24 hours ago");
            psi.ArgumentList.Add("--no-merges");
            psi.ArgumentList.Add("--pretty=format:%s");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("10");

            using var proc = Process.Start(psi);
            if (proc is null) return [];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var output = await outTask;
            if (proc.ExitCode != 0) return [];
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (Exception)
        {
            return []; // git не установлен / не репо / таймаут — не критично для брифа
        }
    }

    private async Task NotifyAsync(string userId, string dailyNoteId)
    {
        var msg = new NotificationMessage(
            Title: "Утренний бриф готов",
            Body: "План на день собран в дневнике",
            Url: $"/#/notes/{dailyNoteId}",
            Kind: "info");
        await _hub.Clients.Group("user_" + userId).SendAsync("message", msg);
        await _push.SendToUserAsync(userId, msg);
    }

    // Вставка/замена секции по заголовку без затирания остального содержимого дневника
    // (в отличие от «Итогов дня», бриф — не последняя секция). Секция = от заголовка
    // до следующего заголовка уровня # или ## (или до конца).
    internal static string UpsertSection(string content, string header, string body)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var section = $"{header}\n\n{body.Trim()}\n";

        var start = lines.FindIndex(l => l.TrimEnd() == header);
        if (start < 0)
        {
            var trimmed = content.TrimEnd();
            return trimmed.Length == 0 ? section : $"{trimmed}\n\n{section}";
        }

        var end = lines.FindIndex(start + 1, l => l.StartsWith("# ") || l.StartsWith("## "));
        if (end < 0) end = lines.Count;

        var before = string.Join("\n", lines.Take(start)).TrimEnd();
        var after = string.Join("\n", lines.Skip(end)).Trim();
        var result = new StringBuilder();
        if (before.Length > 0) result.Append(before).Append("\n\n");
        result.Append(section);
        if (after.Length > 0) result.Append('\n').Append(after).Append('\n');
        return result.ToString();
    }

    private bool AlreadyBriefed(string userId, string day)
    {
        lock (_stateLock)
            return _lastBriefed.TryGetValue(userId, out var d) && d == day;
    }

    private void MarkBriefed(string userId, string day)
    {
        lock (_stateLock)
        {
            _lastBriefed[userId] = day;
            JsonFileStore.Save(_statePath, _lastBriefed);
        }
    }
}
