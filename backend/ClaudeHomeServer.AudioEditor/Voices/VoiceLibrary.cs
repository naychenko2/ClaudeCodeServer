using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.AudioEditor.Voices;

// Библиотека «Голоса» области (ADR-021 §2): голоса живут только в серверном проекте. Личная область и
// локальный проект получают отказ ДО диска: у личной корня нет вовсе, у локального файлы на устройстве
// (ProjectCapabilityGuard, группа FileBound). Часы подменяемые — по ним считается признак «клон MiniMax
// протух».
public sealed class VoiceLibrary(TimeProvider? time = null) : Mcp.IAudioVoiceLibrary
{
    // Код отказа личной области: фронт рисует по нему честный пустой экран «Голоса живут в проекте»
    public const string ProjectOnlyCode = "voices_project_only";
    public const string ProjectOnlyReason = "Библиотека «Голоса» живёт в проекте — в личном чате загрузите образец";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    // Корень проекта для голосов или отказ; диск здесь не трогается
    public string? RootOf(AudioEditScope scope, out AudioEditCallResult<VoiceChange>? refusal)
    {
        refusal = null;
        if (scope.Project is not { } project)
        {
            refusal = AudioEditCallResult<VoiceChange>.Fail(ProjectOnlyCode, ProjectOnlyReason);
            return null;
        }
        if (ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is { } reason)
        {
            refusal = AudioEditCallResult<VoiceChange>.Fail(ProjectCapabilityGuard.Code, reason);
            return null;
        }
        return project.RootPath;
    }

    public IReadOnlyList<VoiceDto>? List(AudioEditScope scope) =>
        RootOf(scope, out _) is { } root ? [.. VoiceStore.List(root).Select(m => VoiceDto.From(m, Now))] : null;

    // Голоса для агента (audio_state, voice у audio_generate): slug и имя, без id поставщиков
    IReadOnlyList<Mcp.AudioLibraryVoice> Mcp.IAudioVoiceLibrary.List(string ownerId, AudioEditScope scope) =>
        [.. (List(scope) ?? []).Select(v => new Mcp.AudioLibraryVoice(v.Slug, v.Name,
            v.Kind == VoiceKinds.Rvc ? "модель RVC — только смена голоса локальной моделью" : null))];

    public VoiceDto? Get(AudioEditScope scope, string slug) =>
        RootOf(scope, out _) is { } root && VoiceStore.Get(root, slug) is { } m ? VoiceDto.From(m, Now) : null;

    public AudioEditCallResult<VoiceChange> CreateFromSamples(AudioEditScope scope, string name, string? transcript,
        IReadOnlyList<VoiceSampleUpload> samples) =>
        RootOf(scope, out var refusal) is { } root ? VoiceStore.CreateFromSamples(root, name, transcript, samples, Now) : refusal!;

    public AudioEditCallResult<VoiceChange> CreateFromRvc(AudioEditScope scope, string name, string modelPath, string indexPath) =>
        RootOf(scope, out var refusal) is { } root ? VoiceStore.CreateFromRvc(root, name, modelPath, indexPath, Now) : refusal!;

    // null — голоса нет
    public AudioEditCallResult<VoiceChange>? Update(AudioEditScope scope, string slug, string? name, string? transcript) =>
        RootOf(scope, out var refusal) is { } root ? VoiceStore.Update(root, slug, name, transcript) : refusal;

    public AudioEditCallResult<VoiceChange>? AddSamples(AudioEditScope scope, string slug, IReadOnlyList<VoiceSampleUpload> samples) =>
        RootOf(scope, out var refusal) is { } root ? VoiceStore.AddSamples(root, slug, samples) : refusal;

    public AudioEditCallResult<VoiceChange>? RemoveSample(AudioEditScope scope, string slug, string file) =>
        RootOf(scope, out var refusal) is { } root ? VoiceStore.RemoveSample(root, slug, file) : refusal;

    public bool Delete(AudioEditScope scope, string slug) =>
        RootOf(scope, out _) is { } root && VoiceStore.Delete(root, slug);

    public string? OpenFile(AudioEditScope scope, string slug, string file) =>
        RootOf(scope, out _) is { } root ? VoiceStore.OpenFile(root, slug, file) : null;

