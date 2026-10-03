using ClaudeHomeServer.Services.ChatContext;
using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Кусок склейки — ровно одно из двух: версия нити этого чата (ThreadId; VersionId null — текущая)
// или файл проекта (ProjectFile — путь от корня проекта через «/»)
public sealed record AudioConcatPiece(string? ThreadId = null, string? VersionId = null, string? ProjectFile = null);

// Склейка (решение Андрея от 01.10, макет audio-editor-v2 «Склейка: новый файл из кусков»). Joint —
// общий стык, Joints — свой на каждое место (длина N−1, null — как общий). NormalizeLoudness — выровнять
// громкость кусков к −16 LUFS, включено по умолчанию. Name — имя нового файла, расширение даёт Format;
// Folder — папка проекта, куда человек сохранит черновик ("" — корень, у личного чата не задаётся)
public sealed record AudioConcatInput(
    string SessionId,
    IReadOnlyList<AudioConcatPiece> Pieces,
    AudioJoint? Joint = null,
    IReadOnlyList<AudioJoint?>? Joints = null,
    bool NormalizeLoudness = true,
    string? Name = null,
    AudioFormat Format = AudioFormat.Wav,
    string? Folder = null,
    AudioEditInitiator Initiator = AudioEditInitiator.Human,
    // Ревизия контекста чата (ADR-023 §Д2.1): с ней Pieces игнорируется — куски это референсы роли piece
    // по AddedAt; устарела — 409 context_changed
    long? ContextRevision = null);

public sealed record AudioConcatResultDto(string ThreadId, string VersionId, string JobId, string Name);

