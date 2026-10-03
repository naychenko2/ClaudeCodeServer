using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Операции патча фильма (ADR-022, FilmPatch): add, remove, move, cut, trim, music. Чистая функция от документа и
// списка операций: всё или ничего — первая же неверная операция отказывает со словами для человека, и документ
// не меняется. Файлы и диск не трогает (existence и границы проекта проверяет FilmService до вызова).
//
// Склейки живут между строками и лежат своим списком: Cuts[i] — стык между строками i и i+1. Поэтому вставка и
// удаление строки сдвигают и склейки: вставка добавляет встык рядом с новой строкой, удаление убирает стык
// рядом с убранной; перенос строк склейки не двигает (стык остаётся на своём месте).
internal static class FilmPatcher
{
    public sealed record Result(FilmDocument? Document, string? Error, IReadOnlyList<int> TouchedIndexes);

    // touched — индексы строк (после всех операций), которые операции добавили или поправили: по ним ставятся
    // пометки «✦ Claude». Удалённые строки в список не попадают
    public static Result Apply(FilmDocument document, IReadOnlyList<FilmPatchOp> ops)
    {
        var items = document.Items.ToList();
        var cuts = document.Cuts.ToList();
        var music = document.Music;
        // Отслеживаем строки по файлу, а не по индексу: индексы плывут от вставок и переносов
        var touchedFiles = new List<string>();

        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case FilmPatchOps.Add:
                    if (Add(items, cuts, op) is { } addError) return Fail(addError);
                    touchedFiles.Add(op.File!);
                    break;
                case FilmPatchOps.Remove:
                    if (op.Index is not { } removeAt || removeAt < 0 || removeAt >= items.Count)
                        return Fail("Нет такой сцены для удаления.");
                    items.RemoveAt(removeAt);
                    if (cuts.Count > 0) cuts.RemoveAt(Math.Min(removeAt, cuts.Count - 1));
                    break;
                case FilmPatchOps.Move:
                    if (op.From is not { } from || op.To is not { } to || from < 0 || from >= items.Count
                        || to < 0 || to >= items.Count)
                        return Fail("Перенос сцены: такого места нет.");
                    var moved = items[from];
                    items.RemoveAt(from);
                    items.Insert(to, moved);
                    touchedFiles.Add(moved.File);
                    break;
                case FilmPatchOps.Cut:
                    if (op.Index is not { } cutAt || cutAt < 0 || cutAt >= cuts.Count)
                        return Fail("Склейка: между сценами такого стыка нет.");
                    var type = op.CutType ?? FilmCutTypes.Butt;
                    var sec = type == FilmCutTypes.Butt ? 0 : op.Sec ?? 1;
                    if (FilmFormat.CutError(type, sec) is { } cutError) return Fail(cutError);
                    cuts[cutAt] = new FilmCut(type, sec);
                    touchedFiles.Add(items[cutAt].File);
                    break;
                case FilmPatchOps.Trim:
                    if (op.Index is not { } trimAt || trimAt < 0 || trimAt >= items.Count)
                        return Fail("Обрезка: такой сцены нет.");
                    if (FilmFormat.TrimError(op.Trim) is { } trimError) return Fail(trimError);
                    items[trimAt] = items[trimAt] with { Trim = [op.Trim![0], op.Trim[1]] };
                    touchedFiles.Add(items[trimAt].File);
                    break;
                case FilmPatchOps.Music:
                    if (op.Music is { } m && FilmFormat.MusicError(m) is { } musicError) return Fail(musicError);
                    music = op.Music;
                    break;
                default:
                    return Fail($"Неизвестная операция «{op.Op}».");
            }
        }

        var next = document with { Items = items, Cuts = cuts, Music = music };
        if (FilmFormat.Validate(next) is { } invalid) return Fail(invalid);
        var touched = touchedFiles.Distinct()
            .Select(f => next.Items.ToList().FindIndex(i => i.File == f)).Where(i => i >= 0).Order().ToList();
        return new Result(next, null, touched);
    }

    private static string? Add(List<FilmItem> items, List<FilmCut> cuts, FilmPatchOp op)
    {
        if (items.Count >= FilmFormat.MaxItems) return $"В фильме не больше {FilmFormat.MaxItems} сцен.";
        if (string.IsNullOrWhiteSpace(op.File)) return "Не указан файл сцены.";
        if (!FilmPaths.IsClipPath(op.File)) return "Файл сцены должен лежать в video/.";
        if (items.Any(i => i.File == op.File)) return "Эта сцена уже есть в фильме.";
        if (FilmFormat.TrimError(op.Trim) is { } trim) return trim;
        var at = op.Index ?? items.Count;
        if (at < 0 || at > items.Count) return "Добавление сцены: такого места нет.";

        items.Insert(at, new FilmItem(op.File, [op.Trim![0], op.Trim[1]], op.Scene));
        // Первая сцена стыков не заводит; иначе новый стык встык рядом с новой строкой (после неё, а в конце —
        // перед ней: стык между прежней последней и новой)
        if (items.Count > 1) cuts.Insert(Math.Min(at, cuts.Count), new FilmCut(FilmCutTypes.Butt, 0));
        return null;
    }

    private static Result Fail(string error) => new(null, error, []);
}
