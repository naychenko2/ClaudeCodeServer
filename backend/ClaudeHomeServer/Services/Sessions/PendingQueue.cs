using System.Collections.ObjectModel;

namespace ClaudeHomeServer.Services.Sessions;

/// <summary>
/// Очередь отложенных сообщений занятого чата (<c>SessionEntry.Pending</c>) с уведомлением об
/// изменении: любая мутация (постановка, разбор, удаление человеком) дёргает <see cref="Changed"/>,
/// по нему PendingQueueService пишет <c>pending.json</c> рядом с history.json. Единая точка вместо
/// правки полутора десятков мест, меняющих список: забытая ветка иначе молча теряла бы очередь
/// при рестарте. Порядок — список, не множество. Синхронизацию держит вызывающий (PendingLock).
/// </summary>
public sealed class PendingQueue : Collection<QueuedMessage>
{
    /// <summary>Вызывается после каждой мутации; до подключения хранилища — null.</summary>
    public Action? Changed { get; set; }

    protected override void InsertItem(int index, QueuedMessage item)
    {
        base.InsertItem(index, item);
        Changed?.Invoke();
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        Changed?.Invoke();
    }

    protected override void SetItem(int index, QueuedMessage item)
    {
        base.SetItem(index, item);
        Changed?.Invoke();
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        Changed?.Invoke();
    }

    public int RemoveAll(Predicate<QueuedMessage> match)
    {
        var removed = 0;
        for (var i = Count - 1; i >= 0; i--)
            if (match(this[i])) { RemoveAt(i); removed++; }
        return removed;
    }

    public int FindIndex(Predicate<QueuedMessage> match)
    {
        for (var i = 0; i < Count; i++)
            if (match(this[i])) return i;
        return -1;
    }

    public void InsertRange(int index, IEnumerable<QueuedMessage> items)
    {
        foreach (var item in items.ToList())
            Insert(index++, item);
    }

    /// <summary>Заполнить без уведомления — восстановление с диска на старте.</summary>
    internal void Restore(IEnumerable<QueuedMessage> items)
    {
        foreach (var item in items) Items.Add(item);
    }

    /// <summary>
    /// Заменить элементы по правилу, не трогая порядок (S2d-6b-7: снятие приватности переносит
    /// файлы вложений — записанные пути отложенных сообщений должны поехать за ними). transform
    /// отдаёт новую версию сообщения или null — оставить как есть. Каждая замена — обычный
    /// SetItem, то есть дёргает Changed поштучно, как <see cref="RemoveAll"/>: pending.json
    /// обновляется на каждую переписанную запись. Синхронизацию держит вызывающий (PendingLock).
    /// </summary>
    public int ReplaceWhere(Func<QueuedMessage, QueuedMessage?> transform)
    {
        var changed = 0;
        for (var i = 0; i < Count; i++)
        {
            var next = transform(this[i]);
            if (next is null) continue;
            this[i] = next;
            changed++;
        }
        return changed;
    }
}

// Содержимое pending.json: очередь по порядку и признак заморозки «Стоп». Рядом с коллекцией,
// которую он сериализует, и с контрактом её элементов (QueuedMessage в SessionEntry.cs):
// читает и пишет его ChatHistoryService (LoadPending/SavePending), собирает — PendingQueueService.
public sealed record PendingQueueFile(bool Frozen, List<QueuedMessage> Items);

// Хранители данных очереди перенесены из корня Services в ядро (хвост линии ядра, S3-1):
// их место — рядом с контрактом, который они держат. В корне они заставляли ядро «прятаться»:
// на них смотрели allow-list правила 4 сторожа формы ядра и точечный допуск root-сторожа.
