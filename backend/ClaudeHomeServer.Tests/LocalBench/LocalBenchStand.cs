using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Стенд локальной модели для замеров мест каталога (Батарея II, ось A).
///
/// Ходит в движок ПРОДУКТОВЫМ клиентом <see cref="LlamaServerClient"/>, а не своим HTTP:
/// поверх запроса лежат обрезка промпта под бюджет профиля, снятие блока размышлений и
/// сборка response_format — своя копия транспорта разошлась бы с продуктом ровно так же,
/// как расходится копия промпта (путь B из спецификации Батареи II).
///
/// Наблюдаемости, которой у клиента нет наружу (finish_reason и usage уходят внутрь
/// RecordSpend и до вызывающего не доезжают), добирается перехватом HTTP-ответа:
/// <see cref="ObservingHandler"/> читает тело, достаёт причину остановки и счётчики
/// токенов и кладёт тело обратно нетронутым. Так обрыв вывода по потолку NumPredict —
/// факт от движка, а не догадка по виду текста.
///
/// Адрес и модель — из конфигурации харнесса (переменные окружения LOCALBENCH_BASEURL /
/// LOCALBENCH_MODEL), дефолт — стенд из docs/research/local-vllm-provider.md.
/// </summary>
public sealed class LocalBenchStand
{
    public const string DefaultBaseUrl = "http://127.0.0.1:18020";
    public const string DefaultModel = "qwen3.8-27b";

    public string BaseUrl { get; }
    public string Model { get; }

    private readonly ObservationSink _sink = new();

    public LocalBenchStand(string? baseUrl = null, string? model = null)
    {
        BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("LOCALBENCH_BASEURL") ?? DefaultBaseUrl)
            .TrimEnd('/');
        Model = model ?? Environment.GetEnvironmentVariable("LOCALBENCH_MODEL") ?? DefaultModel;
    }

    /// <summary>Поднят ли стенд. Не поднят — замер выходит без падения (в CI движка нет).</summary>
    public async Task<bool> AliveAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{BaseUrl}/v1/models");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Продуктовый клиент локального движка, собранный теми же ключами конфигурации
    /// (LocalLlm:*), какими его собирает DI продукта.
    /// </summary>
    public LlamaServerClient BuildClient()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalLlm:Provider"] = "llama-server",
            ["LocalLlm:BaseUrl"] = BaseUrl,
            ["LocalLlm:Model"] = Model,
            ["LocalLlm:TextModel"] = Model,
            ["LocalLlm:DisableThinking"] = "true",
        }).Build();
        return new LlamaServerClient(new ObservingHttpFactory(_sink), config,
            NullLogger<LlamaServerClient>.Instance);
    }

    /// <summary>Снимок последнего наблюдённого ответа движка. null — ответа ещё не было.</summary>
    public ObservedCall? LastCall => _sink.Last;

    /// <summary>Забыть наблюдённое — вызывается перед каждым кейсом.</summary>
    public void ResetObserved() => _sink.Reset();

    // Общий на весь стенд приёмник наблюдений: клиенты и обработчики живут по вызову,
    // а сводка нужна поверх них.
    private sealed class ObservationSink
    {
        private readonly Lock _sync = new();
        private ObservedCall? _last;

        public ObservedCall? Last { get { lock (_sync) return _last; } }
        public void Reset() { lock (_sync) _last = null; }
        public void Record(ObservedCall call) { lock (_sync) _last = call; }
    }

    private sealed class ObservingHttpFactory(ObservationSink sink) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ObservingHandler(sink) { InnerHandler = new HttpClientHandler() });
    }

    private sealed class ObservingHandler(ObservationSink sink) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var resp = await base.SendAsync(request, ct);
            if (resp.Content is null) return resp;

            // Читаем тело целиком и кладём обратно: продуктовый клиент разберёт его сам,
            // а мы к этому моменту уже знаем причину остановки и usage.
            var body = await resp.Content.ReadAsStringAsync(ct);
            sink.Record(Parse(resp, body));
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "application/json";
            resp.Content.Dispose();
            resp.Content = new StringContent(body, Encoding.UTF8, mediaType);
            return resp;
        }

        private static ObservedCall Parse(HttpResponseMessage resp, string body)
        {
            string? finishReason = null;
            int promptTokens = 0, completionTokens = 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("finish_reason", out var fr)
                    && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString();
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv))
                        promptTokens = pv;
                    if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv))
                        completionTokens = cv;
                }
            }
            catch (JsonException) { /* не-JSON ответ движка: причина остановки неизвестна */ }

            return new ObservedCall((int)resp.StatusCode, finishReason, promptTokens, completionTokens);
        }
    }
}

/// <summary>
/// Что движок сказал о последнем вызове: код ответа, причина остановки и токены.
/// FinishReason = "length" — вывод оборван потолком NumPredict профиля.
/// </summary>
public sealed record ObservedCall(int StatusCode, string? FinishReason, int PromptTokens, int CompletionTokens)
{
    public bool Truncated => string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}
