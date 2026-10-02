namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Маршруты модуля. Проектные — api/projects/{projectId}/video-editor/*, личные (чат вне проекта) —
// api/video-editor/chats/{sessionId}/*. Хвосты у обоих одинаковые, у проектных перед сценами стоит
// sessions/{sessionId}/. Фильмы и сохранение в проект — только проектные (личные: personal_scope_no_films)
public static class VideoEditorRoutes
{
    public const string ProjectBase = "api/projects/{projectId}/video-editor";
    public const string PersonalBase = "api/video-editor/chats/{sessionId}";

    // Хвосты (после ProjectBase/sessions/{sessionId}/ или PersonalBase/)
    public const string State = "state";                                                  // GET  → VideoStateDto
    public const string Catalog = "catalog";                                              // GET  → VideoCatalogDto
    public const string Prefs = "prefs";                                                  // GET/PUT ↔ VideoPrefsDto
    public const string Quote = "quote";                                                  // POST VideoQuoteRequest → VideoQuoteResponse
    public const string Jobs = "jobs";                                                    // POST VideoLaunchRequest → VideoLaunchResult
    public const string Job = "jobs/{jobId}";                                             // GET статус / DELETE отмена
    public const string Scenes = "scenes";                                                // GET → VideoThreadsStateDto / POST новая сцена
    public const string ScenesFocus = "scenes/focus";                                     // PUT VideoFocusDto + revision
    public const string Scene = "scenes/{sceneId}";                                       // DELETE
    public const string SceneSettings = "scenes/{sceneId}/settings";                      // PUT VideoSceneSettingsDto + revision
    public const string SceneCurrent = "scenes/{sceneId}/current";                        // PUT versionId + revision
    public const string SceneVersionFile = "scenes/{sceneId}/versions/{versionId}/file";  // GET mp4
    public const string SceneVersionPoster = "scenes/{sceneId}/versions/{versionId}/poster"; // GET jpeg
    public const string SceneSave = "scenes/{sceneId}/save";                              // POST SaveSceneRequest → SaveSceneResult (блок 2)
    public const string Films = "films";                                                  // GET → FilmSummaryDto[] (блок 2)
    public const string FilmState = "films/state";                                        // GET ?path= → FilmStateDto
    public const string FilmPatchRoute = "films";                                         // PATCH ?path= FilmPatch → FilmStateDto
    public const string FilmBuild = "films/build";                                        // POST ?path= / GET статус / DELETE отмена → FilmBuildStatusDto
    public const string FilmMusic = "films/music";                                        // POST ?path= FilmMusicRequest → FilmMusicDraftDto («Сочинить под фильм…»)
}
