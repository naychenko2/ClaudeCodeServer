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

    // Общие коды исполнителя и ручек сцен (добавлены после КТ-1)
    public const string InvalidRequest = "invalid_request";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string QuoteNotFound = "quote_not_found";
    public const string TooManyJobs = "too_many_jobs";
    public const string HeavyBusy = "heavy_busy";
    public const string ChatNotFound = "chat_not_found";
    public const string SceneNotFound = "scene_not_found";
    public const string VersionNotFound = "version_not_found";
    public const string JobNotFound = "job_not_found";
    public const string FileNotFound = "file_not_found";
    // Кадр сцены прочитать нельзя: файла нет, он вне проекта или кадр из «Картинок» ещё не подключён швом
    public const string FrameUnavailable = "frame_unavailable";
    // 409: ревизия контекста чата устарела (ADR-023 §Д2.1); тело — свежий ChatContextDto
    public const string ContextChanged = "context_changed";
}
