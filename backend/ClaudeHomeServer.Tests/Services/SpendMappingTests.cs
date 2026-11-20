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
        string? usageModel = null, string? upstreamProvider = null, string? upstreamModel = null,
        long? apiMs = null) =>
        new("success", 100, 1, new UsageInfo(1000, 200, 0, 0), 0.01,
            UsageModel: usageModel, UpstreamProvider: upstreamProvider, UpstreamModel: upstreamModel,
            DurationApiMs: apiMs);

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

    // Инициатор берётся из параметра, который считает ядро (защёлка хода); актор реестра на него
    // не влияет. null («не назван») — только когда записи хода нет, и это не human и не agent.
    [Theory]
    [InlineData(SpendInitiators.Human, "u-2")]
    [InlineData(SpendInitiators.Agent, "u-2")]
    [InlineData(SpendInitiators.Agent, null)]
    [InlineData(null, "u-2")]
    public void RecordTurnSpend_ИнициаторИзПараметра_АкторНеВлияет(string? initiator, string? actor)
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordTurnSpend(spend, Providers(), _ => "owner-1", null, Chat("claude", "opus"), Result(),
            new Actors(actor), initiator);
        var r = Assert.Single(spend.Records);
        r.Initiator.Should().Be(initiator);
        r.ActorUserId.Should().Be(actor);
    }

    [Fact]
    public void RecordTurnSpend_ИнициаторНеПередан_НеНазван()
    {
        RecordTurnWithActor(Chat("claude", "opus"), Result(), actor: "u-2").Initiator.Should().BeNull();
    }

    // Инвариант разреза «ход»: запись расхода хода чата несёт SessionId — без него аналитика
    // теряет привязку записи к чату (проверка выпала при замене прежнего теста, хвост ревью
    // R-S-SPEND-ИНИЦ, п.1).
    [Fact]
    public void RecordTurnSpend_ЗаписьХода_НесётSessionId()
    {
        var r = RecordTurn(Chat("claude", "opus"), Result());
        r.SessionId.Should().Be("s-1");
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

    // Хвост ревью aa8af844, п.2: DurationApiMs — знаменатель сессионной скорости ответа
    // (SpendAnswerSpeedService): без строки в SpendMapping скорость на проде пропадает молча,
    // тесты зелёные. Запись обязана нести поле.
    [Fact]
    public void RecordTurnSpend_ВремяApi_ПишетсяВЗапись()
    {
        RecordTurn(Chat("claude", "opus"), Result(apiMs: 4200)).DurationApiMs.Should().Be(4200);
    }

    // Обратная сторона того же поля: CLI не прислал (сторонний поток) — запись без поля,
    // и в скорость такой ход не идёт (знаменатель не выдумывается — SpendAnswerSpeedTests).
    [Fact]
    public void RecordTurnSpend_ВремениApiНет_ЗаписьБезПоля()
    {
        RecordTurn(Chat("claude", "opus"), Result()).DurationApiMs.Should().BeNull();
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
// Пакетные submit_jobs/wait_for_jobs (живая сверка 04f65504, 17 запросов на 1,1084 $):
// верхнего request_id у них нет, id лежат в jobs[] — прежний разбор терял сразу все N
// запросов одного вызова.
public class SpendMappingFalRequestIdTests
{
    private const string Rid = "01a19694-7252-73d0-ac86-2fc2bfe3f09d";
    private const string RidA = "01a19694-7252-73d0-a000-000000000000000a";
    private const string RidB = "01a19694-7252-73d1-b000-000000000000000b";
    private const string RidC = "01a19694-7252-73d2-c000-000000000000000c";

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

    // Форма ответа пакетных submit_jobs/wait_for_jobs: верхнего request_id нет, id — в jobs[].
    private static string BatchJson(params string[] ids)
    {
        var jobs = string.Join(",", ids.Select(i =>
            $$"""{"request_id":"{{i}}","status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{i}}"}"""));
        return $$"""{"status":"COMPLETED","jobs":[{{jobs}}]}""";
    }

    // Тот же пакет с блоками картинок: маркеры строкой после JSON (по одному на блок).
    private static string BatchWithMarkersAfter(params string[] ids)
    {
        var markers = string.Join("\n", ids.Select(i => $"[Image: source: /tmp/claude-1000/mcp-images/{i}.webp]"));
        return BatchJson(ids) + "\n" + markers;
    }

    // Маркер встал вместо блока images ВНУТРИ каждого job — JSON рвётся в середине массива.
    private static string BatchWithMarkersInside(params string[] ids)
    {
        var jobs = string.Join(",", ids.Select(i =>
            $$"""{"request_id":"{{i}}","status":"COMPLETED","images":[[Image: source: https://fal.media/files/puma/{{i}}.webp]]}"""));
        return $$"""{"status":"COMPLETED","jobs":[{{jobs}}]}""";
    }

    // Контент с маркером — прежний пропуск, теперь тот же id, что у чистого JSON.
    [Theory]
    [InlineData(false)] // маркер строкой после JSON
    [InlineData(true)]  // маркер внутри JSON
    public void TryExtractFalRequestIds_КонтентСМаркеромИзображения_ДаётId(bool markerInside)
    {
        var content = markerInside ? MarkerInsideJson() : MarkerAfterJson();
        SpendMapping.TryExtractFalRequestIds(content).Should().Equal(Rid);
    }

    // Обратная совместимость: чистый JSON с top-level request_id разбирается как раньше —
    // тем же id, одним элементом списка.
    [Fact]
    public void TryExtractFalRequestIds_ЧистыйJSON_ТотЖеId() =>
        SpendMapping.TryExtractFalRequestIds(CleanJson()).Should().Equal(Rid);

    // Пакетный результат: каждый request_id из jobs[] — отдельный id учёта, в порядке
    // массива. Один вызов submit_jobs больше не теряет все N запросов.
    [Fact]
    public void TryExtractFalRequestIds_ПакетJobs_КаждыйIdИзМассива()
    {
        var ids = SpendMapping.TryExtractFalRequestIds(BatchJson(RidA, RidB, RidC));
        ids.Should().Equal(RidA, RidB, RidC);
        ids.Should().HaveCount(3, "число id равно числу запросов в пакете");
    }

    // Пакетный ответ с блоками картинок (wait_for_jobs с картинкой в каждом job): после
    // вырезания маркеров остаток — цельный JSON, и каждый id из jobs[] учтён. Прежний regex
    // по тексту давал ПЕРВОЕ вхождение: N−1 записей расхода терялись (хвост ревью
    // R-S-SPEND-ПАКЕТ, п.2).
    [Theory]
    [InlineData(false)] // маркеры строкой после JSON пакета
    [InlineData(true)]  // маркер встал вместо блока images внутри каждого job
    public void TryExtractFalRequestIds_ПакетСМаркерамиКартинок_КаждыйIdИзJobs(bool markerInside)
    {
        var content = markerInside ? BatchWithMarkersInside(RidA, RidB, RidC) : BatchWithMarkersAfter(RidA, RidB, RidC);
        var ids = SpendMapping.TryExtractFalRequestIds(content);
        ids.Should().Equal(RidA, RidB, RidC);
        ids.Should().HaveCount(3, "число id равно числу запросов в пакете");
    }

    // Чужой вложенный id не берётся и в маркерной форме: очищенный от маркера контент —
    // JSON-объект, и структурное правило остаётся (data.request_id — не форма fal).
    // Прежний путь давал здесь именно чужой id: этот тест держит инвариант на обеих формах.
    [Fact]
    public void TryExtractFalRequestIds_МаркерныйJSONСЧужимВложеннымId_Пусто()
    {
        var content = $$"""
            {"data":{"request_id":"{{Rid}}"},"status_url":"https://queue.fal.run/fal-ai/flux/requests/{{Rid}}"}
            [Image: source: /tmp/claude-1000/mcp-images/8f2c1a.webp]
            """;
        SpendMapping.TryExtractFalRequestIds(content).Should().BeEmpty();
    }

    // Id, названный и на верхнем уровне, и в jobs[] (одна и та же генерация в двух формах
    // ответа), считается один раз: одному сетевому циклу — одна запись расхода.
    [Fact]
    public void TryExtractFalRequestIds_ВерхнийIdПовторёнВJobs_ОдинРаз()
    {
        var content = $$"""{"request_id":"{{RidA}}","status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{RidA}}","jobs":[{"request_id":"{{RidA}}","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{RidA}}"},{"request_id":"{{RidB}}","response_url":"https://queue.fal.run/fal-ai/flux/requests/{{RidB}}"}]}""";
        SpendMapping.TryExtractFalRequestIds(content).Should().Equal(RidA, RidB);
    }

    // Чужой вложенный id (не структурная форма fal: ни верхний, ни jobs[].request_id) —
    // не берётся: прежний пропуск остаётся пропуском.
    [Fact]
    public void TryExtractFalRequestIds_ЦельныйJSONБезСтруктурныхId_Пусто() =>
        SpendMapping.TryExtractFalRequestIds(
            $$"""{"data":{"request_id":"{{Rid}}"},"status_url":"https://queue.fal.run/fal-ai/flux/requests/{{Rid}}"}""").Should().BeEmpty();

    // Пакет с нестроковыми/пустыми request_id и не-объектами в jobs[]: берутся только
    // строковые id структурной позиции, без исключения. Голый id строкой в массиве — не
    // элемент jobs[] структурной формы, он не берётся.
    [Theory]
    [InlineData("""{"status":"COMPLETED","jobs":[{"request_id":null},{"request_id":""},{"request_id":123},{"status":"QUEUED"},{"request_id":"01a19694-7252-73d0-a000-000000000000000a","response_url":"https://queue.fal.run/fal-ai/flux/requests/x"}]}""", RidA)]
    [InlineData("""{"status":"COMPLETED","jobs":["01a19694-7252-73d0-a000-000000000000000a",{"request_id":"01a19694-7252-73d1-b000-000000000000000b","response_url":"https://queue.fal.run/fal-ai/flux/requests/x"}]}""", RidB)]
    public void TryExtractFalRequestIds_ПакетСНестроковымиЭлементами_ТолькоСтроковыеId(string content, string onlyId) =>
        SpendMapping.TryExtractFalRequestIds(content).Should().Equal(onlyId);

    // Хвосты ревью R-S-SPEND-3 фиксируют намерение текстового пути (последней страховки).
    // П.2: класс символов id широкий намеренно — не-UUID id (например, "job_42-A") берётся;
    // чужой id безвреден: цена по нему не найдётся, в Spend ложной записи не будет.
    [Fact]
    public void TryExtractFalRequestIds_ТекстовыйПутьНеUuidId_Берётся() =>
        SpendMapping.TryExtractFalRequestIds(
            "fal.run ответ (не JSON): \"request_id\": \"job_42-A\" готово").Should().Equal("job_42-A");

    // П.3: в тексте, не являющемся JSON, берётся только ПЕРВОЕ вхождение — остальные
    // теряются осознанно (страховка, а не разбор пакета).
    [Fact]
    public void TryExtractFalRequestIds_ТекстовыйПутьНесколькоId_ТолькоПервое() =>
        SpendMapping.TryExtractFalRequestIds(
            $"fal.run: \"request_id\":\"{RidA}\" затем \"request_id\":\"{RidB}\"").Should().Equal(RidA);

    // П.1: цельный JSON с НЕ-объектовым корнем (массив) уходит в текстовый путь, а не в
    // структурный: берётся только первый id массива. Поведение зафиксировано как есть —
    // ответы fal всегда объекты, массивом на практике не приходят.
    [Fact]
    public void TryExtractFalRequestIds_МассивВКорне_ТекстовыйПутьПервыйId() =>
        SpendMapping.TryExtractFalRequestIds(
            $$"""[{"request_id":"{{RidA}}","response_url":"https://queue.fal.run/x"},{"request_id":"{{RidB}}"}]""")
            .Should().Equal(RidA);

    // Guard по fal-домену остался: чужой результат со своим request_id (без fal-домена) — пусто.
    [Theory]
    [InlineData("""{"request_id":"deadbeef-0000-0000-0000-000000000000","image_url":"https://cdn.example.com/x.webp"}""")]
    [InlineData("request_id: deadbeef-0000-0000-0000-000000000000\nresult: https://cdn.example.com/x.webp")]
    [InlineData("""{"status":"COMPLETED","jobs":[{"request_id":"deadbeef-0000-0000-0000-000000000000"}],"image_url":"https://cdn.example.com/x.webp"}""")]
    public void TryExtractFalRequestIds_БезFalДомена_Пусто(string content) =>
        SpendMapping.TryExtractFalRequestIds(content).Should().BeEmpty();

    // Fal-домен есть, request_id нет (или не строка) — тихо пусто, без исключения.
    [Theory]
    [InlineData("""{"status":"COMPLETED","response_url":"https://queue.fal.run/fal-ai/flux/requests/unknown"}""")]
    [InlineData("""{"request_id":null,"image_url":"https://fal.media/files/puma/x1.webp"}""")]
    [InlineData("""{"status":"COMPLETED","jobs":[]}""")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtractFalRequestIds_FalДоменБезId_ИлиПусто_Пусто(string? content) =>
        SpendMapping.TryExtractFalRequestIds(content!).Should().BeEmpty();
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
