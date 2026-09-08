namespace ClaudeHomeServer.Services.Knowledge;

// Участник синка знаний: каждая вертикаль со своим стором «id записи → {DocId, Hash}».
// Регистрируется как forwarder в DI (`Program.cs:661-670`); каскад UserKnowledgeCascade
// получает участников через IEnumerable<IKnowledgeSyncParticipant>, а не по конкретным
// типам — иначе разрез цикла Knowledge→Notes/Memory/Dossiers не сложить.
//
// DeleteAllAsync(userId) — каскадное удаление знаний владельца: локальные сторы,
// подписки на дебаунс-синк. Dify-датасеты удаляет сам UserKnowledgeCascade общим
// проходом по префиксу имени; участник НЕ трогает Dify, чтобы не дублировать
// каскад и не ловить отказы Dify дважды.
public interface IKnowledgeSyncParticipant
{
    // Снимок текущих целей участника (датасеты с записанным DatasetId)
    IReadOnlyList<KnowledgeSyncTarget> ListTargets();

    // Уборка локальных сторов и подписок участника для удалённого пользователя.
    // Dify на этом шаге НЕ чистится — это делает UserKnowledgeCascade общим проходом.
    Task DeleteAllAsync(string userId);
}
