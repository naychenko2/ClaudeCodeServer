using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// SP3 («учётка хода»): провайдер и модель записи расхода — из фактического маршрута прогона
// (ResultMessage.UpstreamProvider/UpstreamModel), а не из настроек чата и не из отчёта CLI.
public class SpendMappingTests
{
    private sealed class RecordingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];

        public void Record(SpendRecord record) => Records.Add(record);
    }

    // local-qwen — локальный движок (IsLocal), minimax — платный облачный с моделью MiniMax-M3.
    private static LlmProviderRegistry Providers() => new(TestConfig.Build(new Dictionary<string, string?>
    {
        ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
        ["LlmProviders:local-qwen:ApiBaseUrl"] = "http://127.0.0.1:8080/v1",
        ["LlmProviders:local-qwen:IsLocal"] = "true",
        ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
        ["LlmProviders:minimax:DisplayName"] = "MiniMax",
        ["LlmProviders:minimax:ApiBaseUrl"] = "https://api.minimax.io",
        ["LlmProviders:minimax:ApiKey"] = "test-key",
        ["LlmProviders:minimax:Models:0:Id"] = "MiniMax-M3",
    }));

    private static Session Chat(string provider, string? model) =>
        new() { Id = "s-1", ProjectId = "p-1", Provider = provider, Model = model };

    private static ResultMessage Result(
        string? usageModel = null, string? upstreamProvider = null, string? upstreamModel = null) =>
        new("success", 100, 1, new UsageInfo(1000, 200, 0, 0), 0.01,
            UsageModel: usageModel, UpstreamProvider: upstreamProvider, UpstreamModel: upstreamModel);

    private static SpendRecord RecordTurn(Session s, ResultMessage m)
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordTurnSpend(spend, Providers(), _ => "owner-1", null, s, m);
        return Assert.Single(spend.Records);
    }

    private sealed class Actors(string? actor) : ClaudeHomeServer.Services.Access.ITurnActorRegistry
    {
        public string? Actor(string sessionId, string ownerId) => actor;
    }

    private static SpendRecord RecordTurnWithActor(Session s, ResultMessage m, string? actor)
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordTurnSpend(spend, Providers(), _ => "owner-1", null, s, m, new Actors(actor));
        return Assert.Single(spend.Records);
    }

    // S2a-6b: запись расхода хода несёт актора — того, кто подал ход (реестр читается в
    // момент учёта). Ключ хранилища остаётся владельцем-арендатором: актор — разрез.
    [Fact]
    public void RecordTurnSpend_АкторХода_ПишетсяВЗапись()
    {
        var r = RecordTurnWithActor(Chat("claude", "opus"), Result(), actor: "u-2");
        r.ActorUserId.Should().Be("u-2");
        r.OwnerId.Should().Be("owner-1");
    }

    // Актор неизвестен (серверный/автоматический ход, реестр пуст) и реестра вовсе нет —
    // запись без актора: срез чтения отнесёт её владельцу (тот же порядок, что у К4).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RecordTurnSpend_АктораНет_ЗаписьБезАктора(string? actor)
    {
        RecordTurnWithActor(Chat("claude", "opus"), Result(), actor).ActorUserId.Should().BeNull();
        RecordTurn(Chat("claude", "opus"), Result()).ActorUserId.Should().BeNull();
    }

    // Чат настроен Claude, ход через шлюз ушёл в minimax: запись обязана брать маршрут —
    // модель шлюза важнее отчёта CLI (сессия ротацию шлюза не видит).
    [Fact]
    public void RecordTurnSpend_МаршрутШлюза_ВажнееНастроекЧатаИОтчётаCLI()
    {
        var r = RecordTurn(Chat("claude", "opus"),
            Result(usageModel: "opus", upstreamProvider: "minimax", upstreamModel: "MiniMax-M3"));
        r.Provider.Should().Be("minimax");
        r.Model.Should().Be("MiniMax-M3");
        r.Source.Should().Be(SpendSources.ChatTurn);
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Cheap);
    }

    // Ход на локальном движке: запись «free», даже когда настройки чата — Claude.
    [Fact]
    public void RecordTurnSpend_ЛокальныйАпстрим_ИсточникFree()
    {
        var r = RecordTurn(Chat("claude", "opus"),
            Result(usageModel: "opus", upstreamProvider: "local-qwen", upstreamModel: "qwen3.8-27b"));
        r.Provider.Should().Be("local-qwen");
        r.Model.Should().Be("qwen3.8-27b");
        r.Source.Should().Be(SpendSources.Free);
        // Класс и источник не разъезжаются: та же запись по правилу классов — «бесплатно»
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Free);
    }

    // Признак isLocal считается по тому же ключу, что пишется в запись: чат, настроенный на
    // локальный движок, но реально ходивший в облако, не должен записываться «бесплатным».
    [Fact]
    public void RecordTurnSpend_НастройкиЛокальныНоМаршрутОблачный_НеБесплатно()
    {
        var r = RecordTurn(Chat("local-qwen", "qwen3.8-27b"),
            Result(upstreamProvider: "minimax", upstreamModel: "MiniMax-M3"));
        r.Provider.Should().Be("minimax");
        r.Source.Should().Be(SpendSources.ChatTurn);
    }

    // result до SP1 (без upstream-полей): поведение прежнее — провайдер из настроек сессии
    // (sub-* нормализуется в claude), модель из modelUsage, фолбэк — модель сессии.
    [Theory]
    [InlineData("deepseek", "deepseek-v4-pro", null, "deepseek", "deepseek-v4-pro")]
    [InlineData("sub-work", "opus", null, "claude", "opus")]
    [InlineData("claude", "opus", "sonnet", "claude", "sonnet")]
    public void RecordTurnSpend_АпстримаНет_ПрежнееПоведение(
        string sessionProvider, string? sessionModel, string? usageModel, string wantProvider, string wantModel)
    {
        var r = RecordTurn(Chat(sessionProvider, sessionModel), Result(usageModel: usageModel));
        r.Provider.Should().Be(wantProvider);
        r.Model.Should().Be(wantModel);
        r.Source.Should().Be(SpendSources.ChatTurn);
    }

    // Провайдер не резолвится по модели из отчёта CLI: подписка claude-3 остаётся claude-3,
    // класс — «дорого» (префиксный резолв по UsageModel уводил бы реальные подписки в «дёшево»).
    [Fact]
    public void RecordTurnSpend_ПодпискаClaude_НеУводитсяПрефикснымРезолвом()
    {
        var r = RecordTurn(Chat("claude-3", "claude-opus-5-5"),
            Result(usageModel: "claude-opus-5-5", upstreamProvider: "claude-3"));
        r.Provider.Should().Be("claude-3");
        r.Model.Should().Be("claude-opus-5-5");
        r.Source.Should().Be(SpendSources.ChatTurn);
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Expensive);
    }

    // Подписка через шлюз (доработка SP2, блокер c6785c27): UpstreamOfRun отдаёт модель маршрута
    // только у Provider; у подписочного маршрута UpstreamModel = null (его Model — подсказка
    // `--model`). Модель записи — из modelUsage CLI (UsageModel): полная, с окном, а не алиас
    // или «default»; класс — «дорого». Без этой пары правка UpstreamOfRun здесь бы молчала.
    [Fact]
    public void RecordTurnSpend_ПодпискаЧерезШлюз_МодельИзUsageModel()
    {
        var r = RecordTurn(Chat("claude", "opus"),
            Result(usageModel: "claude-opus-5-5[1m]", upstreamProvider: "claude-9", upstreamModel: null));
        r.Provider.Should().Be("claude-9");
        r.Model.Should().Be("claude-opus-5-5[1m]");
        r.Source.Should().Be(SpendSources.ChatTurn);
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Expensive);
    }
}

