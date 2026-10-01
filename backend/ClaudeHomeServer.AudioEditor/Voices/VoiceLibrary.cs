using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.AudioEditor.Voices;

// Библиотека «Голоса» области (ADR-021 §2): голоса живут только в серверном проекте. Личная область и
// локальный проект получают отказ ДО диска: у личной корня нет вовсе, у локального файлы на устройстве
// (ProjectCapabilityGuard, группа FileBound). Часы подменяемые — по ним считается признак «клон MiniMax
// протух».
public sealed class VoiceLibrary(TimeProvider? time = null)
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

    // Записать кеш поставщика (созданный клон, элемент, эмбеддинг). null — голоса нет или отказ области
    public VoiceManifest? UpdateProviders(AudioEditScope scope, string slug, Action<JsonObject, DateTime> change) =>
        RootOf(scope, out _) is { } root ? VoiceStore.UpdateProviders(root, slug, p => change(p, Now)) : null;
}
