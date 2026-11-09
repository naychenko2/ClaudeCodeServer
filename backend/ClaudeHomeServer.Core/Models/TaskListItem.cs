using System.Reflection;

namespace ClaudeHomeServer.Models;

// Облегчённый элемент СПИСКОВ задач (GET /api/tasks, GET /api/projects/{id}/tasks).
// На проде Description и ResultMarkdown — ~45 из 58 МБ tasks.json, а фронт держит весь
// список в сторе ради дерева/канбана/плашек, где полный текст не нужен. Поэтому в списке:
//   • Description = "" и ResultMarkdown = null — тяжёлого текста в ответе нет;
//   • HasDescription / HasResult — признак наличия (вместо самого текста);
//   • DescriptionPreview — начало описания (до PreviewLength символов) для подсказок;
//   • ListItem = true — маркер «это облегчённый элемент, полный текст не загружен»:
//     фронт по нему догружает карточку по id (GET /api/tasks/{id} отдаёт полный TaskItem).
// Остальные поля (включая Repro/Verification) копируются как есть. Копирование — рефлексией
// по свойствам TaskItem, чтобы новое поле модели не потерялось в списке молча
// (сторож — TaskListItemTests: JSON списка совпадает с JSON полной задачи, кроме тяжёлых полей).
public sealed class TaskListItem : TaskItem
{
    public const int PreviewLength = 200;

    // Только getter: сериализуется, но не копируется из TaskItem (там такого свойства нет)
    public bool ListItem => true;
    public bool HasDescription { get; init; }
    public bool HasResult { get; init; }
    public string DescriptionPreview { get; init; } = "";

    // Свойства базового TaskItem, которые можно скопировать (get + set/init)
    private static readonly PropertyInfo[] CopyProps = typeof(TaskItem)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetMethod is not null && p.SetMethod is not null)
        .ToArray();

    public static TaskListItem From(TaskItem source)
    {
        var description = source.Description ?? "";
        var item = new TaskListItem
        {
            HasDescription = description.Length > 0,
            HasResult = !string.IsNullOrEmpty(source.ResultMarkdown),
            DescriptionPreview = Preview(description),
        };
        foreach (var p in CopyProps)
            p.SetValue(item, p.GetValue(source));
        // Тяжёлые поля — после копирования, чтобы перекрыть исходные значения
        item.Description = "";
        item.ResultMarkdown = null;
        return item;
    }

    // Начало текста без разрыва суррогатной пары; усечённое помечается «…»
    private static string Preview(string text)
    {
        if (text.Length <= PreviewLength) return text;
        var cut = PreviewLength;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
