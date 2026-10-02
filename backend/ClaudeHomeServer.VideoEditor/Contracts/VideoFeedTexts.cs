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

    public static string FilmPatched(string initiator, string filmName, string what) =>
        $"{Verb(initiator, "Поправил", "Вы поправили")} фильм {filmName}: {what}";

    private static string Verb(string initiator, string agent, string human) =>
        initiator == VideoInitiators.Agent ? agent : human;
}
