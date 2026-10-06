using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.ImageEditor.ChatContext;

// Строки «Чем» для котировки картинки по операции (ADR-023 §Д2.1, решение Р2): «Авто» сверху, затем
// своя видеокарта и облако, цена полями, серая строка с причиной. Серверный двойник фронтового
// executorRows.ts: источник строк теперь один, строка контекста и панель читают ответ quote.
// Id строки — «поставщик|модель» (в id моделей бывают слеши), «Авто» — «auto»; так же их читает фронт.
public static class ImageExecutorRows
{
    public const string AutoId = "auto";

    public static IReadOnlyList<ExecutorRowDto> Build(
        ImageEditCatalogDto catalog, IEnumerable<IImageEditor> editors, ImageEditOp op, bool hasImage, bool hasMask)
    {
        var rows = new List<ExecutorRowDto>();
        var admin = catalog.Providers.FirstOrDefault(p => p.Key == catalog.Default.Provider);
        if (admin is not null)
        {
            var adminModel = catalog.Default.Model == ImageEditCatalog.AutoModelId
                ? null
                : admin.Models.FirstOrDefault(m => m.Id == catalog.Default.Model);
            var now = AutoNow(admin, adminModel);
            var price = Price(admin, adminModel, Eta(editors, admin, adminModel));
            rows.Add(new ExecutorRowDto(AutoId, "auto", ImageEditCatalog.AutoModelLabel,
                now.Length > 0 ? $"сейчас: {now}" : "как в настройках",
                price.Text, price.Free, price.Amount, price.Unit, price.EtaSeconds,
                Badges(admin)));
        }

        // Сначала своя видеокарта, потом облако; внутри — порядок каталога
        var ordered = catalog.Providers.Where(OnOwnGpu).Concat(catalog.Providers.Where(p => !OnOwnGpu(p)));
        foreach (var pv in ordered)
        foreach (var m in pv.Models)
        {
            var isAuto = m.Id == ImageEditCatalog.AutoModelId;
            var price = Price(pv, isAuto ? null : m, isAuto ? null : Eta(editors, pv, m));
            var why = BlockReason(m, op, hasImage, hasMask);
            rows.Add(new ExecutorRowDto(
                $"{pv.Key}|{m.Id}",
                OnOwnGpu(pv) ? "local" : "cloud",
                Name(pv, m),
                isAuto ? "подберём модель под задачу" : null,
                price.Text, price.Free, price.Amount, price.Unit, price.EtaSeconds,
                Badges(pv),
                Disabled: why.Length > 0,
                Reason: why.Length > 0 ? why : null));
        }
        return rows;
    }

    // Умеет ли своя видеокарта эту операцию (хоть одна её модель без причины отказа): «Авто» при
    // local-media-default уходит в облако только когда локальной не по силам
    public static bool LocalCan(ImageEditCatalogDto catalog, ImageEditOp op, bool hasImage, bool hasMask) =>
        catalog.Providers.Where(p => OnOwnGpu(p) && p.Available)
            .SelectMany(p => p.Models)
            .Any(m => m.Id != ImageEditCatalog.AutoModelId && BlockReason(m, op, hasImage, hasMask).Length == 0);

    private static bool OnOwnGpu(ImageEditProviderDto pv) => pv.PriceUnit == ImageEditPriceUnits.Free;

    private static string Name(ImageEditProviderDto pv, ImageEditModelDto m) =>
        m.Id == ImageEditCatalog.AutoModelId ? $"{pv.Label} · {ImageEditCatalog.AutoModelLabel}"
        : OnOwnGpu(pv) ? m.Label
        : $"{pv.Label} · {m.Label}";

    // «локально · Qwen-Image 2.1», «fal · FLUX Kontext»
    private static string AutoNow(ImageEditProviderDto pv, ImageEditModelDto? model) =>
        string.Join(" · ", new[] { OnOwnGpu(pv) ? "локально" : pv.Label, model?.Label }.Where(s => !string.IsNullOrEmpty(s)));