// S-SPEND-3 (дефект f7e8c46d): выдержка request_id из tool_result внешнего MCP mcp.fal.ai.
// Когда результат содержит блок изображения, Claude CLI заменяет его маркером
// "[Image: source: …webp]" — контент перестаёт быть одним JSON-объектом, и прежний разбор
// (JsonDocument целиком) молча давал null: такие вызовы не учитывались вовсе (эмпирика
// 29.09–01.11: 4 запроса с маркером пропущены, fal_cost только у чистых JSON).
public class SpendMappingFalRequestIdTests
{
    private const string Rid = "01a19694-7252-73d0-ac86-2fc2bfe3f09d";

    // run_model без блока картинки: чистый JSON (этот путь учитывался и раньше).
    private static string CleanJson() =>
        $$"""
        {"request_id":"{{Rid}}","status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{Rid}}","images":[{"url":"https://fal.media/files/puma/x1.webp","mime_type":"image/webp"}]}
        """;

    // Тот же вызов с картинкой, образец формы из наблюдения разведки: JSON плюс маркер
    // строкой ниже.
    private static string MarkerAfterJson() =>
        $$"""
        {"request_id":"{{Rid}}","status":"COMPLETED","images":[{"url":"https://fal.media/files/puma/x1.webp","mime_type":"image/webp"}]}
        [Image: source: /tmp/claude-1000/mcp-images/8f2c1a.webp]
        """;

