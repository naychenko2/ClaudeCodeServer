using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Человеческие обороты правки фильма для ленты. Change — глагол без окончания и остаток фразы: у человека глагол
// во множественном («переставили»), у агента в единственном («переставил»), лицо агента даёт метка «✦ Claude».
// Номера сцен и стыков — с единицы, как их видит человек в панели.
public static class FilmPatchText
{
    public sealed record Change(string Stem, string Rest)
    {
        public string Phrase(bool agent) => $"{Stem}{(agent ? "л" : "ли")} {Rest}";
    }

    // Обороты операций патча; document — фильм ПОСЛЕ правки (номер добавленной сцены известен только по нему)
    internal static IReadOnlyList<Change> Describe(IReadOnlyList<FilmPatchOp> ops, FilmDocument after)
    {
        var changes = new List<Change>();
        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case FilmPatchOps.Add:
                    var at = after.Items.ToList().FindIndex(i => i.File == op.File);
                    changes.Add(new Change("добави", at >= 0 ? $"сцену {at + 1}" : "сцену"));
                    break;
                case FilmPatchOps.Remove:
                    changes.Add(new Change("убра", op.Index is { } r ? $"сцену {r + 1}" : "сцену"));
                    break;
                case FilmPatchOps.Move:
                    changes.Add(new Change("перестави", "сцены"));
                    break;
                case FilmPatchOps.Cut:
                    changes.Add(new Change("поменя", op.Index is { } c
                        ? $"склейку {c + 1} → {c + 2} на {CutName(op.CutType)}"
                        : $"склейку на {CutName(op.CutType)}"));
                    break;
                case FilmPatchOps.Trim:
                    changes.Add(new Change("подреза", op.Index is { } t ? $"сцену {t + 1}" : "сцену"));
                    break;
                case FilmPatchOps.Music:
                    changes.Add(op.Music is { } m
                        ? new Change("постави", $"музыку «{Path.GetFileNameWithoutExtension(m.File)}»")
                        : new Change("убра", "музыку"));
                    break;
            }
        }
        return changes;
    }

    private static string CutName(string? type) => type switch
    {
        FilmCutTypes.Dissolve => "наплыв",
        FilmCutTypes.Fade => "затемнение",
        _ => "«встык»",
    };
}
