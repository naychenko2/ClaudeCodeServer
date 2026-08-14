namespace ClaudeHomeServer.Services.Knowledge;

// Контракт участника синка знаний для реконсайлера error-документов Dify.
// Каждый владелец локального стора «id записи → {DocId, Hash}» отдаёт свои цели
// (датасеты) и три операции над ними. Чтение и запись разделены намеренно:
// без этого невозможны ни режим наблюдения (observe), ни карантин «ядовитых»
// записей (карантинные ключи отбрасываются ПОСЛЕ ResolveAsync, ДО InvalidateAsync).
//
// - ResolveAsync(docIds) — чистое чтение: сопоставить DocId документов Dify со
//   стабильными ключами записей владельца (id записи памяти / noteId / относительный
//   путь файла). Неизвестные DocId (сироты) в ответ не попадают.
// - InvalidateAsync(entryKeys) — сброс Hash="" по уже отобранным ключам + Save.
//   Хеш сбрасывается ЗАМЕНОЙ объекта DocRef, а не правкой поля: снапшоты активного
//   дифф-синка — поверхностные копии, объекты общие со стором.
// - KickSync() — планирование штатного дебаунс-синка, без ожидания завершения.
//   Нельзя звать под _syncLock владельца — дедлок с его же SyncAsync; порядок
//   строго ResolveAsync → InvalidateAsync (локи отпущены) → KickSync.
public sealed record KnowledgeSyncTarget(
    string DatasetId,
    IReadOnlyList<string> OwnerUserIds,
    string Label,
    Func<IReadOnlyCollection<string>, Task<IReadOnlyList<(string DocId, string EntryKey)>>> ResolveAsync,
    Func<IReadOnlyCollection<string>, Task> InvalidateAsync,
    Action KickSync);

public interface IKnowledgeSyncParticipant
{
    // Снимок текущих целей участника (датасеты с записанным DatasetId)
    IReadOnlyList<KnowledgeSyncTarget> ListTargets();
}
