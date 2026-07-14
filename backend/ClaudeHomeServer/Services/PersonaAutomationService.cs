using System.Text;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.TriggerSources;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services;

// Проактивность персон: событийно-управляемый rules-движок «триггер → действие».
// Collaborator внутри ЕДИНСТВЕННОГО тика TaskSchedulerService (НЕ отдельный BackgroundService —
// иначе третий параллельный таймер, как у удалённой PersonaProactiveService). Источники-опросчики
// (Timer/File/Note/GitCommit/TaskStatus) вызываются из MaybeRunAutomationsAsync; Mention — push
// (подписка на SessionManager.OnUserMessage). Запуск реакции — по образцу TaskExecutionService:
// большой промпт с контекстом срабатывания (## секции + объекты-триггеры; для файлов — пути и
// содержимое) запускает ход персоны в закреплённом чате правила (acceptEdits). Weight=Work —
// действовать/править; Gate — оценить и ответить. Троттлинг: тихие часы + MinInterval + потолок N/час.
public sealed class PersonaAutomationService : IDisposable
{
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly PushService _push;
    private readonly IHubContext<SessionHub> _hub;
    private readonly NotificationService _notif;
    private readonly AutomationStateStore _state;
    private readonly MentionTriggerSource _mentions;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly IReadOnlyDictionary<AutomationTriggerType, ITriggerSource> _sources;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaAutomationService> _log;

    private int DefaultMinIntervalMinutes => _config.GetValue("Persona:AutomationMinIntervalMinutes", 5);
    private int HourlyCap => _config.GetValue("Persona:AutomationHourlyCap", 8);

    // Дефолтный кулдаун File-триггера короче общего: изменения файлов копятся в самом дереве
    // проекта и один скан после кулдауна отдаёт их единым батч-событием.
    private const int FileDefaultMinIntervalMinutes = 1;

    // Единый расчёт кулдауна правила. Тик и FireAsync ОБЯЗАНЫ считать одинаково: рассинхрон
    // дефолтов приводил к тому, что тик отдавал батч через 1 мин, а FireAsync резал его по
    // 5 мин как throttled — накопленные события файлов терялись.
    private int EffectiveMinIntervalMinutes(PersonaAutomationRule rule) =>
        rule.Condition?.MinIntervalMinutes
        ?? (rule.Trigger.Type == AutomationTriggerType.File ? FileDefaultMinIntervalMinutes : DefaultMinIntervalMinutes);

    public PersonaAutomationService(PersonaManager personas, SessionManager sessions,
        PushService push, IHubContext<SessionHub> hub,
        NotificationService notif,
        AutomationStateStore state, MentionTriggerSource mentions, ProjectManager projects,
        UserStore users, IEnumerable<ITriggerSource> sources, IConfiguration config,
        ILogger<PersonaAutomationService> log)
    {
        _personas = personas; _sessions = sessions; _push = push; _hub = hub; _notif = notif;
        _state = state; _mentions = mentions; _projects = projects; _users = users;
        _config = config; _log = log;
        _sources = sources.ToDictionary(s => s.Type);
        _sessions.OnUserMessage += OnUserMessageAsync;
        _sessions.OnSessionMessage += OnSessionMessageAsync;
        _sessions.OnSessionDeleted += OnSessionDeleted;
    }

    public void Dispose()
    {
        _sessions.OnUserMessage -= OnUserMessageAsync;
        _sessions.OnSessionMessage -= OnSessionMessageAsync;
        _sessions.OnSessionDeleted -= OnSessionDeleted;
    }

    // ─── Тик (collaborator из TaskSchedulerService) ─────────────────────────────

