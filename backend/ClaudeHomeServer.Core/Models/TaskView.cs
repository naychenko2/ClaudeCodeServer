using System.Reflection;
using System.Text;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Единая проекция задачи для участника общего проекта (S2c-9, разрез Александра §5, решение Андрея 01.11).
/// Через неё идут и список, и карточка — второй точки правды о том, «что видно участнику», нет:
/// карточка участника = элемент списка + тяжёлые поля (Description/ResultMarkdown), остальные поля те же.
///
/// Закрывает проекция:
/// • SourceNoteId — если источник заметки не этот проект: id заметки — base64url от «source|путь»
///   (ср. NotesService.EncodeId), id заметки личного vault владельца раскрывает участнику путь и
///   название личной заметки владельца.
///
/// Как есть (решение Андрея 01.11, Q2): ClaudeResult — участнику показывается как сейчас.
///
/// ChatRef (К5) — якорь шага S2d-4: id чатов (LinkedSessionId, SourceSessionId,
/// ExecutorLaunchedBySessionId, ReportToSessionId) здесь не меняются: заглушка для чужих приватных
/// чатов — отдельный шаг, и точка у него одна — эта, а не вторая.
///
/// Путь владельца через проекцию не идёт: свои задачи он получает как раньше (TaskListItem / полный
/// TaskItem). Ключ хранилища проекция не трогает: задача лежит у арендатора.
/// </summary>
public static class TaskView
{
    // Свойства базового TaskItem, которые можно скопировать (get + set/init) — тот же образец, что
    // в TaskListItem: чтобы новое поле модели не потерялось в проекции молча.
    private static readonly PropertyInfo[] CopyProps = typeof(TaskItem)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod is not null && p.SetMethod is not null)
        .ToArray();

    /// <summary>Полная карточка для участника: все поля + тяжёлый текст, SourceNoteId вне проекта закрыт.
    /// Хранимый объект не меняется — в ответ идёт копия (проекция правит только копию).</summary>
    public static TaskItem ForMember(TaskItem source)
    {
        var copy = new TaskItem();
        foreach (var p in CopyProps)
            p.SetValue(copy, p.GetValue(source));
        if (IsForeignNoteSource(copy)) copy.SourceNoteId = null;
        return copy;
    }

    /// <summary>Элемент списка для участника: тот же, что у владельца (TaskListItem.From), плюс та же
    /// маска SourceNoteId — проекция списка и карточки одна, тест на совпадение опирается на это.</summary>
    public static TaskListItem ForMemberListItem(TaskItem source)
    {
        var item = TaskListItem.From(source);
        if (IsForeignNoteSource(item)) item.SourceNoteId = null;
        return item;
    }

    /// <summary>Проекция для вызова MCP: актор идущего хода не арендатор — вид участника. Актор
    /// неизвестен (null — фон, исполнитель без инициатора) — права арендатора, как сейчас (разрез §2).</summary>
    public static TaskItem ForActor(TaskItem source, string tenantOwnerId, string? actorUserId) =>
        actorUserId is not null && actorUserId != tenantOwnerId ? ForMember(source) : source;

    /// <summary>Источник заметки — не этот проект: id декодируется (base64url «source|путь») и источник
    /// сверяется с проектом задачи. Личный vault, чужой проект, недекодируемый id — true (fail-closed:
    /// неизвестный источник участнику не открывается). Своя заметка этого проекта — false: участник
    /// заметки проекта и так читает.</summary>
    public static bool IsForeignNoteSource(TaskItem t)
    {
        if (t.SourceNoteId is null or "") return false;
        return NoteSourceOf(t.SourceNoteId) != t.ProjectId;
    }

    // Источник из id заметки; недекодируемый id — null (ср. DecodeId в NotesService: та же форма).
    private static string? NoteSourceOf(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        b64 = (b64.Length % 4) switch { 2 => b64 + "==", 3 => b64 + "=", _ => b64 };
        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
        var sep = raw.IndexOf('|');
        return sep < 0 ? null : raw[..sep];
    }
}
