using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Котировка драйвера (ADR-017, раздел 4): оценка в единицах поставщика — $ у fal,
// кредиты у Higgsfield — уже умноженная на число вариантов. Драйвер без этого шва
// котируется по PriceHint модели.
public interface IImageEditQuoter
{
    // Бросает ImageEditProviderUnavailableException, если поставщик отказал в доступе:
    // котировка тогда отвечает 409, а не цифрой
    Task<ImageEditEstimateDto> EstimateAsync(ImageEditModelInfo model, ImageEditQuoteRequest request, CancellationToken ct);

    // Ожидаемая длительность для процентов на фронте; null — неизвестно
    int? ExpectedSeconds(ImageEditModelInfo model);
}

public sealed class ImageEditProviderUnavailableException(string message) : Exception(message);

public static class ImageEditEstimates
{
    public static ImageEditEstimateDto Unknown(string unit) =>
        new(null, unit, true, ImageEditEstimateSources.Unknown);

    // Ориентир каталога: «за картинку» и «за запуск» умножаются на число вариантов,
    // мегапиксели — если известен размер. Прочее — «цена станет известна после запуска».
    public static ImageEditEstimateDto FromHint(ImageEditModelInfo model, ImageEditQuoteRequest request, string unit)
    {
        if (model.PriceHint is not { } hint) return Unknown(unit);
        var amount = PerUnit(hint.Per, hint.Amount, request);
        return amount is null ? Unknown(unit) : new(amount, unit, true, ImageEditEstimateSources.Catalog);
    }

    // Сумма по цене за единицу поставщика; null — единица без формулы
    public static double? PerUnit(string per, double unitPrice, ImageEditQuoteRequest request)
    {
        var count = Math.Max(1, request.Count);
        switch (per.Trim().ToLowerInvariant())
        {
            case "image" or "images" or "generation" or "generations" or "request" or "requests":
                return Math.Round(unitPrice * count, 6);
            case "megapixel" or "megapixels":
                if (request.Width is not > 0 || request.Height is not > 0) return null;
                var mp = request.Width.Value * (double)request.Height.Value / 1_000_000;
                return Math.Round(unitPrice * Math.Max(mp, 1) * count, 6);
            default:
                return null;
        }
    }
}
