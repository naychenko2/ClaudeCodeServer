using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ImageEditor;

// Категория неоднозначного запроса для «Авто» локальных моделей (Ш3): когда правила LocalImageRouting
// по словам ничего не нашли, короткий вопрос локальной LLM — «люди / товар / иллюстрация / надписи…».
// Котировка ждёт ответа не дольше потолка; не успел — выбор по правилам, а вызов дорабатывает в фоне и
// кладёт ответ в кэш: запуск по тому же тексту его уже застанет. Замер на наборе Ш1 — tools/image-route-classify
public sealed class ImageRouteClassifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    // Сколько вызов может дорабатывать в фоне после потолка котировки
    private static readonly TimeSpan BackgroundCeiling = TimeSpan.FromSeconds(20);
    private const int CacheLimit = 256;

    private const string GenInstruction =
        "Определи категорию запроса на генерацию картинки. Категории: " +
        "люди — главное в кадре человек или группа людей; " +
        "товар — предметная или рекламная съёмка одного продукта; " +
        "арт — рисунок, живопись, иллюстрация, аниме, 3D-графика или другая стилизация, не фото; " +
        "надписи — в кадре должен быть читаемый текст; " +
        "состав — важны точное число, цвета и расположение нескольких предметов; " +
        "пейзаж — природа, город или интерьер без главного героя. " +
        "Ответь JSON вида {\"category\":\"…\"}. Ничего, кроме JSON.";

    private const string EditInstruction =
        "Определи вид правки фотографии по запросу. Виды: " +
        "фон — заменить или изменить фон; одежда — переодеть человека; " +
        "добавить — добавить предмет или аксессуар; убрать — убрать предмет; " +
        "стиль — перерисовать всю картинку в другом стиле; текст — написать или заменить надпись. " +
        "Ответь JSON вида {\"category\":\"…\"}. Ничего, кроме JSON.";

    // Модель иногда отвечает по-английски — те же категории (видно в замере)
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["people"] = "люди", ["product"] = "товар", ["art"] = "арт", ["text"] = "текст", ["composition"] = "состав",
        ["landscape"] = "пейзаж", ["background"] = "фон", ["clothes"] = "одежда", ["clothing"] = "одежда",
        ["add"] = "добавить", ["remove"] = "убрать", ["style"] = "стиль",
    };

    private readonly ICheapTextRunner _runner;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new();

    public ImageRouteClassifier(ICheapTextRunner runner, TimeSpan? timeout = null)
    {
        _runner = runner;
        _timeout = timeout ?? DefaultTimeout;
    }

    // Категория (ключ LocalImageRouting.GenCategories / EditCategories) или null: нет ответа за потолок,
    // сбой или ответ вне словаря
    public async Task<string?> ClassifyAsync(ImageEditOp op, string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || op is not (ImageEditOp.Generate or ImageEditOp.Edit)) return null;
        // В LLM уходит то же начало запроса, что видят правила по словам
        var text = PromptHints.Clip(prompt.Trim())!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{op}\n{text}")));
        if (_cache.Count >= CacheLimit) _cache.Clear();
        var task = _cache.GetOrAdd(key, _ => AskAsync(op, text));
        // Сбой не кэшируем: он может быть временным. Снимаем именно эту задачу, а не новую по тому же тексту
        _ = task.ContinueWith(t => { if (t.Result is null) _cache.TryRemove(KeyValuePair.Create(key, t)); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(_timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // Вызов не привязан к токену котировки: брошенная котировка не обрывает ответ, который пригодится запуску
    private async Task<string?> AskAsync(ImageEditOp op, string prompt)
    {
        try
        {
            using var cts = new CancellationTokenSource(BackgroundCeiling);
            var instruction = op == ImageEditOp.Generate ? GenInstruction : EditInstruction;
            var raw = await _runner.RunLocalOrChosenAsync(LocalActionCatalog.ImageRouteClassify,
                $"{instruction}\n\nТЕКСТ ЗАПРОСА:\n{prompt}", jsonFormat: "json", ct: cts.Token)
                .WaitAsync(BackgroundCeiling);
            return ParseModelAnswer(op, raw);
        }
        catch (Exception)
        {
            // Сбой и таймаут — «нет ответа»: выбор по правилам
            return null;
        }
    }

    internal static string? ParseModelAnswer(ImageEditOp op, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("category", out var v) || v.ValueKind != JsonValueKind.String)
                return null;
            var value = v.GetString()!.Trim().ToLowerInvariant();
            value = Aliases.GetValueOrDefault(value, value);
            if (op == ImageEditOp.Generate && value == "текст") value = "надписи";
            var known = op == ImageEditOp.Generate ? LocalImageRouting.GenCategories : LocalImageRouting.EditCategories;
            return known.ContainsKey(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
