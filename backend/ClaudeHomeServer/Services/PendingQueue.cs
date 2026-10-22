using System.Collections.ObjectModel;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Очередь отложенных сообщений занятого чата (<c>SessionEntry.Pending</c>) с уведомлением об
/// изменении: любая мутация (постановка, разбор, удаление человеком) дёргает <see cref="Changed"/>,
/// по нему SessionManager пишет <c>pending.json</c> рядом с history.json. Единая точка вместо
/// правки полутора десятков мест, меняющих список: забытая ветка иначе молча теряла бы очередь
/// при рестарте. Порядок — список, не множество. Синхронизацию держит вызывающий (PendingLock).
/// </summary>
public sealed class PendingQueue : Collection<SessionManager.QueuedMessage>
{
    /// <summary>Вызывается после каждой мутации; до подключения хранилища — null.</summary>
    public Action? Changed { get; set; }

    protected override void InsertItem(int index, SessionManager.QueuedMessage item)
    {
        base.InsertItem(index, item);
        Changed?.Invoke();
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        Changed?.Invoke();
    }

    protected override void SetItem(int index, SessionManager.QueuedMessage item)
    {
        base.SetItem(index, item);
        Changed?.Invoke();
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        Changed?.Invoke();
    }

    public int RemoveAll(Predicate<SessionManager.QueuedMessage> match)
    {
        var removed = 0;
        for (var i = Count - 1; i >= 0; i--)
            if (match(this[i])) { RemoveAt(i); removed++; }
        return removed;
    }

    public int FindIndex(Predicate<SessionManager.QueuedMessage> match)
    {
        for (var i = 0; i < Count; i++)
            if (match(this[i])) return i;
        return -1;
    }

    public void InsertRange(int index, IEnumerable<SessionManager.QueuedMessage> items)
    {
        foreach (var item in items.ToList())
            Insert(index++, item);
    }

    /// <summary>Заполнить без уведомления — восстановление с диска на старте.</summary>
    internal void Restore(IEnumerable<SessionManager.QueuedMessage> items)
    {
        foreach (var item in items) Items.Add(item);
    }
}
