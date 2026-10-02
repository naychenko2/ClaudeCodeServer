using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// Агент позвал local_* напрямую, мимо audio_generate: звук лёг файлом в проект, а ленте нечего показать,
// кроме голого вызова инструмента. Усыновитель заводит нить по каждому звуковому файлу результата и кладёт
// якорь в ленту — та же богатая карточка (плеер, волна, версии, действия), что у запуска кнопкой или
// через audio_*. Нить по этому файлу уже есть — второго якоря нет. Фокус человека не трогаем: выбор звука
// в полосе остаётся его. Выключенный модуль (флаг) и чужой чат — молчаливый отказ.
public sealed class LocalAudioAdopter(
    AudioThreadStore store,
    AudioJobThreads threads,
    ISessionDirectory directory,
    IProjectManager projects,
    IFeatureFlagGate flags) : ILocalMediaAdopter
{
    // Столько карточек на задачу достаточно: стемы шести дорожек — это уже не «результат», а набор
    public const int MaxThreadsPerJob = 6;

    public async Task AdoptAsync(LocalMediaAdoption adoption, CancellationToken ct)
    {
        var files = adoption.Files
            .Where(f => f.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            .Take(MaxThreadsPerJob).ToList();
        if (files.Count == 0) return;
        if (!flags.IsEnabled(adoption.OwnerId, FeatureFlagKeys.AudioEditor)) return;
        if (directory.GetById(adoption.SessionId) is not { } session || session.ProjectId != adoption.ProjectId) return;
        if (directory.ResolveOwnerId(session) != adoption.OwnerId) return;
        if (projects.GetById(adoption.ProjectId) is not { } project || project.OwnerId != adoption.OwnerId) return;

        var scope = AudioEditScope.Of(project);
        var before = store.Get(adoption.OwnerId, session.Id).Focus;
        AudioThreadsState? last = null;
        foreach (var file in files)
        {
            var written = store.Open(adoption.OwnerId, session.Id, file.Path, null, null);
            if (written is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread }) continue;
            last = written.State;
            if (!written.Existing) await threads.AnchorAsync(session.Id, thread, ct);
        }
        if (last is null) return;

        // Open отдаёт фокус новой нити; прежний выбор человека возвращаем
        if (before is not null && last.Focus != before)
        {
            var back = store.SetFocus(adoption.OwnerId, session.Id, before, null);
            if (back.Status == AudioThreadWriteStatus.Ok) last = back.State;
        }
        await threads.BroadcastAsync(adoption.OwnerId, scope.Key, session.Id, last);
    }
}
