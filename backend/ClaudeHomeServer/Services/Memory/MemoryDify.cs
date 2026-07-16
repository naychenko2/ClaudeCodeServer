using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Services.Memory;

// Ссылка на документ Dify для записи памяти: id документа + хеш проиндексированного содержимого
// (по нему дифф-синк решает, нужно ли переиндексировать). Общий тип для памяти персон и команд;
// сериализуется в стор так же, как прежние приватные DocRef (те же имена свойств).
public sealed class MemoryDocRef
{
    public string DocId { get; set; } = "";
    public string Hash { get; set; } = "";
}

// Одна запись памяти, приведённая к виду для синхронизации с Dify: идентификатор, строка-источник
// хеша, имя документа, текст и метки. Facade собирает список из своих записей (TypeLabel/формат хеша —
// специфика стека), ядро лишь диффит его с уже проиндексированными документами.
public readonly record struct MemorySyncItem(
    string Id, string HashSource, string DocName, string Text, List<string>? Tags);

// Общее ядро синхронизации памяти с Dify: хеш содержимого, дебаунс-планировщик и дифф-синк-петля.
// Специфика (имя датасета, снапшоты под своим локом, один файл-стор у персоны vs два у команды)
// остаётся тонкой связкой в самом сервисе.
public static class MemoryDify
{
    // SHA-256 hex от строки-источника (Type/Text/Tags) — ключ инвалидации документа Dify
    public static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // Дифф-синхронизация: для каждой записи, чей хеш изменился/отсутствует, удаляем старый документ
    // и индексируем новый; документы исчезнувших записей удаляем. Мутации docs идут через setDoc/
    // removeDoc (facade держит их под своим локом). Возвращает число затронутых документов.
    public static async Task<int> DiffSyncAsync(
        KnowledgeService knowledge, string datasetId,
        IReadOnlyList<MemorySyncItem> items,
        IReadOnlyDictionary<string, MemoryDocRef> docsSnapshot,
        Action<string, MemoryDocRef> setDoc, Action<string> removeDoc,
        ILogger? log)
    {
        var alive = new HashSet<string>(items.Select(i => i.Id));
        var changed = 0;

        foreach (var it in items)
        {
            var hash = Hash(it.HashSource);
            if (docsSnapshot.TryGetValue(it.Id, out var doc) && doc.Hash == hash) continue;

            if (doc is not null)
                try { await knowledge.DeleteDocumentAsync(datasetId, doc.DocId); }
                catch (Exception ex) { log?.LogDebug(ex, "memory-dify: удаление старого документа записи {Entry}", it.Id); }

            var info = await knowledge.IndexFileByTextAsync(datasetId, it.DocName, it.Text, it.Tags);
            setDoc(it.Id, new MemoryDocRef { DocId = info.Id, Hash = hash });
            changed++;
        }

        foreach (var stale in docsSnapshot.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            try { await knowledge.DeleteDocumentAsync(datasetId, docsSnapshot[stale].DocId); }
            catch (Exception ex) { log?.LogDebug(ex, "memory-dify: удаление документа исчезнувшей записи {Entry}", stale); }
            removeDoc(stale);
            changed++;
        }

        return changed;
    }
}

// Дебаунс-планировщик синхронизации: на ключ (personaId / «owner:project») держит один одноразовый
// таймер, сбрасываемый при новой активности — синк идёт только после паузы SyncDebounce.
public sealed class MemoryDifyDebouncer(TimeSpan debounce)
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();

    public void Schedule(string key, Action action) =>
        _timers.AddOrUpdate(key,
            _ => new Timer(_ => action(), null, debounce, Timeout.InfiniteTimeSpan),
            (_, timer) => { timer.Change(debounce, Timeout.InfiniteTimeSpan); return timer; });
}