    // Клон MiniMax ушёл в запуск — отсчёт 7 дней заново. false — голоса или клона нет
    public bool TouchMiniMax(AudioEditScope scope, string slug)
    {
        if (RootOf(scope, out _) is not { } root) return false;
        var touched = false;
        VoiceStore.UpdateProviders(root, slug, p => touched = VoiceProviders.TouchMiniMax(p, Now));
        return touched;
    }

    // Голос для запуска: первый образец с расшифровкой или пара RVC плюс кеш id поставщиков. Отказ области —
    // до диска; голоса нет — voice_not_found
    public async Task<AudioEditCallResult<AudioVoiceUse>> ForLaunchAsync(AudioEditScope scope, string slug, CancellationToken ct)
    {
        if (RootOf(scope, out var refusal) is not { } root)
            return AudioEditCallResult<AudioVoiceUse>.Fail(refusal!.ErrorCode!, refusal.Error!);
        if (VoiceStore.Get(root, slug) is not { } manifest)
            return AudioEditCallResult<AudioVoiceUse>.Fail(AudioEditErrorCodes.VoiceNotFound, $"Голоса «{slug}» нет в библиотеке проекта");

        var ids = VoiceProviders.Ids(manifest.Providers);
        if (manifest.Kind == VoiceKinds.Rvc)
        {
            if (VoiceProviders.RvcPair(manifest.Providers) is not { } pair
                || VoiceStore.OpenFile(root, slug, pair.Model) is not { } model
                || VoiceStore.OpenFile(root, slug, pair.Index) is not { } index)
                return AudioEditCallResult<AudioVoiceUse>.Fail(AudioEditErrorCodes.FileNotFound, "У голоса нет файлов модели RVC");
            return AudioEditCallResult<AudioVoiceUse>.Ok(new AudioVoiceUse(slug, null, null,
                await File.ReadAllBytesAsync(model, ct), await File.ReadAllBytesAsync(index, ct), ids));
        }

        AudioBytes? sample = null;
        foreach (var s in manifest.Samples)
        {
            if (VoiceStore.OpenFile(root, slug, s.File) is not { } path) continue;
            sample = new AudioBytes(await File.ReadAllBytesAsync(path, ct), Controllers.AudioVersionFiles.ContentTypeOf(path));
            break;
        }
        if (sample is null)
            return AudioEditCallResult<AudioVoiceUse>.Fail(AudioEditErrorCodes.FileNotFound, "У голоса нет записей");
        return AudioEditCallResult<AudioVoiceUse>.Ok(new AudioVoiceUse(slug, sample, manifest.Transcript, null, null, ids));
    }

    // Состояние клона MiniMax голоса; null — голоса нет или отказ области
    public string? MiniMaxState(AudioEditScope scope, string slug) =>
        RootOf(scope, out _) is { } root && VoiceStore.Get(root, slug) is { } m ? VoiceProviders.MiniMaxState(m.Providers, Now) : null;

    // Привязки, созданные запуском, — в кеш голоса
    public void Remember(AudioEditScope scope, string slug, IReadOnlyList<AudioVoiceCacheEntry> entries) =>
        UpdateProviders(scope, slug, (p, now) =>
        {
            foreach (var e in entries)
            {
                switch (e.Provider)
                {
                    case VoiceProviders.Higgsfield: VoiceProviders.SetHiggsfield(p, e.Id, now); break;
                    case VoiceProviders.HiggsfieldMedia: VoiceProviders.SetHiggsfieldMedia(p, e.Id, now); break;
                    case VoiceProviders.MiniMax: VoiceProviders.SetMiniMax(p, e.Id, now); break;
                    case VoiceProviders.FalQwen: VoiceProviders.SetFalQwen(p, e.Id, now); break;
                }
            }
        });

    // Записать кеш поставщика (созданный клон, элемент, эмбеддинг). null — голоса нет или отказ области
    public VoiceManifest? UpdateProviders(AudioEditScope scope, string slug, Action<JsonObject, DateTime> change) =>
        RootOf(scope, out _) is { } root ? VoiceStore.UpdateProviders(root, slug, p => change(p, Now)) : null;
}