    // Маркер встал внутрь объекта (на место значения) — JSON рвётся в середине; fal-домен
    // остаётся в самом маркере (наблюдение разведки: source у маркера — URL .webp).
    private static string MarkerInsideJson() =>
        $$"""
        {"request_id":"{{Rid}}","status":"COMPLETED","images":[[Image: source: https://fal.media/files/puma/x1.webp]]}
        """;

    // Контент с маркером — прежний пропуск, теперь тот же id, что у чистого JSON.
    [Theory]
    [InlineData(false)] // маркер строкой после JSON
    [InlineData(true)]  // маркер внутри JSON
    public void TryExtractFalRequestId_КонтентСМаркеромИзображения_ДаётId(bool markerInside)
    {
        var content = markerInside ? MarkerInsideJson() : MarkerAfterJson();
        SpendMapping.TryExtractFalRequestId(content).Should().Be(Rid);
    }

    // Обратная совместимость: чистый JSON разбирается как раньше — тем же id.
    [Fact]
    public void TryExtractFalRequestId_ЧистыйJSON_ТотЖеId() =>
        SpendMapping.TryExtractFalRequestId(CleanJson()).Should().Be(Rid);

    // Цельный JSON-объект без top-level request_id — поведение прежнее: null (вложенный id
    // цельного объекта не выдерживаем, правка касалась только нецельного контента).
    [Fact]
    public void TryExtractFalRequestId_ЦельныйJSONБезTopLevelRequestId_Null() =>
        SpendMapping.TryExtractFalRequestId(
            $$"""{"data":{"request_id":"{{Rid}}"},"status_url":"https://queue.fal.run/fal-ai/flux/requests/{{Rid}}"}""").Should().BeNull();

    // Guard по fal-домену остался: чужой результат со своим request_id (без fal-домена) — null.
    [Theory]
    [InlineData("""{"request_id":"deadbeef-0000-0000-0000-000000000000","image_url":"https://cdn.example.com/x.webp"}""")]
    [InlineData("request_id: deadbeef-0000-0000-0000-000000000000\nresult: https://cdn.example.com/x.webp")]
    public void TryExtractFalRequestId_БезFalДомена_Null(string content) =>
        SpendMapping.TryExtractFalRequestId(content).Should().BeNull();

    // Fal-домен есть, request_id нет (или не строка) — тихо null, без исключения.
    [Theory]
    [InlineData("""{"status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/unknown"}""")]
    [InlineData("""{"request_id":null,"image_url":"https://fal.media/files/puma/x1.webp"}""")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtractFalRequestId_FalДоменБезId_ИлиПусто_Null(string? content) =>
        SpendMapping.TryExtractFalRequestId(content!).Should().BeNull();
}

// S-SPEND-8: генерации, которые зовёт сам CLI в ходе (внешний mcp.fal.ai, glif, higgsfield),
// через роутер картинок не идут — но и у них один признак агентской траты: Initiator = agent.
// «null + SessionId» — это второй способ сказать то же самое, и он не должен оставаться.
public class SpendMappingAgentGenerationTests
{
    private sealed class RecordingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    private static Session Chat() => new() { Id = "s-1", ProjectId = "p-1", Provider = "claude", Model = "opus" };

    private static SpendRecord One(ISpendCollector spend) => Assert.Single(((RecordingSpend)spend).Records);

    [Fact]
    public void RecordFalGeneration_ИнициаторАгента()
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordFalGeneration(spend, _ => "owner-1", null, Chat(),
            new FalCostMessage("req-1", "fal-ai/flux/dev", 0.0123));
        var r = One(spend);
        r.Source.Should().Be(SpendSources.Fal);
        r.Initiator.Should().Be(SpendInitiators.Agent);
    }

    [Fact]
    public void RecordGlifGeneration_ИнициаторАгента()
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordGlifGeneration(spend, _ => "owner-1", null, Chat(),
            new GlifCostMessage("job-1", "image", 1, 3, "glif-image"));
        var r = One(spend);
        r.Source.Should().Be(SpendSources.Glif);
        r.Initiator.Should().Be(SpendInitiators.Agent);
    }

    [Fact]
    public void RecordHiggsfieldGeneration_ИнициаторАгента()
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordHiggsfieldGeneration(spend, _ => "owner-1", null, Chat());
        var r = One(spend);
        r.Source.Should().Be(SpendSources.Higgsfield);
        r.Initiator.Should().Be(SpendInitiators.Agent);
    }
}