    private static IReadOnlyList<ExecutorBadgeDto>? Badges(ImageEditProviderDto pv) =>
        pv.Available ? null : [new ExecutorBadgeDto("не отвечает", "warn")];

    // Время запуска у локальных: таблица замеров драйвера; у облака его нет
    private static int? Eta(IEnumerable<IImageEditor> editors, ImageEditProviderDto pv, ImageEditModelDto? model)
    {
        if (!OnOwnGpu(pv) || model is null || model.Id == ImageEditCatalog.AutoModelId) return null;
        try
        {
            var editor = editors.FirstOrDefault(e => e.Key == pv.Key);
            var info = editor?.Models.FirstOrDefault(x => x.Id == model.Id);
            return editor is IImageEditQuoter quoter && info is not null ? quoter.ExpectedSeconds(info) : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct RowPrice(string Text, bool Free, double? Amount, string? Unit, int? EtaSeconds);

    // Цена за единицу из ориентира каталога; точная сумма запуска — в estimate котировки
    private static RowPrice Price(ImageEditProviderDto pv, ImageEditModelDto? model, int? eta)
    {
        if (OnOwnGpu(pv))
            return new RowPrice(eta is { } s ? $"бесплатно · ~{s} с" : "бесплатно", true, null, ImageEditPriceUnits.Free, eta);
        if (model?.PriceHint is { } hint)
        {
            if (hint.Unit == ImageEditPriceUnits.Free)
                return new RowPrice("бесплатно", true, null, ImageEditPriceUnits.Free, null);
            return new RowPrice(ContextPriceText.Format(hint.Amount, hint.Unit, hint.Per), false, hint.Amount, hint.Unit, null);
        }
        var text = pv.PriceUnit switch
        {
            ImageEditPriceUnits.Usd => "$ за картинку",
            ImageEditPriceUnits.Credits => "кредиты",
            _ => "",
        };
        return new RowPrice(text, false, null, pv.PriceUnit, null);
    }

    // Почему модель не возьмёт задачу: тексты как у фронта (format.ts modelBlockReason)
    private static string BlockReason(ImageEditModelDto m, ImageEditOp op, bool hasImage, bool hasMask)
    {
        if (m.Id == ImageEditCatalog.AutoModelId || m.Caps is not { } caps) return "";
        var ops = caps.Ops;
        if (ops.Count > 0 && ops.All(o => o == ImageEditOp.EnhanceFaces) && op != ImageEditOp.EnhanceFaces)
            return "Умеет только «Улучшить лица» — для этой задачи не годится";
        var fromScratch = op == ImageEditOp.Generate;
        var image = !fromScratch && hasImage;
        if (!image && !fromScratch && !ops.Contains(ImageEditOp.Generate))
            return "Только правит готовую картинку — сначала загрузите её";
        if (image && hasMask && caps.Mask == MaskSupport.None)
            return "Не правит по маске — сотрите кисть или возьмите другую модель";
        // Операцию считаем как котировка: правка с маской — инпейнт
        var want = op == ImageEditOp.Edit && hasMask ? ImageEditOp.Inpaint : op;
        if (ops.Count > 0 && !ops.Contains(want))
            return ops.Count == 1 ? OnlyOp(ops[0]) : "Не умеет эту операцию — возьмите другую модель";
        return "";
    }

    private static string OnlyOp(ImageEditOp only) => only switch
    {
        ImageEditOp.Generate => "Только рисует новую картинку — снимите выбор картинки или выберите «По тексту»",
        ImageEditOp.Inpaint => "Правит только по маске — отметьте место кистью",
        ImageEditOp.Outpaint => "Только дорисовывает за края — операция «Дорисовать за края»",
        ImageEditOp.RemoveBackground => "Только убирает фон — операция «Убрать фон»",
        ImageEditOp.Upscale => "Только улучшает качество — операция «Улучшить качество»",
        _ => "Не умеет эту операцию — возьмите другую модель",
    };
}
