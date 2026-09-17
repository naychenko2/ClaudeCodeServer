using System.Diagnostics;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Живой замер скорости локального стенда ЧЕРЕЗ ОБВЯЗКУ ПРОДУКТА, а не через прямой
/// HTTP-запрос: вызовы идут тем же <see cref="LlamaServerClient"/> и теми же профилями
/// (<see cref="LocalActionCatalog.ProfileDefaults"/>), какими ходят фоновые one-shot
/// действия и голосовой разговор. Числа прямого запроса к эндпоинту о продукте не
/// говорят: поверх лежат обрезка промпта по бюджету профиля, потолок вывода
/// (numPredict), таймаут маршрута и склейка потока по границе предложения
/// (StreamSentenceBuffer) — именно она определяет, когда в разговоре начинается озвучка.
///
/// ТЕСТ НЕ УТВЕРЖДАЕТ ПОРОГОВ СКОРОСТИ. Абсолютные числа стенда плавают от нагрева карт
/// и режима контейнера (§7в docs/research/local-vllm-provider.md: разброс 7–11% между
/// запусками, дрейф вниз по мере нагрева), а на машине CI локального движка нет вовсе.
/// Утверждается только работоспособность маршрута; числа печатаются в вывод теста и
/// переносятся в документ замеров руками.
///
/// Эндпоинт не поднят — замер выходит без падения (в CI и на машине без стенда это норма).
/// Прогон: dotnet test --filter "FullyQualifiedName~LocalModelSpeedLive".
/// </summary>
[Trait("Category", "Slow")]
public class LocalModelSpeedLiveTests(ITestOutputHelper output)
{
    private sealed class RealHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private const string BaseUrl = "http://127.0.0.1:18020";
    private const string Model = "qwen3.8-27b";

