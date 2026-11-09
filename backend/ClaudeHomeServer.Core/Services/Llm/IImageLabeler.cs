namespace ClaudeHomeServer.Services.Llm;

// Шов разметки картинок моделью со зрением (ADR-027 §4). ImageEditor не ссылается на
// ClaudeHomeServer.Llm, а текстовые раннеры картинок не принимают — этот шов единственная
// точка, через которую вертикаль узнаёт маршрут места разметки и отправляет фото на модель.
public interface IImageLabeler
{
    // Чем размечает место для этого владельца. Второго чтения стора назначений нет.
    ImageLabelRoute RouteOf(string actionKey, string ownerId);

    // Только Kind = Vision; FacePose сюда не приходит (его ведёт ILocalImageMedia).
    // Отказ — Failure в результате, а не исключение: разметка фоновая, у неё своя деградация.
    Task<ImageLabelResult> LabelAsync(ImageLabelRequest request, CancellationToken ct);
}

public enum ImageLabelRouteKind { FacePose, Vision, None }

// Explicit — маршрут выбрал админ (подменять его нельзя); Model — модель, которой пойдёт вызов.
public sealed record ImageLabelRoute(ImageLabelRouteKind Kind, bool Explicit, string? Model);

public sealed record ImageLabelInput(byte[] Bytes, string ContentType);

public sealed record ImageLabelRequest(
    string ActionKey, string OwnerId, string Prompt, IReadOnlyList<ImageLabelInput> Images);

public enum ImageLabelFailure { NoVision, Unavailable, Refused, Timeout, Failed }

public sealed record ImageLabelResult(string? Text, ImageLabelFailure? Failure)
{
    public static ImageLabelResult Ok(string text) => new(text, null);
    public static ImageLabelResult Fail(ImageLabelFailure failure) => new(null, failure);
}
