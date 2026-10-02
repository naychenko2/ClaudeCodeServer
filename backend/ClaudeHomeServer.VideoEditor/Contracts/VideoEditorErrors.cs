namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Коды ошибок ручек и тулсета: поле code в теле ответа. Чужое неотличимо от несуществующего (404).
// Менять значения нельзя: по ним ветвится фронт
public static class VideoEditorErrors
{
    // 409: имя файла проекта занято — перезаписи нет
    public const string NameTaken = "name_taken";
    // 409: ревизия нити или .film устарела; в теле свежее состояние
    public const string RevisionConflict = "revision_conflict";
    // 503: нет ffmpeg на хосте или выключена подсистема images
    public const string DspUnavailable = "dsp_unavailable";
    // 400: фильмы — только в чате проекта
    public const string PersonalScopeNoFilms = "personal_scope_no_films";
    // 400: локальная съёмка в личном чате закрыта (в v1)
    public const string LocalUnavailablePersonal = "local_unavailable_personal";
    // 400: локальный проект (ADR-016): файлы на устройстве, сервер файлы и сборку не трогает
    public const string ProjectLocalUnsupported = "project_local_unsupported";
    // 400: путь вне video/** и music/**
    public const string OutsideAllowedFolders = "outside_allowed_folders";
    // 422: .film не прошёл проверку (Cuts ≠ Items − 1, пустой путь, трим вне клипа)
    public const string FilmInvalid = "film_invalid";
    // 422: неизвестная схема .film — только чтение
    public const string FilmSchemaUnsupported = "film_schema_unsupported";
}
