using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.ChatContext;

// Состояние стора → DTO для фронта. Подписи считает ОДНА функция провайдера — Describe, поэтому чип,
// строка «Работаем с» и хвост хода не расходятся. UsedBy референса — операции основного объекта,
// чья таблица AcceptedRefs его принимает (вид и роль совпали); без основного или без совпадения — пусто.
public static class ChatContextDtoBuilder
{
    public static ChatContextDto Build(ContextKindRegistry registry, ContextScope scope, ChatContextState state)
    {
        var accepted = state.Primary is { } primary && registry.Find(primary.Kind) is { } owner
            ? owner.AcceptedRefs(scope, primary, null)
            : [];

        ContextItemSummary Describe(ContextItem item) =>
            registry.Find(item.Kind)?.Describe(scope, item)
            ?? new ContextItemSummary(item.Kind, null, null, true);

        ChatContextItemDto? primaryDto = null;
        if (state.Primary is { } p)
        {
            var s = Describe(p);
            primaryDto = new ChatContextItemDto(p.Id, p.Kind, p.Ref, null, p.By, p.AddedAt, s.Label, s.Version, s.Thumb, s.Missing);
        }

        var refs = state.Refs.Select(r =>
        {
            var s = Describe(r);
            var usedBy = accepted
                .Where(a => a.Role == r.Role && a.Kinds.Contains(r.Kind))
                .SelectMany(a => a.Ops)
                .Distinct()
                .ToList();
            return new ChatContextRefDto(r.Id, r.Kind, r.Ref, r.Role, r.By, r.AddedAt,
                s.Label, s.Version, s.Thumb, s.Missing, usedBy);
        }).ToList();

        return new ChatContextDto(state.Revision, primaryDto, refs);
    }
}
