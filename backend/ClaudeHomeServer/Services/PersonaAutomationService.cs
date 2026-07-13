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
// (подписка на SessionManager.OnUserMessage). Действие по умолчанию — one-shot гейт «стоит ли
// реагировать?» через PersonaAskService, затем сообщение в закреплённый чат правила; полный ход —
// при Action.Weight==Work. Троттлинг: тихие часы + MinInterval per-rule + потолок N/час per-persona.
public sealed class PersonaAutomationService : IDisposable
{
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly PersonaAskService _ask;
    private readonly PushService _push;
    private readonly IHubContext<SessionHub> _hub;
    private readonly AutomationStateStore _state;
    private readonly MentionTriggerSource _mentions;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly IReadOnlyDictionary<AutomationTriggerType, ITriggerSource> _sources;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaAutomationService> _log;

    // sessionId → ruleId: ходы, запущенные движком. По ResultMessage шлём «персона написала вам».
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _inflight = new();

    private int DefaultMinIntervalMinutes => _config.GetValue("Persona:AutomationMinIntervalMinutes", 5);
    private int HourlyCap => _config.GetValue("Persona:AutomationHourlyCap", 8);

    public PersonaAutomationService(PersonaManager personas, SessionManager sessions,
        PersonaAskService ask, PushService push, IHubContext<SessionHub> hub,
        AutomationStateStore state, MentionTriggerSource mentions, ProjectManager projects,
        UserStore users, IEnumerable<ITriggerSource> sources, IConfiguration config,
        ILogger<PersonaAutomationService> log)
    {
        _personas = personas; _sessions = sessions; _ask = ask; _push = push; _hub = hub;
        _state = state; _mentions = mentions; _projects = projects; _users = users;
        _config = config; _log = log;
        _sources = sources.ToDictionary(s => s.Type);
        _sessions.OnUserMessage += OnUserMessageAsync;
        _sessions.OnSessionMessage += OnSessionMessageAsync;
    }

    public void Dispose()
    {
        _sessions.OnUserMessage -= OnUserMessageAsync;
        _sessions.OnSessionMessage -= OnSessionMessageAsync;
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
                var ctx = new TriggerContext(user, persona, rule, tz, nowUtc, state);
                IReadOnlyList<TriggerEvent> events;
                try { events = await source.EvaluateAsync(ctx, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Ошибка источника {Type} правила {Rule}", rule.Trigger.Type, rule.Id);
                    continue;
                }
                if (events.Count > 0) _state.Save();   // персистим обновлённые источником снапшоты

                foreach (var ev in events)
                    _ = FireAsync(persona, rule, tz, ev, CancellationToken.None);  // тяжёлая работа в фоне, не блокирует тик
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

    // ─── Executor: gate → send → escalate ───────────────────────────────────────

    // Реакция персоны на событие. bypassThrottle=true для ручного теста (/test эндпоинт).
    private async Task FireAsync(Persona persona, PersonaAutomationRule rule, TimeZoneInfo tz,
        TriggerEvent ev, CancellationToken ct, bool bypassThrottle = false)
    {
        var state = _state.GetRule(persona.Id, rule.Id);
        var now = DateTime.UtcNow;

        // 0. Троттлинг (fail-fast до LLM)
        if (!bypassThrottle)
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
            if (InQuietWindow(rule, localNow)) { MarkResult(state, "quiet"); return; }
            var minInterval = rule.Condition?.MinIntervalMinutes ?? DefaultMinIntervalMinutes;
            if (state.LastFiredAt is { } last && now - last < TimeSpan.FromMinutes(minInterval))
            { MarkResult(state, "throttled"); return; }
            if (!_state.TryConsumeHourly(persona.Id, HourlyCap, now))
            { MarkResult(state, "throttled"); return; }
        }

        // 1. Mark-fired ДО запуска (идемпотентность, как у удалённой PersonaProactiveService)
        state.LastFiredAt = now;
        state.RunCount++;
        _state.Save();

        // Перечитать персону/правило — могли удалить/выключить между тиком и запуском
        var freshPersona = _personas.Get(persona.Id, persona.OwnerId);
        if (freshPersona is null) return;
        var freshRule = freshPersona.AutomationRules?.FirstOrDefault(r => r.Id == rule.Id);
        if (freshRule is null || !freshRule.Enabled) return;

        // 2. One-shot гейт: «стоит ли реагировать + что сказать?»
        string gateAnswer;
        try { gateAnswer = await _ask.AskAsync(freshPersona.OwnerId, freshPersona, BuildGatePrompt(freshRule, ev), ev.Summary, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "gate правило {Rule}", rule.Id); MarkResult(state, "error"); return; }

        if (!ParseGateYes(gateAnswer)) { MarkResult(state, "no"); return; }
        var message = ExtractGateMessage(gateAnswer);
        if (string.IsNullOrWhiteSpace(message)) message = ev.Summary;

        // 3. Целевой чат — закреплённый чат правила (Phase 1 единообразно; OriginSessionId Mention
        //    попадает в контекст gate-промпта через Summary). Same-chat-ответ Mention — Phase 2.
        string targetSessionId;
        try { targetSessionId = await EnsureRuleChatAsync(freshPersona, freshRule, state); }
        catch (Exception ex) { _log.LogWarning(ex, "создание чата правила {Rule}", rule.Id); MarkResult(state, "error"); return; }

        // 4. gate-ответ → сообщение в чат (ход от лица персоны)
        _inflight[targetSessionId] = freshRule.Id;
        try { await _sessions.SendMessageAsync(targetSessionId, message, [], auto: true, senderPersonaId: freshPersona.Id); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "send правило {Rule}", rule.Id);
            _inflight.TryRemove(targetSessionId, out _);
            MarkResult(state, "error");
            return;
        }

        // 5. Эскалация в полный агентский ход (правка файлов/задач через MCP) — по явному Weight==Work
        if (freshRule.Action.Weight == AutomationActionWeight.Work)
        {
            var workDirective = string.IsNullOrWhiteSpace(freshRule.Action.Instruction)
                ? "Разберись с этим событием и сделай необходимое от моего имени."
                : freshRule.Action.Instruction.Trim();
            try { await _sessions.SendMessageAsync(targetSessionId, workDirective, [], auto: true, senderPersonaId: freshPersona.Id); }
            catch (Exception ex) { _log.LogWarning(ex, "work-эскалация правило {Rule}", rule.Id); }
        }

        MarkResult(state, "yes");
    }

