namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// recordType карточек ленты (module_record, module = "videoeditor"). НЕ УДАЛЯТЬ И НЕ ПЕРЕИМЕНОВЫВАТЬ
// НИКОГДА: записи лежат в истории чатов, фронт рисует карточку по этому ключу, и после удаления
// старая лента показала бы пустоту. Новые — только добавлять
public static class VideoThreadRecordTypes
{
    public const string Module = "videoeditor";
    // Якорь сцены в ленте
    public const string Scene = "video_scene";
    // Варианты запуска стали версиями
    public const string LaunchVersions = "video_launch_versions";
    // Сцена сохранена в проект
    public const string Saved = "video_saved";
    // Фильм собран
    public const string FilmBuilt = "video_film_built";
    // Тихие строки-история (без карточки): кадр изменён, сцена добавлена в фильм, правка агента
    public const string Note = "video_note";
}