    // Клиент собирается теми же ключами конфигурации, что и в продукте (LocalLlm:*).
    private static LlamaServerClient BuildClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalLlm:Provider"] = "llama-server",
            ["LocalLlm:BaseUrl"] = BaseUrl,
            ["LocalLlm:Model"] = Model,
            ["LocalLlm:TextModel"] = Model,
            ["LocalLlm:DisableThinking"] = "true",
        }).Build();
        return new LlamaServerClient(new RealHttpFactory(), config,
            NullLogger<LlamaServerClient>.Instance);
    }

    private static async Task<bool> AliveAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{BaseUrl}/v1/models");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // Один замер: полное время хода, время до ПЕРВОГО КУСКА в ленте (у нас это первое
    // законченное предложение — так работает StreamSentenceBuffer, и ровно этот момент
    // слышит пользователь голосового разговора) и токены из usage.
    private sealed record Shot(double TotalMs, double FirstChunkMs, int PromptTokens, int OutputTokens)
    {
        // Декод считаем ПОСЛЕ первого куска: до него время съедает prefill, и общая
        // «скорость хода» на длинном промпте занижала бы генерацию в разы.
        public double DecodeTokPerSec => OutputTokens > 0 && TotalMs - FirstChunkMs > 1
            ? OutputTokens / ((TotalMs - FirstChunkMs) / 1000.0) : 0;
        public double PrefillTokPerSec => PromptTokens > 0 && FirstChunkMs > 0
            ? PromptTokens / (FirstChunkMs / 1000.0) : 0;
    }

    private static async Task<Shot> MeasureAsync(LlamaServerClient client,
        IReadOnlyList<ChatMsg> messages, int numPredict, int numCtx, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        double firstMs = 0;
        var result = await client.ChatTurnAsync(messages, Model,
            TimeSpan.FromMilliseconds(timeoutMs), numPredict, numCtx, ownerId: null,
            onDelta: _ =>
            {
                if (firstMs == 0) firstMs = sw.Elapsed.TotalMilliseconds;
                return Task.CompletedTask;
            });
        sw.Stop();
        return new Shot(sw.Elapsed.TotalMilliseconds, firstMs,
            result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
    }

    // Промпт заданного размера в токенах (грубо, по оценке обвязки: 3 символа на токен —
    // та же константа, по которой клиент считает бюджет обрезки).
    private static string FillerPrompt(int approxTokens)
    {
        const string unit = "Строка технического журнала: сервис обработал запрос и записал результат. ";
        var need = approxTokens * 3;
        var sb = new System.Text.StringBuilder(need + unit.Length);
        while (sb.Length < need) sb.Append(unit);
        return sb.ToString(0, need);
    }

    [Fact]
    public async Task Замер_профилей_фоновых_действий()
    {
        if (!await AliveAsync()) { output.WriteLine("Локальный стенд не поднят — замер пропущен"); return; }

        var client = BuildClient();
        Assert.True(client.Enabled);
        await client.WarmUpAsync(Model);

        foreach (var (profile, spec) in LocalActionCatalog.ProfileDefaults)
        {
            // Промпт под завязку профиля: столько, сколько место реально отдаёт модели
            // после обрезки по бюджету (numCtx - numPredict).
            var prompt = FillerPrompt(Math.Max(64, spec.NumCtx - spec.NumPredict - 128));
            // Ответ просим развёрнутый: на ответе в одно предложение декод не измерить —
            // всё время хода занимает prefill, и цифра генерации выходит бессмысленной.
            var messages = new List<ChatMsg>
            {
                new("system", "Ты — фоновый обработчик. Отвечай по-русски."),
                new("user", prompt + "\n\nОпиши подробно, о чём этот журнал и что в нём "
                    + "повторяется. Пиши развёрнуто, не меньше 15 предложений."),
            };
            var shot = await MeasureAsync(client, messages, spec.NumPredict, spec.NumCtx, spec.TimeoutMs);
            output.WriteLine($"[{profile}] numCtx={spec.NumCtx} numPredict={spec.NumPredict} "
                + $"таймаут={spec.TimeoutMs}мс -> всего {shot.TotalMs:F0} мс, первый кусок {shot.FirstChunkMs:F0} мс, "
                + $"вход {shot.PromptTokens} ток (prefill {shot.PrefillTokPerSec:F0} ток/с), "
                + $"выход {shot.OutputTokens} ток (декод {shot.DecodeTokPerSec:F0} ток/с)");
        }
    }

    [Fact]
    public async Task Замер_большого_контекста()
    {
        if (!await AliveAsync()) { output.WriteLine("Локальный стенд не поднят — замер пропущен"); return; }

        var client = BuildClient();
        await client.WarmUpAsync(Model);

        // 262 144 — окно стенда после перехода на TP=2. Проверяем, что обвязка доносит
        // до модели промпты, которые прежнее каталожное окно 98 304 обрезало бы.
        foreach (var approx in new[] { 32_768, 65_536, 131_072 })
        {
            var messages = new List<ChatMsg>
            {
                new("system", "Отвечай по-русски."),
                new("user", FillerPrompt(approx) + "\n\nОпиши, что это за журнал и сколько в нём "
                    + "примерно строк. Пиши развёрнуто, не меньше 15 предложений."),
            };
            var shot = await MeasureAsync(client, messages, 512, 262_144, 900_000);
            output.WriteLine($"[контекст ~{approx}] всего {shot.TotalMs:F0} мс, первый кусок {shot.FirstChunkMs:F0} мс, "
                + $"вход {shot.PromptTokens} ток (prefill {shot.PrefillTokPerSec:F0} ток/с), "
                + $"выход {shot.OutputTokens} ток (декод {shot.DecodeTokPerSec:F0} ток/с)");
        }
    }

    [Fact]
    public async Task Замер_параллельных_ходов()
    {
        if (!await AliveAsync()) { output.WriteLine("Локальный стенд не поднят — замер пропущен"); return; }

        var client = BuildClient();
        await client.WarmUpAsync(Model);

        // Сколько одновременных ходов держит стенд: столько же параллельных фоновых
        // действий и сабагентов проект может отправить на локаль, не деградируя.
        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            var sw = Stopwatch.StartNew();
            var shots = await Task.WhenAll(Enumerable.Range(0, degree).Select(async i =>
            {
                var messages = new List<ChatMsg>
                {
                    new("system", "Отвечай по-русски."),
                    new("user", FillerPrompt(4096) + $"\n\nЗапрос {i}: перескажи смысл журнала "
                        + "развёрнуто, не меньше 20 предложений."),
                };
                return await MeasureAsync(client, messages, 512, 262_144, 300_000);
            }));
            sw.Stop();
            var outTokens = shots.Sum(s => s.OutputTokens);
            var perThread = shots.Average(s => s.DecodeTokPerSec);
            output.WriteLine($"[параллельно {degree}] стена {sw.Elapsed.TotalMilliseconds:F0} мс, "
                + $"выход суммарно {outTokens} ток -> {outTokens / sw.Elapsed.TotalSeconds:F0} ток/с суммарно, "
                + $"на поток в среднем {perThread:F0} ток/с, "
                + $"первый кусок медиана {Median(shots.Select(s => s.FirstChunkMs)):F0} мс");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
