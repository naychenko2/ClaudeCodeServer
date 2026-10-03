using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// Агент позвал local_* напрямую, мимо audio_generate: звук лёг файлом в проект, а ленте нечего показать,
// кроме голого вызова инструмента. Усыновитель заводит нить и кладёт якорь в ленту — та же богатая карточка
// (плеер, волна, версии, действия), что у запуска кнопкой или через audio_*. Форма нити — как у кнопки:
//  - разделение на стемы (audio_separate) — ОДНА нить и ОДНА версия со стемами по ролям (stem:vocals…),
//    запуск и его якорь audio_launch_versions, как у кнопки separate;
//  - остальные операции — нить по каждому звуковому файлу; нить по этому файлу уже есть — второго якоря нет.
// Фокус человека не трогаем: выбор звука в полосе остаётся его. Выключенный модуль (флаг) и чужой чат —
// молчаливый отказ.
public sealed class LocalAudioAdopter(
    AudioThreadStore store,
    AudioJobThreads threads,
    ISessionDirectory directory,
    IProjectManager projects,
    IFeatureFlagGate flags) : ILocalMediaAdopter
{
    // Столько карточек на задачу достаточно: набор из стольких файлов — это уже не «результат»
    public const int MaxThreadsPerJob = 6;
    // Стемов в одной версии: шесть дорожек HTDemucs и запас
    public const int MaxStems = 8;

    // Сколько раз перечитываем ревизию, если человек записал своё между чтением и записью
    private const int Attempts = 3;

    private const string SeparateOp = "separate";
    private const string StemsModel = "local-media";

    public async Task AdoptAsync(LocalMediaAdoption adoption, CancellationToken ct)
    {
        var separate = adoption.Op == "audio_separate";
        var files = adoption.Files
            .Where(f => f.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            .Take(separate ? MaxStems : MaxThreadsPerJob).ToList();
        if (files.Count == 0) return;
        if (!flags.IsEnabled(adoption.OwnerId, FeatureFlagKeys.AudioEditor)) return;
        if (directory.GetById(adoption.SessionId) is not { } session || session.ProjectId != adoption.ProjectId) return;
        if (directory.ResolveOwnerId(session) != adoption.OwnerId) return;
        if (projects.GetById(adoption.ProjectId) is not { } project || project.OwnerId != adoption.OwnerId) return;

        var scope = AudioEditScope.Of(project);
        var before = store.Get(adoption.OwnerId, session.Id).Focus;
        var last = separate
            ? await AdoptStemsAsync(adoption, session.Id, files, ct)
            : await AdoptFilesAsync(adoption, session.Id, files, ct);
        if (last is null) return;

        // Open отдаёт фокус новой нити; прежний выбор человека возвращаем — только если ревизию с тех пор
        // никто не двигал: конфликт значит, что человек уже выбрал сам, и его выбор важнее
        if (before is not null && last.Focus != before
            && store.SetFocus(adoption.OwnerId, session.Id, before, last.Revision) is { Status: AudioThreadWriteStatus.Ok } back)
            last = back.State;
        threads.SyncFocus(adoption.OwnerId, session.Id, before, last.Focus, ContextActor.Agent);
        await threads.BroadcastAsync(adoption.OwnerId, scope.Key, session.Id, last);
    }

    private async Task<AudioThreadsState?> AdoptFilesAsync(LocalMediaAdoption adoption, string sessionId,
        IReadOnlyList<LocalMediaAdoptedFile> files, CancellationToken ct)
    {
        AudioThreadsState? last = null;
        foreach (var file in files)
        {
            var written = Open(adoption.OwnerId, sessionId, file.Path, null, null);
            if (written is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread }) continue;
            last = written.State;
            if (!written.Existing) await threads.AnchorAsync(sessionId, thread, ct);
        }
        return last;
    }

    // Стемы одной задачи — черновик нити с одной версией, роли файлов — stem:<имя>. Запуск и якорь те же,
    // что у кнопки; повторное усыновление той же задачи (нить с её запуском уже есть) ничего не пишет
    private async Task<AudioThreadsState?> AdoptStemsAsync(LocalMediaAdoption adoption, string sessionId,
        IReadOnlyList<LocalMediaAdoptedFile> files, CancellationToken ct)
    {
        var owner = adoption.OwnerId;
        if (store.Get(owner, sessionId).Threads.Any(t => t.Launches.Any(l => l.JobId == adoption.JobId))) return null;

        var folder = files[0].Path.Contains('/') ? files[0].Path[..files[0].Path.LastIndexOf('/')] : "";
        var opened = Open(owner, sessionId, null, folder, "Стемы");
        if (opened is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread }) return null;

        var now = store.Now();
        var launched = store.AddLaunch(owner, sessionId, thread.Id,
            new AudioThreadLaunch(adoption.JobId, null, now, AudioThreadLaunchStatus.Running, SpendInitiators.Agent, null, null));
        if (launched.Status != AudioThreadWriteStatus.Ok) return opened.State;

        var finished = store.FinishLaunch(owner, sessionId, thread.Id, adoption.JobId, AudioThreadLaunchStatus.Done,
            [(1, StemFiles(adoption.JobId, files))],
            new AudioThreadEvent(now, AudioThreadEventKinds.Versions,
                $"Claude разделил звук на стемы (local-media): {files.Count} файл(ов)", thread.Id, adoption.JobId));
        var state = finished.Status == AudioThreadWriteStatus.Ok ? finished.State : launched.State;
        await threads.AnchorLaunchAsync(sessionId, thread, adoption.JobId, SeparateOp, StemsModel, SpendInitiators.Agent, ct);
        return state;
    }

    // Имя стема — хвост имени файла после «{jobId}-» (lm_…-no_vocals.wav → stem:no_vocals); без хвоста —
    // номер файла. Повтор имени получает номер, чтобы роли в версии не совпали
    internal static List<AudioVersionFile> StemFiles(string jobId, IReadOnlyList<LocalMediaAdoptedFile> files)
    {
        var result = new List<AudioVersionFile>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(files[i].Path);
            var head = jobId + "-";
            var tail = name.StartsWith(head, StringComparison.Ordinal) ? name[head.Length..] : "";
            tail = new string([.. tail.Take(40).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
            if (tail.Length == 0) tail = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = AudioFileRoles.Stem(tail);
            if (!used.Add(role)) role = AudioFileRoles.Stem($"{tail}_{i + 1}");
            result.Add(new AudioVersionFile(role, files[i].Path));
        }
        return result;
    }

    // Open с перечитыванием ревизии при конфликте: человек мог записать своё между чтением и записью
    private AudioThreadWrite Open(string ownerId, string sessionId, string? file, string? draftFolder, string? name)
    {
        AudioThreadWrite written = null!;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            written = store.Open(ownerId, sessionId, file, draftFolder, store.Get(ownerId, sessionId).Revision, null, name);
            if (written.Status != AudioThreadWriteStatus.Conflict) break;
        }
        return written;
    }
}