    // Создать/переиспользовать закреплённый чат правила (один на правило, брендирован персоной).
    private async Task<string> EnsureRuleChatAsync(Persona persona, PersonaAutomationRule rule, RuleRuntimeState state)
    {
        if (state.SessionId is { } sid && _sessions.GetById(sid) is { } s && s.PersonaId == persona.Id)
            return sid;
        var title = string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : persona.Role;
        var chat = await _sessions.CreatePersonaChatAsync(persona.OwnerId, persona.Id, ClaudeMode.Auto,
            name: $"{title}: {rule.Name}", automationRuleId: rule.Id);
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

    // ─── Уведомление по завершении хода ─────────────────────────────────────────

    private async Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage) return;
        if (!_inflight.TryRemove(session.Id, out var ruleId)) return;
        var ownerId = ResolveOwner(session);
        if (ownerId is null) return;
        var (persona, _) = FindRule(ownerId, ruleId);
        var label = persona is null
            ? "Персона"
            : (string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : $"{persona.Role} ({persona.Name})");
        var n = new NotificationMessage(
            Title: $"{label} написала вам",
            Body: "Новое сообщение по правилу автоматизации — откройте чат",
            Url: $"/#/chats/{session.Id}",
            Kind: "claude");
        try
        {
            await _hub.Clients.Group("user_" + ownerId).SendAsync("message", n);
            await _push.SendToUserAsync(ownerId, n);
        }
        catch { /* уведомление — best-effort */ }
    }

    // ─── Чистые предикаты (юнит-тесты) ──────────────────────────────────────────

    internal static string BuildGatePrompt(PersonaAutomationRule rule, TriggerEvent ev)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сработало правило автоматизации «{rule.Name}».");
        sb.AppendLine($"Событие: {ev.Summary}");
        if (rule.Condition?.OnlyIf is { } only && !string.IsNullOrWhiteSpace(only))
            sb.AppendLine($"Доп. условие: {only}");
        if (ev.Details is { Count: > 0 } d)
        {
            sb.AppendLine("Детали:");
            foreach (var kv in d)
            {
                var val = kv.Value.Length > 500 ? kv.Value[..497] + "…" : kv.Value;
                sb.AppendLine($"- {kv.Key}: {val}");
            }
        }
        if (!string.IsNullOrWhiteSpace(rule.Action.Instruction))
            sb.AppendLine($"\nТвоя инструкция к действию: {rule.Action.Instruction.Trim()}");
        sb.AppendLine();
        sb.AppendLine("Реши, стоит ли тебе реагировать на это событие прямо сейчас. Если НЕ стоит — " +
                      "ответь ровно одним словом NO. Если стоит — первой строкой YES, затем короткое " +
                      "сообщение пользователю от своего лица (он увидит его в чате и, возможно, не у " +
                      "экрана — сообщение должно быть самодостаточным).");
        return sb.ToString();
    }

    internal static bool ParseGateYes(string? answer)
    {
        var first = (answer ?? "").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractGateMessage(string answer)
    {
        var idx = answer.IndexOf('\n');
        return idx < 0 ? "" : answer[(idx + 1)..].Trim();
    }

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
