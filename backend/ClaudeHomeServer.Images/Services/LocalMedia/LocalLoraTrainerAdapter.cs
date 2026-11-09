using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Шов обучения LoRA персонажа (ADR-026 §3) поверх LocalMediaService. Вся логика — в сервисе,
// здесь только перевод вызовов: потребитель шва (ImageEditor) о LocalMedia не знает
public sealed class LocalLoraTrainerAdapter(LocalMediaService media) : ILocalLoraTrainer
{
    public bool Enabled => media.LoraTrainEnabled;

    public LoraTrainBusy? Busy(string ownerId) => media.LoraBusy(ownerId);

    public Task<LoraTrainStartResult> StartAsync(LoraTrainStart request, CancellationToken ct) =>
        media.StartLoraTrainAsync(request, ct);

    public Task<LoraTrainState?> GetAsync(string ownerId, string jobId, CancellationToken ct) =>
        media.GetLoraTrainAsync(ownerId, jobId, ct);

    public IReadOnlyList<LoraTrainState> ListFor(string ownerId) => media.ListLoraTrain(ownerId);

    public IReadOnlyList<LoraTrainState> ListAll() => media.ListLoraTrain(null);

    public Task<LoraCancelResult> CancelAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct) =>
        media.CancelLoraTrainAsync(ownerId, jobId, asAdmin, ct);

    public Task<LoraDeleteState> DeleteRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct) =>
        media.DeleteLoraRunAsync(ownerId, projectId, slug, runId, ct);
}
