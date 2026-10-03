using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.ChatContext;

// Файлы, которые сохранены из сцен этого чата (ADR-023 §3.3): читаются из следов сцен, а не из журнала событий —
// тот обрезается. Чужой чат не виден: стор ключуется владельцем и сессией. У записи без времени (до КТ-5) —
// время создания сцены
public sealed class VideoSavedFiles(VideoThreadStore store) : IChatSavedFiles
{
    public const string ThreadKind = "video";

    public IReadOnlyList<ChatSavedFile> List(ContextScope scope) =>
        [.. store.Get(scope.OwnerId, scope.Session.Id).Scenes
            .SelectMany(s => s.SavedFiles.Select(f => new ChatSavedFile(f.Path, ThreadKind, f.SavedAt ?? s.CreatedAt)))];
}