    public async Task MaybeRunAutomationsAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct)
    {
        foreach (var persona in _personas.GetByOwner(user.Id))
        {
            // Снимаем снапшот списка правил: UpdateRules заменяет ссылку, итерация по старой ссылке безопасна.
            var rules = persona.AutomationRules;
            if (rules is null or { Count: 0 }) continue;
            foreach (var rule in rules)
            {
                if (!rule.Enabled) continue;
                if (rule.Trigger.Type == AutomationTriggerType.Mention) continue;   // push-источник, не из тика
                if (!_sources.TryGetValue(rule.Trigger.Type, out var source)) continue;

                var state = _state.GetRule(persona.Id, rule.Id);

                // Pre-throttle ДО опроса источника — единый для всех poll-типов. Снапшот-источники
                // (File/Note/GitCommit/TaskStatus) продвигают снапшот внутри EvaluateAsync, поэтому
                // отбрасывать событие ПОСЛЕ детекции нельзя — оно уничтожается безвозвратно.
                // Кулдаун, тихие часы и потолок в час откладывают саму детекцию: снапшот не тронут,
                // изменения обнаружатся первым разрешённым тиком. Для File это же экономит дорогой
                // обход дерева проекта: скан идёт не чаще кулдауна, а изменения за время ожидания
                // копятся в самом дереве и выходят одним сводным батч-событием.
                if (InQuietWindow(rule, TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz))) continue;
                var minInterval = EffectiveMinIntervalMinutes(rule);
                if (state.LastFiredAt is { } lastFired && nowUtc - lastFired < TimeSpan.FromMinutes(minInterval))
                    continue;
                if (!_state.HasHourlyBudget(persona.Id, HourlyCap, nowUtc)) continue;

                var ctx = new TriggerContext(user, persona, rule, tz, nowUtc, state);
                IReadOnlyList<TriggerEvent> events;
                try { events = await source.EvaluateAsync(ctx, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Ошибка источника {Type} правила {Rule}", rule.Trigger.Type, rule.Id);
                    continue;
                }
                if (events.Count == 0) continue;
                _state.Save();  // персистим продвинутый снапшот (чтобы не переобнаруживать то же)

                foreach (var ev in events)
                    _ = FireAsync(persona, rule, tz, ev, CancellationToken.None);
            }
        }
    }

    // ─── @упоминание (push-канал) ───────────────────────────────────────────────

    private async Task OnUserMessageAsync(Session session, string text, string? senderPersonaId)
    {
        if (senderPersonaId is not null) return;   // только текст реального пользователя
        var ownerId = ResolveOwner(session);
        if (ownerId is null) return;
        try
        {
            var events = await _mentions.DetectAsync(ownerId, session, text, CancellationToken.None);
            var tz = ResolveTz(ownerId);
            foreach (var ev in events)
            {
                var (persona, rule) = FindRule(ownerId, ev.RuleId);
                if (persona is null || rule is null) continue;
                _ = FireAsync(persona, rule, tz, ev, CancellationToken.None);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Mention-детекция"); }
    }

    // ─── Executor: постановка-промпт → ход ──────────────────────────────────────

    // Реакция персоны на событие: собираем большой промпт с контекстом срабатывания
    // (по образцу TaskExecutionService — ## секции + объекты-триггеры; для файлов — пути и
    // содержимое) и запускаем им ход в закреплённом чате правила. Характер персоны идёт
    // системным слоем сессии. bypassThrottle=true для ручного теста (/test эндпоинт).
    private async Task FireAsync(Persona persona, PersonaAutomationRule rule, TimeZoneInfo tz,
        TriggerEvent ev, CancellationToken ct, bool bypassThrottle = false)
    {
        var state = _state.GetRule(persona.Id, rule.Id);
        var now = DateTime.UtcNow;

        // 0-1. Троттлинг (fail-fast до LLM) + mark-fired ДО запуска. Проверка и установка
        // LastFiredAt — атомарно под локом state: Mention (push) и тик конкурируют за одно
        // правило, без лока два потока проходили throttle-проверку и дублировали реакцию.
        if (!bypassThrottle)
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
            if (InQuietWindow(rule, localNow)) { MarkResult(state, "quiet"); return; }
            var minInterval = EffectiveMinIntervalMinutes(rule);
            lock (state)
            {
                if (state.LastFiredAt is { } last && now - last < TimeSpan.FromMinutes(minInterval))
                { MarkResult(state, "throttled"); return; }
                if (!_state.TryConsumeHourly(persona.Id, HourlyCap, now))
                { MarkResult(state, "throttled"); return; }
                state.LastFiredAt = now;
                state.RunCount++;
            }
        }
        else
        {
            state.LastFiredAt = now;
            state.RunCount++;
        }
        _state.Save();

        // Перечитать персону/правило — могли удалить/выключить между тиком и запуском
        var freshPersona = _personas.Get(persona.Id, persona.OwnerId);
        if (freshPersona is null) return;
        var freshRule = freshPersona.AutomationRules?.FirstOrDefault(r => r.Id == rule.Id);
        if (freshRule is null || !freshRule.Enabled) return;

        // 2. Большой промпт с контекстом срабатывания (как постановка задачи)
        string prompt;
        try { prompt = await BuildAutomationPromptAsync(freshRule, ev); }
        catch (Exception ex) { _log.LogWarning(ex, "build-prompt правило {Rule}", rule.Id); MarkResult(state, "error"); return; }

        // 3. Закреплённый чат правила (acceptEdits — персона может править файлы/заводить задачи)
        string targetSessionId;
        try { targetSessionId = await EnsureRuleChatAsync(freshPersona, freshRule, state); }
        catch (Exception ex) { _log.LogWarning(ex, "создание чата правила {Rule}", rule.Id); MarkResult(state, "error"); return; }

        // 4. Промпт → ход от лица персоны (контекст срабатывания — внутри сообщения чата)
        try { await _sessions.SendMessageAsync(targetSessionId, prompt, [], auto: true, senderPersonaId: freshPersona.Id); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "send правило {Rule}", rule.Id);
            // Чат мог быть удалён/сломан — сбросим ссылку, чтобы следующий ход создал свежий
            if (state.SessionId == targetSessionId) { state.SessionId = null; _state.Save(); }
            MarkResult(state, "error");
            return;
        }

        MarkResult(state, "fired");
    }

    // Создать/переиспользовать закреплённый чат правила (один на правило, брендирован персоной).
    private async Task<string> EnsureRuleChatAsync(Persona persona, PersonaAutomationRule rule, RuleRuntimeState state)
    {
        if (state.SessionId is { } sid && _sessions.GetById(sid) is { } s && s.PersonaId == persona.Id)
            return sid;
        var title = string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : persona.Role;
        var chat = await _sessions.CreatePersonaChatAsync(persona.OwnerId, persona.Id, ClaudeMode.AcceptEdits,
            name: $"{title}: {rule.Name}", automationRuleId: rule.Id);
        // TTL чата правила — только при создании; null у Action.ExpiresAfterMinutes — бессрочно
        if (rule.Action.ExpiresAfterMinutes is { } ttl) _sessions.SetExpiry(chat.Id, ttl);
        state.SessionId = chat.Id;
        _state.Save();
        return chat.Id;
    }

    // Ручной прогон правила (UX «Проверить» в UI): синтетическое событие, байпас троттлинга.
    public async Task TestAsync(string ownerId, string personaId, string ruleId)
    {
        var (persona, rule) = FindRule(ownerId, ruleId);
        if (persona is null || rule is null || persona.Id != personaId) return;
        var synthetic = new TriggerEvent(rule.Id, rule.Trigger.Type,
            $"Проверка правила «{rule.Name}» (ручной запуск)", null);
        await FireAsync(persona, rule, ResolveTz(ownerId), synthetic, CancellationToken.None, bypassThrottle: true);
    }

    // ─── Уведомления по событиям хода ────────────────────────────────────────────

    // Все типы сообщений, требующие внимания пользователя: ход завершён, задан вопрос,
    // запрошено разрешение на инструмент, представлен план. Уведомление шлём ТОЛЬКО если
    // пользователь не смотрит этот чат (нет активных SignalR-подписок в группе сессии).
    private async Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not (ResultMessage or AskQuestionMessage or PermissionRequestMessage or PlanReviewMessage)) return;
        if (string.IsNullOrEmpty(session.AutomationRuleId)) return;
        var ownerId = ResolveOwner(session);
        if (ownerId is null) return;
        var (persona, _) = FindRule(ownerId, session.AutomationRuleId);
        var label = persona is null
            ? "Персона"
            : (string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : $"{persona.Role} ({persona.Name})");

        // Если пользователь смотрит этот чат — не шлём уведомление (он видит в реальном времени)
        if (_sessions.HasViewers(session.Id)) return;

        try
        {
            string title, body;
            if (msg is ResultMessage)
            {
                title = $"{label} написала вам";
                body = "Новое сообщение по правилу автоматизации";
            }
            else if (msg is AskQuestionMessage)
            {
                title = $"{label} ждёт ответа на вопрос";
                body = "Персона спрашивает — ответьте, чтобы продолжить";
            }
            else if (msg is PermissionRequestMessage)
            {
                title = $"{label} запрашивает разрешение";
                body = "Персона хочет выполнить действие — разрешите или отклоните";
            }
            else // PlanReviewMessage
            {
                title = $"{label} представила план";
                body = "Персона предлагает план — согласуйте его";
            }

	            var chatUrl = string.IsNullOrEmpty(session.ProjectId)
                ? $"/chats/{session.Id}"
                : $"/project/{session.ProjectId}/chat/{session.Id}";

            await _notif.SendNotificationMessageAsync(ownerId, new NotificationMessage(
                Title: title, Body: body,
                Url: chatUrl,
                Kind: "claude", Tag: "Автоматизация"), sendPush: true);
        }
        catch { /* уведомление — best-effort */ }
    }

    // Чат правила удалили (вручную или авто-удалением временного) — сбросим ссылку state.SessionId,
    // чтобы следующий ход создал свежий чат, а не пытался переиспользовать удалённый.
    private void OnSessionDeleted(Session session)
    {
        if (session.AutomationRuleId is string ruleId && session.PersonaId is string personaId)
        {
            var st = _state.GetRule(personaId, ruleId);
            if (st.SessionId == session.Id)
            {
                st.SessionId = null;
                _state.Save();
            }
        }
    }

    // ─── Постановка-промпт (по образцу TaskExecutionService.BuildPrompt) ────────

    // Большой промпт для хода персоны: ## секции + объекты, вызвавшие срабатывание.
    // Характер персоны инжектится системным слоем сессии (как у персоны-исполнителя задач).
    internal async Task<string> BuildAutomationPromptAsync(PersonaAutomationRule rule, TriggerEvent ev)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## СОБЫТИЕ");
        sb.AppendLine($"Сработало правило автоматизации «{rule.Name}» ({TriggerLabel(rule)}).");
        sb.AppendLine(ev.Summary);
        if (rule.Condition?.OnlyIf is { } only && !string.IsNullOrWhiteSpace(only))
        {
            sb.AppendLine();
            sb.AppendLine($"Доп. условие реакции: {only}");
        }

        sb.AppendLine();
        sb.AppendLine("## КОНТЕКСТ (что вызвало срабатывание)");
        sb.AppendLine(await BuildContextBlockAsync(rule, ev));

        sb.AppendLine("## ИНСТРУКЦИЯ");
        var instruction = rule.Action.Instruction?.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
            instruction = rule.Action.Weight == AutomationActionWeight.Work
                ? "Действуй: разберись с событием и сделай необходимое от моего имени — можно править файлы, заводить задачи/заметки через инструменты. Итог — сообщением в этот чат."
                : "Оцени событие и коротко ответь пользователю от своего лица в этом чате. Тяжёлых действий не предпринимай.";
        sb.AppendLine(instruction);

        sb.AppendLine();
        sb.AppendLine("## ОЖИДАЕМЫЙ РЕЗУЛЬТАТ");
        sb.AppendLine("- Ответь пользователю в этом чате от своего лица (он, возможно, не у экрана — сообщение самодостаточно).");
        sb.AppendLine("- Если событие не стоит твоей реакции — кратко скажи об этом и не делай лишнего.");
        sb.AppendLine("- Действуй конкретно по переданному контексту, не сканируй всё подряд.");

        sb.AppendLine();
        sb.AppendLine("## ПРАВИЛА");
        sb.AppendLine("- Не выдумывай работу сверх события и инструкции.");
        sb.AppendLine("- Соблюдай свой характер и зону контекста.");
        sb.AppendLine("- Если нужно уточнить что-то у пользователя — используй инструмент AskUserQuestion с вариантами ответов (не пиши вопросы текстом). Он покажет ему кнопки для выбора.");
        return sb.ToString();
    }

    private static string TriggerLabel(PersonaAutomationRule rule) => rule.Trigger.Type switch
    {
        AutomationTriggerType.Timer => "таймер",
        AutomationTriggerType.File => "изменение файлов",
        AutomationTriggerType.Note => "заметки",
        AutomationTriggerType.GitCommit => "новые коммиты",
        AutomationTriggerType.TaskStatus => "смена статуса задачи",
        AutomationTriggerType.Mention => "@упоминание",
        _ => rule.Trigger.Type.ToString(),
    };

    // Блок объектов срабатывания. Для файлов — пути + содержимое (top-N, с ограничением);
    // для остальных — детали события из источника.
    private async Task<string> BuildContextBlockAsync(PersonaAutomationRule rule, TriggerEvent ev)
    {
        if (rule.Trigger.Type == AutomationTriggerType.File)
            return await BuildFileContextAsync(rule, ev);

        var sb = new StringBuilder();
        if (ev.Details is { Count: > 0 })
        {
            foreach (var kv in ev.Details)
                sb.AppendLine($"- {kv.Key}: {Truncate(kv.Value, 800)}");
        }
        else
        {
            sb.AppendLine("Дополнительного контекста нет.");
        }
        return sb.ToString();
    }

    private async Task<string> BuildFileContextAsync(PersonaAutomationRule rule, TriggerEvent ev)
    {
        var sb = new StringBuilder();
        var projectId = TriggerArgs.Of(rule.Trigger).GetString("projectId");
        var project = projectId is null ? null : _projects.GetById(projectId);
        if (project is not null)
            sb.AppendLine($"Проект «{project.Name}» (корень: {project.RootPath}).");

        var paths = new List<string>();
        if (ev.Details?.TryGetValue("created", out var created) == true)
            paths.AddRange(created.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (ev.Details?.TryGetValue("changed", out var changed) == true)
            paths.AddRange(changed.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        // Сентинел усечения из источника («…и ещё N») — не путь, в список файлов не берём
        paths.RemoveAll(p => p.StartsWith("…и ещё", StringComparison.Ordinal));
        if (paths.Count == 0) { sb.AppendLine("Файлы не переданы."); return sb.ToString(); }

        sb.AppendLine(paths.Count == 1 ? "Файл:" : $"Файлы ({paths.Count}):");
        var shown = 0;
        foreach (var rel in paths)
        {
            if (shown >= 6) { sb.AppendLine($"- …и ещё {paths.Count - shown}"); break; }
            sb.AppendLine($"- {rel}");
            if (project is not null && await ReadSnippetAsync(project.RootPath, rel) is { } snippet)
            {
                sb.AppendLine("```");
                sb.AppendLine(Truncate(snippet, 1500));
                sb.AppendLine("```");
            }
            shown++;
        }
        sb.AppendLine("(целиком или diff — прочитай файловыми инструментами проекта)");
        return sb.ToString();
    }

    // Прочитать содержимое файла-фрагмента, если он текстовый и небольшой. null — пропустить
    // (бинарник, большой, удалён, вне корня проекта — защита от path traversal).
    private static async Task<string?> ReadSnippetAsync(string root, string rel)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootFull, rel.TrimStart('/', '\\')));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(full)) return null;
            if (new FileInfo(full).Length > 50_000) return null;
            var text = await File.ReadAllTextAsync(full);
            if (text.Contains('\0')) return null;   // бинарный
            return text;
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    internal static bool InQuietWindow(PersonaAutomationRule rule, DateTime localNow)
    {
        var c = rule.Condition;
        if (c is null || string.IsNullOrWhiteSpace(c.QuietFrom) || string.IsNullOrWhiteSpace(c.QuietTo)) return false;
        if (!TimeOnly.TryParseExact(c.QuietFrom, "HH:mm", out var from)) return false;
        if (!TimeOnly.TryParseExact(c.QuietTo, "HH:mm", out var to)) return false;
        var now = new TimeOnly(localNow.TimeOfDay.Ticks);
        return from <= to ? (now >= from && now < to)   // обычный диапазон
                          : (now >= from || now < to);   // переход через полночь
    }

    // ─── Хелперы ─────────────────────────────────────────────────────────────────

    private void MarkResult(RuleRuntimeState state, string result)
    {
        state.LastResult = result;
        state.LastResultAt = DateTime.UtcNow;
        _state.Save();
    }

    private (Persona? Persona, PersonaAutomationRule? Rule) FindRule(string ownerId, string ruleId)
    {
        foreach (var p in _personas.GetByOwner(ownerId))
            if (p.AutomationRules?.FirstOrDefault(r => r.Id == ruleId) is { } r) return (p, r);
        return (null, null);
    }

    private string? ResolveOwner(Session s)
    {
        if (!string.IsNullOrEmpty(s.OwnerId)) return s.OwnerId;
        if (!string.IsNullOrEmpty(s.ProjectId) && _projects.GetById(s.ProjectId) is { } pr && pr.OwnerId is { } oid)
            return oid;
        return null;
    }

    private TimeZoneInfo ResolveTz(string? ownerId)
    {
        var tzId = ownerId is null ? null : _users.GetById(ownerId)?.TimeZone;
        return TaskDueCalculator.ResolveTimeZone(tzId);
    }
}
