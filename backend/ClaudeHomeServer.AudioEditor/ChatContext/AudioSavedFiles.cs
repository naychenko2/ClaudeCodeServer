using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.AudioEditor.ChatContext;

// Файлы, которые человек сохранил из звуков этого чата (ADR-023 §3.3): читаются из следов нитей,
// а не из журнала событий — тот обрезается. Чужой чат не виден: стор ключуется владельцем и сессией.
public sealed class AudioSavedFiles(AudioThreadStore store) : IChatSavedFiles
{
    public const string ThreadKind = "audio";

    public IReadOnlyList<ChatSavedFile> List(ContextScope scope) =>
        [.. store.Get(scope.OwnerId, scope.Session.Id).Threads
            .SelectMany(t => t.SavedFiles)
            .Select(f => new ChatSavedFile(f.Path, ThreadKind, f.SavedAt))];
}
