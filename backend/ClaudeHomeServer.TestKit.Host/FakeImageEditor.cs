using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Драйвер-заглушка: доступность и список моделей задаются тестом, запуск не нужен —
// до настоящих драйверов контроллер и каталог проверяются на контракте
internal sealed class FakeImageEditor(string key, bool enabled = true, bool throwOnEnabled = false,
    params ImageEditModelInfo[] models) : IImageEditor
{
    public string Key => key;
    public string Label => key.ToUpperInvariant();
    public string PriceUnit => key == "higgsfield" ? ImageEditPriceUnits.Credits : ImageEditPriceUnits.Usd;
    public bool Enabled => throwOnEnabled ? throw new InvalidOperationException("ключа нет") : enabled;
    public IReadOnlyList<ImageEditModelInfo> Models => models;

    public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => models.FirstOrDefault();

    public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

    public static ImageEditModelInfo Model(string id) =>
        new(id, id, new ImageEditCaps([ImageEditOp.Edit], MaskSupport.AsReference, 3, 4, true));
}
