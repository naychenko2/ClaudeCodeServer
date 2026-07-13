using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Чистый роутер группового чата: по @упоминаниям в тексте пользователя определяет,
// какая персона-участник отвечает на этот ход. Без побочных эффектов — легко тестируется.
public static class GroupChatRouter
{
    // SpeakerPersonaId — кто отвечает; Switched — спикер сменился относительно текущего;
    // AlsoMentioned — остальные упомянутые участники (спикеру стоит спросить их через persona_ask).
    public sealed record RouteResult(string SpeakerPersonaId, bool Switched, IReadOnlyList<string> AlsoMentioned);

    // @handle по границе слова: не срабатывает внутри email (a@b) и на «слипшихся» токенах.
    // internal — переиспользуется источником триггеров MentionTriggerSource.
    internal static readonly Regex MentionPattern =
        new(@"(?<![\p{L}\p{N}_@-])@([\p{L}\p{N}_-]+)", RegexOptions.Compiled);

    // Первый @handle участника в тексте (без учёта регистра) → спикер; остальные упомянутые
    // участники → AlsoMentioned. Упоминания не-участников игнорируются. Без упоминаний —
    // текущий активный; если текущий выбыл из участников — ведущая (первая в списке).
    public static RouteResult Resolve(string text, IReadOnlyList<Persona> participants, string? currentPersonaId)
    {
        if (participants.Count == 0)
            throw new ArgumentException("Список участников пуст", nameof(participants));

        var mentioned = new List<Persona>();
        foreach (Match m in MentionPattern.Matches(text ?? ""))
        {
            var handle = m.Groups[1].Value;
            var p = participants.FirstOrDefault(x =>
                string.Equals(x.Handle, handle, StringComparison.OrdinalIgnoreCase));
            if (p is not null && !mentioned.Contains(p)) mentioned.Add(p);
        }

        var current = participants.FirstOrDefault(p => p.Id == currentPersonaId);
        var speaker = mentioned.FirstOrDefault() ?? current ?? participants[0];
        var also = mentioned.Skip(1).Select(p => p.Id).ToList();
        return new RouteResult(speaker.Id, speaker.Id != currentPersonaId, also);
    }
}
