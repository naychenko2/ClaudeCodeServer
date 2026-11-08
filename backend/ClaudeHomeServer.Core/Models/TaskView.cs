using System.Reflection;
using System.Text;
using ClaudeHomeServer.Services;

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
/// ChatRef (К5, шаг S2d-4b) — здесь: по каждому id чата задачи (LinkedSessionId, SourceSessionId,
/// ExecutorLaunchedBySessionId, ReportToSessionId) решение отдаёт <see cref="ChatRefs"/>, и оно
/// одно на все роли — маска стоит и в пути участника, и в пути владельца (владелец проекта чужой
/// приватный чат тоже не видит). Видимый чат — id в своём поле, как раньше; чужой приватный — id
/// обнулён, рядом форма-заглушка. Второй точки правды нет; путь MCP (ForActor → TasksToolset) —
/// следующий шаг S2d-4b2.
///
/// Путь владельца через проекцию состава полей не идёт: свои задачи он получает как раньше
/// (TaskListItem / полный TaskItem), но с той же маской чатов (CopyOf + MaskForeignChats).
/// Ключ хранилища проекция не трогает: задача лежит у арендатора.
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
        var copy = CopyOf(source);
        if (IsForeignNoteSource(copy)) copy.SourceNoteId = null;
        return copy;
    }

    /// <summary>Форма ответа на запись (F2-T1): та же, что у пути чтения, — второй точки правды
    /// о том, что видно участнику, нет ни в REST-двери задач, ни в MCP. Владелец записи
    /// (арендатор общего проекта или сам вызывающий для личной) получает полную копию,
    /// участник — ForMember. Ключ — владение записью, а не роль в проекте: личная задача
    /// вызывающего всегда полная.</summary>
    public static TaskItem ForWrite(TaskItem source, string callerUserId) =>
        source.OwnerId == callerUserId ? CopyOf(source) : ForMember(source);

    /// <summary>Копия хранимого объекта со всеми полями — для путей, где состав ответа не меняется
    /// (карточка и элемент списка владельца), но нужна маска полей: проекция правит копию, а не
    /// запись стора.</summary>
    public static TaskItem CopyOf(TaskItem source)
    {
        var copy = new TaskItem();
        foreach (var p in CopyProps)
            p.SetValue(copy, p.GetValue(source));
        return copy;
    }

    /// <summary>Маска чужих приватных чатов задачи (S2d-4b, К5) — одна для всех ролей, включая
    /// владельца проекта. По каждому id чата решение отдаёт <see cref="ChatRefs.For"/> для этой
    /// аудитории: видимый чат — id остаётся в своём поле, отдельного объекта нет; чужой приватный —
    /// id обнуляется, рядом становится форма-заглушка. Несуществующий чат не маскируется (см.
    /// ChatRefs.For), как и случай «каталог не передан»: решения нет — поведение прежнее.
    ///
    /// Правит переданный объект и возвращает его (для цепочки): передавай копию — CopyOf или
    /// результат проекции, — а не запись стора.</summary>
    public static T MaskForeignChats<T>(T task, ISessionDirectory? sessions, IProjectManager? projects,
        ChatAudience audience) where T : TaskItem
    {
        void Apply(string? id, Action<string?> setId, Action<ChatRef?> setRef)
        {
            var hidden = ChatRefs.For(sessions, projects, id, audience);
            if (hidden is not { Private: true }) return;
            setId(null);
            setRef(hidden);
        }

        Apply(task.LinkedSessionId, v => task.LinkedSessionId = v, r => task.LinkedChat = r);
        Apply(task.SourceSessionId, v => task.SourceSessionId = v, r => task.SourceChat = r);
        Apply(task.ExecutorLaunchedBySessionId, v => task.ExecutorLaunchedBySessionId = v, r => task.ExecutorLaunchedByChat = r);
        Apply(task.ReportToSessionId, v => task.ReportToSessionId = v, r => task.ReportToChat = r);
        return task;
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
