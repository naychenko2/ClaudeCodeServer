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
    Action KickSync)
{
    // Общее ядро ResolveAsync участников: какие ключи карты «ключ записи → документ»
    // ссылаются на переданные DocId. Не ToDictionary по DocId: два ключа с одним DocId
    // встречаются на проде (досье ClaudeCodeServer, 2026-10), и словарь падал
    // ArgumentException на каждом тике — реконсайлер глотал его как сбой цели, и цель не
    // лечилась никогда. Отдаём ВСЕ ключи документа: сброс хеша пересоздаст каждую запись.
    public static IReadOnlyList<(string DocId, string EntryKey)> MatchDocIds<TDoc>(
        IEnumerable<KeyValuePair<string, TDoc>> docs, Func<TDoc, string> docIdOf,
        IReadOnlyCollection<string> docIds)
    {
        var wanted = docIds.ToHashSet(StringComparer.Ordinal);
        return docs
            .Select(kv => (DocId: docIdOf(kv.Value), EntryKey: kv.Key))
            .Where(p => wanted.Contains(p.DocId))
            .ToList();
    }
}
