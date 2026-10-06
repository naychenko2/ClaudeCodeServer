namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Тексты тихих строк и карточек ленты (ADR-022): лицо даёт метка «✦ Claude» у агентской строки, поэтому
// у агента текст без слова «Claude» («Сохранил…»), у человека — «Вы сохранили…». Единая точка для сцены,
// правки и сборки фильма
public static class VideoFeedTexts
{
    public static string SceneSaved(string initiator, string sceneName, string path) =>
        $"{Verb(initiator, "Сохранил", "Вы сохранили")} сцену «{sceneName}» в проект: {path}";

    public static string FilmBuilt(string initiator, string filmName, string? file) =>
        $"{Verb(initiator, "Собрал", "Вы собрали")} фильм {filmName}: {file}";

    // Одна правка — «Вы переставили сцены» / «Переставил сцены»; серия — «Вы поправили фильм: переставили сцены,
    // подрезали сцену 1» (обороты — FilmPatchText.Change)
    public static string FilmPatched(string initiator, IReadOnlyList<Films.FilmPatchText.Change> changes)
    {
        var agent = initiator == VideoInitiators.Agent;
        if (changes.Count == 0) return $"{Verb(initiator, "Поправил", "Вы поправили")} фильм";
        if (changes.Count == 1)
        {
            var phrase = changes[0].Phrase(agent);
            return agent ? char.ToUpperInvariant(phrase[0]) + phrase[1..] : "Вы " + phrase;
        }
        return $"{Verb(initiator, "Поправил", "Вы поправили")} фильм: {string.Join(", ", changes.Select(c => c.Phrase(agent)))}";
    }

    private static string Verb(string initiator, string agent, string human) =>
        initiator == VideoInitiators.Agent ? agent : human;
}
