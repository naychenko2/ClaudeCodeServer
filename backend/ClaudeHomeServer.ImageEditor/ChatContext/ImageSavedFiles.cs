using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;

namespace ClaudeHomeServer.Services.ImageEditor.ChatContext;

// Файлы, которые человек сохранил из картинок этого чата (ADR-023 §3.3): читаются из следов нитей,
// а не из журнала событий — тот обрезается. Чужой чат не виден: стор ключуется владельцем и сессией.
public sealed class ImageSavedFiles(ImageThreadStore store) : IChatSavedFiles
{
    public const string ThreadKind = "image";

    public IReadOnlyList<ChatSavedFile> List(ContextScope scope) =>
        [.. store.Get(scope.OwnerId, scope.Session.Id).Threads
            .SelectMany(t => t.SavedFiles)
            .Select(f => new ChatSavedFile(f.Path, ThreadKind, f.SavedAt))];
}
