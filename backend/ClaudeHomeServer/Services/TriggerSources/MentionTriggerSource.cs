using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// @упоминание-триггер (push, не poll): детектит @handle персоны в тексте пользователя.
// Вызывается сервисом из подписки на SessionManager.OnUserMessage (не из тика). Guard:
// не активный спикер сессии и не участник группового чата — иначе это зона RouteGroupSpeakerAsync
// (там @упоминание участника переключает спикера в одном чате). Реакция пойдёт в тот же чат
// (OriginSessionId), если у упомянутой персоны есть включённое правило Mention.
//
// Не реализует ITriggerSource.EvaluateAsync — у него другая сигнатура (push). Сервис вызывает DetectAsync.
public sealed class MentionTriggerSource(PersonaManager personas)
{
    public AutomationTriggerType Type => AutomationTriggerType.Mention;

    public Task<IReadOnlyList<TriggerEvent>> DetectAsync(string ownerId, Session session, string text, CancellationToken ct)
    {
        var matches = GroupChatRouter.MentionPattern.Matches(text);
        if (matches.Count == 0) return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var participants = session.Participants is { Count: > 0 } p ? new HashSet<string>(p) : null;
        var events = new List<TriggerEvent>();
        var seenHandle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var handle = m.Groups[1].Value;
            if (!seenHandle.Add(handle)) continue;
            var persona = personas.GetByHandle(ownerId, handle);
            if (persona is null) continue;
            if (session.PersonaId == persona.Id) continue;             // уже активный спикер
            if (participants?.Contains(persona.Id) == true) continue;  // участник группы — его роутит GroupChatRouter

            foreach (var rule in persona.AutomationRules ?? [])
            {
                if (!rule.Enabled || rule.Trigger.Type != AutomationTriggerType.Mention) continue;
                var snippet = text.Length > 160 ? text[..157] + "…" : text;
                var where = string.IsNullOrWhiteSpace(session.Name) ? "" : $" «{session.Name}»";
                events.Add(new TriggerEvent(rule.Id, AutomationTriggerType.Mention,
                    $"Вас (@{handle}) упомянули в чате{where}",
                    new Dictionary<string, string> { ["handle"] = handle, ["message"] = snippet },
                    OriginSessionId: session.Id));
            }
        }
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(events);
    }
}
