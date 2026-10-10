using ClaudeHomeServer.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Llm;

// Реализация шва IImageLabeler (ADR-027 §4): one-shot claude CLI с image-блоками. Маршрут
// места берём тем же роутером, что и текстовые действия; слот и пресет разворачиваем в
// модель владельца. Модель без зрения — отказ, а не ответ: слепая модель «размечает» фото
// домыслом (проверено на GLM), поэтому слот tier:*, развернувшийся в такую модель, ловится
// здесь же, в рантайме.
public sealed class ImageLabeler(
    LocalActionRouter router, LlmProviderRegistry providers, OneShotClaudeRunner runner,
    ILogger<ImageLabeler> log, ITierModelResolver? appSettings = null,
    UserModelTierResolver? userTiers = null, ModelAssignmentResolver? assignment = null)
    : IImageLabeler
{
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

        var model = route.Kind == RouteKind.Model ? route.Model : SlotModel(route, actionKey, ownerId);
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
    // модель той же точкой, что и у текстового раннера. Пусто — null (дефолт CLI).
    private string? SlotModel(ActionRoute route, string actionKey, string ownerId)
    {
        var tier = route.Tier ?? ModelTier.Medium;
        var slot = userTiers?.ModelFor(tier, ownerId) ?? appSettings?.TierModel(tier);
        if (string.IsNullOrWhiteSpace(slot)) return null;
        if (assignment is null || !IsMarker(slot)) return slot;
        var expanded = assignment.ExpandModelChain(slot, ownerId).FirstOrDefault();
        if (expanded is null)
            log.LogWarning("image-labeler: слот {Slot} места {Action} развернулся пусто — идём на дефолт CLI",
                slot, actionKey);
        return expanded;
    }

    private static bool IsMarker(string value) =>
        LocalActionOverridesStore.IsPresetRoute(value) || LocalActionOverridesStore.ParseTierRoute(value) is not null;

    public async Task<ImageLabelResult> LabelAsync(ImageLabelRequest request, CancellationToken ct)
    {
        if (request.Images.Count == 0)
            return ImageLabelResult.Fail(ImageLabelFailure.Failed);

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
            var timeout = TimeSpan.FromMilliseconds(router.CloudTimeoutMsFor(request.ActionKey));
            var result = await runner.RunWithImagesAsync(request.Prompt, request.Images,
                route.Model, timeout, ct, request.OwnerId, request.ActionKey);
            // Пустой ответ — модель отказалась описывать людей; вызывающий оставит unknown
            return string.IsNullOrWhiteSpace(result.Text)
                ? ImageLabelResult.Fail(ImageLabelFailure.Refused)
                : ImageLabelResult.Ok(result.Text);
        }
        catch (LlmTimeoutException ex)
        {
            if (ct.IsCancellationRequested) throw;
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
