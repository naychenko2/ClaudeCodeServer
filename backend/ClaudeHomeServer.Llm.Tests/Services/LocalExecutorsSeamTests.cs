using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// W1a — Core-шов исполнителей ночи: три новых члена ICheapTextRunner с телом по умолчанию и
// флаг каталога ParallelExecutors. Реализации здесь нет (она — W1b): проверяется сам контракт.
//
// Первый блок — защита «десятков тестовых фейков и заглушки TestKit.Host»: фейк, который
// реализует ровно прежний облик интерфейса, компилируется (новые члены не абстрактные) и
// получает дефолты: пусто / false / null. Абстрактный метод сломал бы их всех разом, и правка
// каждого была бы подписанной работой этой волны. Вызов — через переменную интерфейса: метод с
// телом по умолчанию не достаётся из типа, который его не переопределил.
//
// Второй блок — флаг: true ровно у wiki-nightly. Ночь — единственное место, которое делит свою
// работу между движками (и единственное, что не уходит в облако — И3). Мутация «флаг у
// wiki-triage» краснит ровно тест флага.
public class LocalExecutorsSeamTests
{
    // Ровно прежний облик интерфейса до W1a: ни одного из трёх новых членов.
    private sealed class OldShapeRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "local";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult("ответ");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult<string?>("ответ");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(new OneShotResult("ответ", null, 0));
    }

    private static ICheapTextRunner OldShape() => new OldShapeRunner();

    [Fact]
    public void DefaultLocalExecutors_Empty()
    {
        // Шов, который про движках ничего не знает, не должен выдумывать исполнителя:
        // пустой список = «работу не начинаем», а не «идём куда-нибудь».
        Assert.Empty(OldShape().LocalExecutorsFor(LocalActionCatalog.WikiNightly));
    }

    [Fact]
    public async Task DefaultEngineReady_False()
    {
        // Ни «local», ни имя движка, ни неизвестное — всегда false: подмены «считаем готовым» нет.
        Assert.False(await OldShape().IsLocalEngineReadyAsync(LocalActionOverridesStore.LocalRoute,
            CancellationToken.None));
        Assert.False(await OldShape().IsLocalEngineReadyAsync("video", CancellationToken.None));
        Assert.False(await OldShape().IsLocalEngineReadyAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task DefaultRunOnLocalEngine_Null()
    {
        // null = отказ окна: вызывающий решает сам, подмены другим исполнителем у шва нет.
        var result = await OldShape().RunOnLocalEngineDetailedAsync("video",
            LocalActionCatalog.WikiNightly, "промпт", ownerId: "owner", jsonFormat: "json",
            CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void ParallelExecutors_OnlyWikiNightly()
    {
        var flagged = LocalActionCatalog.All.Where(a => a.ParallelExecutors).Select(a => a.Key).ToList();
        Assert.Equal(new[] { LocalActionCatalog.WikiNightly }, flagged);

        // Соседи по ночной работе остаются одиночными вызовами: ступень 2 отбора, судья,
        // разбор источника.
        Assert.False(LocalActionCatalog.Find(LocalActionCatalog.WikiTriage)!.ParallelExecutors);
        Assert.False(LocalActionCatalog.Find(LocalActionCatalog.WikiJudge)!.ParallelExecutors);
        Assert.False(LocalActionCatalog.Find(LocalActionCatalog.WikiIngest)!.ParallelExecutors);
    }
}