// Склейка звука без ИИ (операция AudioOp.Concat): куски — версии нитей чата и файлы проекта, итог —
// НОВАЯ нить-черновик с версией 1, а не версия одного из кусков: у склейки нет «главного» куска.
// Бесплатно и без очереди GPU, поэтому без котировки и потолков исполнителя задач: ffmpeg за швом
// IAudioDsp отвечает сразу, вызывающий ждёт итог.
//
// Инварианты:
// - в личной области файлы проекта запрещены — отказ ДО обращения к диску (как ScopeRefusal);
// - путь проекта — только через ProjectLinkGuard.ResolveInside, путь версии — только внутри папки её задачи;
// - версия-результат несёт jobId (рабочая папка не чистит её по TTL) и лицензии кусков: склейка трека
//   YuE2 остаётся CC BY-NC.
public sealed class AudioConcatService(
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    ILogger<AudioConcatService> log,
    IAudioDsp? dsp = null,
    ChatContext.AudioContextLaunch? context = null)
{
    // Кусок на диске крупнее — отказ до чтения: склейка держит все куски в памяти
    public const long MaxPieceBytes = 200L * 1024 * 1024;
    public const int MaxNameLength = 120;

    public const string DspUnavailableText = "На сервере нет ffmpeg — склейка недоступна. Поставить его может администратор";
    public const string PersonalProjectFileText =
        "В личном чате куски — только звук этого чата: файлы проекта здесь недоступны";

    private sealed record Piece(byte[] Audio, string Label, string? License);

    public async Task<AudioEditCallResult<AudioConcatResultDto>> ConcatAsync(
        string ownerId, AudioEditScope scope, AudioConcatInput input, CancellationToken ct)
    {
        if (dsp is null || !dsp.Available) return Fail(AudioEditErrorCodes.DspUnavailable, DspUnavailableText);
        var requested = input.Pieces;
        if (input.ContextRevision is { } revision)
        {
            if (context is null) return Fail(AudioEditErrorCodes.Unavailable, ChatContext.AudioContextLaunch.UnavailableText);
            var read = context.Read(ownerId, scope, input.SessionId, revision);
            if (!read.Ok) return read.Fail<AudioConcatResultDto>();
            requested = ChatContext.AudioContextLaunch.Extract(scope, read.State!, AudioEditJobService.OpName(AudioOp.Concat)).Pieces;
            if (requested.Count < AudioDspLimits.MinConcatPieces)
                return Invalid("В контексте чата меньше двух кусков склейки — добавьте звуки с ролью «Кусок»");
        }
        if (requested.Count < AudioDspLimits.MinConcatPieces) return Invalid("Нужно хотя бы два куска — добавьте ещё один");
        if (requested.Count > AudioDspLimits.MaxConcatPieces)
            return Invalid($"Кусков для склейки — не больше {AudioDspLimits.MaxConcatPieces}");
        if (input.Joints is { } own && own.Count != requested.Count - 1)
            return Invalid("Своих стыков должно быть на один меньше, чем кусков");
        if (!Enum.IsDefined(input.Format)) return Invalid("Неизвестный формат результата");
        if (FileName(input.Name, input.Format) is not { } name) return Invalid("Имя файла — без папок и не длиннее 120 символов");
        if (Folder(scope, input.Folder) is not { } folder) return Invalid("Папка для сохранения — вне проекта");
        if (!threads.OwnChat(ownerId, scope.Key, input.SessionId)) return Invalid("Чат не найден");
        var sessionId = input.SessionId.Trim();

        var pieces = new List<Piece>();
        for (var i = 0; i < requested.Count; i++)
        {
            var (piece, error) = await ResolveAsync(ownerId, scope, sessionId, requested[i], ct);
            if (piece is null) return Invalid($"Кусок {i + 1}: {error}");
            pieces.Add(piece);
        }
        var joints = Enumerable.Range(0, pieces.Count - 1)
            .Select(i => input.Joints?[i] ?? input.Joint ?? AudioJoint.Butt).ToList();

        var output = await dsp.ConcatAsync([.. pieces.Select(p => p.Audio)], joints,
            input.NormalizeLoudness ? AudioDspLimits.ConcatLufs : null, input.Format, ct);
        if (output.Audio is null) return Invalid(output.Error ?? "Склейка не получилась");

        var jobId = Guid.NewGuid().ToString("N");
        var path = workspace.SaveFile(ownerId, jobId, 1, AudioFileRoles.Main, output.Audio, output.Extension);
        return await WriteThreadAsync(ownerId, scope, sessionId, jobId, name, folder, input, pieces, joints,
            [new AudioVersionFile(AudioFileRoles.Main, path)], ct);
    }

    // Новая нить-черновик: запуск склейки и его итог — версия 1 с jobId; фокус чата — на неё
    private async Task<AudioEditCallResult<AudioConcatResultDto>> WriteThreadAsync(
        string ownerId, AudioEditScope scope, string sessionId, string jobId, string name, string folder,
        AudioConcatInput input, IReadOnlyList<Piece> pieces, IReadOnlyList<AudioJoint> joints,
        IReadOnlyList<AudioVersionFile> files, CancellationToken ct)
    {
        var store = threads.Store;
        var opened = threads.Tracked(ownerId, sessionId,
            () => store.Open(ownerId, sessionId, null, folder, null,
                new AudioThreadSettings(AudioModes.Process, AudioEditJobService.OpName(AudioOp.Concat), null, null, null), name),
            input.Initiator == AudioEditInitiator.Agent ? ContextActor.Agent : ContextActor.Human);
        var thread = opened.Thread!;
        var who = input.Initiator == AudioEditInitiator.Agent ? SpendInitiators.Agent : SpendInitiators.Human;
        var license = License(pieces);
        var recipe = Recipe(pieces, joints);
        store.AddLaunch(ownerId, sessionId, thread.Id,
            new AudioThreadLaunch(jobId, null, store.Now(), AudioThreadLaunchStatus.Running, who, recipe, license),
            new AudioThreadEvent(store.Now(), AudioThreadEventKinds.Launched,
                $"{(input.Initiator == AudioEditInitiator.Agent ? "Ты склеил" : "Человек склеил")} {pieces.Count} кусков в новый файл {name}: {recipe}",
                thread.Id, jobId));
        var finished = store.FinishLaunch(ownerId, sessionId, thread.Id, jobId, AudioThreadLaunchStatus.Done, [(1, files)],
            new AudioThreadEvent(store.Now(), AudioThreadEventKinds.Versions, $"Готово: новый файл {name}", thread.Id, jobId));
        if (finished.Status != AudioThreadWriteStatus.Ok || finished.NewVersions.Count == 0)
        {
            log.LogWarning("Звук: склейка {JobId} не легла в нить {ThreadId}: {Status}", jobId, thread.Id, finished.Status);
            return Invalid("Не удалось сохранить результат склейки");
        }

        await threads.AnchorAsync(sessionId, finished.Thread!, ct);
        await threads.BroadcastAsync(ownerId, scope.Key, sessionId, finished.State);
        return AudioEditCallResult<AudioConcatResultDto>.Ok(
            new AudioConcatResultDto(thread.Id, finished.NewVersions[0].Id, jobId, name));
    }

    private async Task<(Piece? Piece, string? Error)> ResolveAsync(
        string ownerId, AudioEditScope scope, string sessionId, AudioConcatPiece piece, CancellationToken ct)
    {
        var hasThread = !string.IsNullOrWhiteSpace(piece.ThreadId);
        var hasFile = !string.IsNullOrWhiteSpace(piece.ProjectFile);
        if (hasThread == hasFile) return (null, "укажите версию нити или файл проекта");

        if (hasFile)
        {
            var file = piece.ProjectFile!.Trim();
            return await ReadProjectAsync(scope, file, file, null, ct);
        }

        var thread = threads.Store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == piece.ThreadId!.Trim());
        if (thread is null) return (null, "нить не найдена");
        var version = thread.Version(piece.VersionId?.Trim() ?? thread.CurrentVersionId);
        if (version?.File(AudioFileRoles.Main) is not { } main) return (null, "у версии нет звука");
        var label = $"{AudioJobThreads.Name(thread)} {(version.IsOrigin ? "исходник" : $"в{version.Number}")}";

        // Исходник — файл проекта: те же правила, что у куска-файла
        if (version.IsOrigin) return await ReadProjectAsync(scope, main.Path, label, version.License, ct);
        // Версия запуска и правки без ИИ (DspAudioEngine) — файл в папке своей задачи. Без jobId — только
        // старые записи до этапа 5: файла на диске у них нет
        if (version.JobId is not { } jobId) return (null, "у этой версии нет файла на сервере");
        var full = ProjectLinkGuard.ResolveInside(workspace.JobDir(ownerId, jobId), main.Path);
        return await ReadAsync(full, label, version.License, ct);
    }

    // Отказ личной области — до диска: Project у неё нет, RootPath не читаем
    private static async Task<(Piece? Piece, string? Error)> ReadProjectAsync(
        AudioEditScope scope, string relative, string label, string? license, CancellationToken ct)
    {
        if (scope.IsPersonal || scope.Project is not { } project) return (null, PersonalProjectFileText);
        return await ReadAsync(ProjectLinkGuard.ResolveInside(project.RootPath, relative), label, license, ct);
    }

    private static async Task<(Piece? Piece, string? Error)> ReadAsync(string? full, string label, string? license, CancellationToken ct)
    {
        if (full is null) return (null, "путь вне проекта");
        var info = new FileInfo(full);
        if (!info.Exists) return (null, "файл не найден");
        if (info.Length > MaxPieceBytes) return (null, $"файл больше {MaxPieceBytes / 1024 / 1024} МБ");
        return (new Piece(await File.ReadAllBytesAsync(full, ct), label, license), null);
    }

    // Лицензии кусков по порядку без повторов: склейка наследует все ограничения
    private static string? License(IReadOnlyList<Piece> pieces)
    {
        var labels = pieces.Select(p => p.License).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return labels.Count == 0 ? null : string.Join(", ", labels);
    }

    // «intro.mp3 в3 → пауза 0,5 с → greeting.wav исходник»
    private static string Recipe(IReadOnlyList<Piece> pieces, IReadOnlyList<AudioJoint> joints)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var parts = new List<string> { pieces[0].Label };
        for (var i = 0; i < joints.Count; i++)
        {
            parts.Add(joints[i].Kind switch
            {
                AudioJointKind.Pause => $"пауза {joints[i].Seconds.ToString("0.##", ru)} с",
                AudioJointKind.Crossfade => $"плавно {joints[i].Seconds.ToString("0.##", ru)} с",
                _ => "встык",
            });
            parts.Add(pieces[i + 1].Label);
        }
        return string.Join(" → ", parts);
    }

    // Имя без папок; расширение — по формату результата. null — имя не годится
    private static string? FileName(string? name, AudioFormat format)
    {
        if (name is not null && name.IndexOfAny(['/', '\\']) >= 0) return null;
        var stem = string.IsNullOrWhiteSpace(name) ? "склейка" : Path.GetFileNameWithoutExtension(name.Trim()).Trim();
        if (stem.Length is 0 or > MaxNameLength || stem.Contains("..")
            || stem.IndexOfAny(['/', '\\', ':']) >= 0 || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return stem + AudioFormats.Extension(format);
    }

    // Папка сохранения: у личной области её нет, у проекта — только внутри проекта. null — отказ
    private static string? Folder(AudioEditScope scope, string? folder)
    {
        var f = folder?.Trim().Trim('/') ?? "";
        if (f.Length == 0) return "";
        if (scope.IsPersonal || scope.Project is not { } project) return null;
        return ProjectLinkGuard.ResolveInside(project.RootPath, f) is null ? null : f;
    }

    private static AudioEditCallResult<AudioConcatResultDto> Invalid(string error) =>
        Fail(AudioEditErrorCodes.InvalidRequest, error);

    private static AudioEditCallResult<AudioConcatResultDto> Fail(string code, string error) =>
        AudioEditCallResult<AudioConcatResultDto>.Fail(code, error);
}
