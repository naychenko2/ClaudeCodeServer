using ClaudeHomeServer.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Llm;

// Подмена запуска CLI — только для тестов; в DI не регистрируется, по умолчанию раннер
public delegate Task<OneShotResult> ImageRunFunc(string prompt, IReadOnlyList<ImageLabelInput> images,
    string? model, TimeSpan? timeout, CancellationToken ct, string? ownerId, string? label);

// Реализация шва IImageLabeler (ADR-027 §4): one-shot claude CLI с image-блоками. Маршрут
// места берём тем же роутером, что и текстовые действия; слот и пресет разворачиваем в
// модель владельца. Модель без зрения — отказ, а не ответ: слепая модель «размечает» фото
// домыслом (проверено на GLM), поэтому слот tier:*, развернувшийся в такую модель, ловится
// здесь же, в рантайме.
public sealed class ImageLabeler(
    LocalActionRouter router, LlmProviderRegistry providers, OneShotClaudeRunner runner,
    ILogger<ImageLabeler> log, ITierModelResolver? appSettings = null,
    UserModelTierResolver? userTiers = null, ModelAssignmentResolver? assignment = null,
    ImageRunFunc? run = null)
    : IImageLabeler
{
    // Потолки входа: ответ строит один CLI-вызов, лишние картинки только раздувают stdin
    public const int MaxImages = 10;
    public const long MaxTotalBytes = 30L * 1024 * 1024;
    private const int FallbackTimeoutMs = 120_000;

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp" };

    public ImageLabelRoute RouteOf(string actionKey, string ownerId)
    {
        // Место без картинок на входе этим швом не обслуживается
        if (LocalActionCatalog.Find(actionKey)?.Input != ActionInput.Images)
            return new ImageLabelRoute(ImageLabelRouteKind.None, false, null);

        var route = router.Resolve(actionKey);
        var explicitRoute = route.Source == RouteSource.Admin;
        switch (route.Kind)
        {
            case RouteKind.FacePose:
                return new ImageLabelRoute(ImageLabelRouteKind.FacePose, explicitRoute, null);
            case RouteKind.Local:
                // Локальная текстовая модель картинок не видит
                return new ImageLabelRoute(ImageLabelRouteKind.None, explicitRoute, null);
        }

        var (model, noRoute) = route.Kind == RouteKind.Model
            ? (route.Model, false)
            : SlotModel(route, actionKey, ownerId);
        // Маркер слота развернулся в пусто (все шаги пресета недоступны) — честный отказ
        // маршрута: разметить некем. Не Vision с null-моделью: та ушла бы в CLI как preset:…
        // (подстановка слота в OneShotClaudeRunner) и упала с unrecognized_model, а фото
        // висело бы в ожидании, а не получало noroute с повтором.
        if (noRoute)
            return new ImageLabelRoute(ImageLabelRouteKind.None, explicitRoute, null);
        if (model is not null && CloudCheapClient.IsDirectRoute(model))
            return new ImageLabelRoute(ImageLabelRouteKind.None, explicitRoute, model);

        // Пустая модель — дефолт CLI (слот «средняя»): зрение проверяем у неё
        var effective = model ?? userTiers?.ModelFor(ModelTier.Medium, ownerId)
            ?? appSettings?.TierModel(ModelTier.Medium);
        var blind = providers.ResolveByModel(effective) is { SupportsImages: false };
        return new ImageLabelRoute(
            blind ? ImageLabelRouteKind.None : ImageLabelRouteKind.Vision, explicitRoute, model);
    }

    // Слот → модель владельца; маркер слота (preset:/tier:*) разворачивается в конкретную
    // модель той же точкой, что и у текстового раннера. Пустой слот — (null, false): дефолт CLI.
    // Маркер, развернувшийся в пусто (все шаги пресета недоступны), — (null, true): честный
    // отказ маршрута (noroute), а не null, иначе preset:… ушёл бы в CLI как --model.
    private (string? Model, bool NoRoute) SlotModel(ActionRoute route, string actionKey, string ownerId)
    {
        var tier = route.Tier ?? ModelTier.Medium;
        var slot = userTiers?.ModelFor(tier, ownerId) ?? appSettings?.TierModel(tier);
        if (string.IsNullOrWhiteSpace(slot)) return (null, false);
        if (assignment is null || !IsMarker(slot)) return (slot, false);
        var expanded = assignment.ExpandModelChain(slot, ownerId).FirstOrDefault();
        if (expanded is null)
        {
            log.LogWarning("image-labeler: слот {Slot} места {Action} развернулся пусто — разметки некем (noroute)",
                slot, actionKey);
            return (null, true);
        }
        return (expanded, false);
    }

    private static bool IsMarker(string value) =>
        LocalActionOverridesStore.IsPresetRoute(value) || LocalActionOverridesStore.ParseTierRoute(value) is not null;

    public async Task<ImageLabelResult> LabelAsync(ImageLabelRequest request, CancellationToken ct)
    {
        if (request.Images.Count == 0 || request.Images.Count > MaxImages
            || request.Images.Sum(i => (long)i.Bytes.Length) > MaxTotalBytes
            || request.Images.Any(i => !AllowedContentTypes.Contains(i.ContentType ?? "")))
        {
            log.LogWarning("image-labeler: вход места {Action} вне лимитов — разметка отказана",
                request.ActionKey);
            return ImageLabelResult.Fail(ImageLabelFailure.Failed);
        }

        var route = RouteOf(request.ActionKey, request.OwnerId);
        if (route.Kind != ImageLabelRouteKind.Vision)
        {
            log.LogWarning(
                "image-labeler: место {Action} имеет маршрут {Kind} — модели со зрением нет, разметка отказана",
                request.ActionKey, route.Kind);
            return ImageLabelResult.Fail(route.Kind == ImageLabelRouteKind.FacePose
                ? ImageLabelFailure.Unavailable : ImageLabelFailure.NoVision);
        }

        try
        {
            var timeoutMs = router.CloudTimeoutMsFor(request.ActionKey);
            var timeout = TimeSpan.FromMilliseconds(timeoutMs > 0 ? timeoutMs : FallbackTimeoutMs);
            var result = await (run ?? runner.RunWithImagesAsync)(request.Prompt, request.Images,
                route.Model, timeout, ct, request.OwnerId, request.ActionKey);
            // Пустой ответ — модель отказалась описывать людей; вызывающий оставит unknown
            return string.IsNullOrWhiteSpace(result.Text)
                ? ImageLabelResult.Fail(ImageLabelFailure.Refused)
                : ImageLabelResult.Ok(result.Text);
        }
        catch (LlmTimeoutException ex)
        {
            // Отмена вызывающего — отмена, а не Timeout и не LlmTimeoutException наружу
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);
            log.LogWarning("image-labeler: {Action} — {Message}", request.ActionKey, ex.Message);
            return ImageLabelResult.Fail(ImageLabelFailure.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "image-labeler: вызов места {Action} упал", request.ActionKey);
            return ImageLabelResult.Fail(ImageLabelFailure.Failed);
        }
    }
}
