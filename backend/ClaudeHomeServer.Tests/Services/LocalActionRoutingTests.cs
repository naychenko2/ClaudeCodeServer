using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Маршрутизация фоновых действий локаль(Ollama)/claude: LocalActionRouter + CheapTextRunner.
public class LocalActionRoutingTests
{
    private static IConfiguration Config(Dictionary<string, string?> d) =>
        TestConfig.Build(d);

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static OllamaClient Ollama(IConfiguration config) =>
        new(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);

    // Прямой HTTP-адаптер. Без настроенного провайдера openrouter в конфиге он Enabled=false —
    // ровно как в тестах цепочки (шаг адаптера «не сработал», управление уходит дальше).
    private static CloudCheapClient Cloud(IConfiguration config) =>
        new(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);

    // Стор оверрайдов пишет файл рядом с DataPath — в тестах уводим его во временную папку,
    // чтобы прогоны не делили состояние между собой и с рабочей data/.
    private static IConfiguration ConfigWithTempData(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return Config(d);
    }

    private static LocalActionOverridesStore Store(IConfiguration config) =>
        new(config, NullLogger<LocalActionOverridesStore>.Instance);

    private static LocalActionRouter Router(Dictionary<string, string?> cfg)
    {
        var config = ConfigWithTempData(cfg);
        return new LocalActionRouter(Ollama(config), Store(config), config, NullLogger<LocalActionRouter>.Instance);
    }

    // Роутер вместе со своим стором — для тестов админских оверрайдов
    private static (LocalActionRouter Router, LocalActionOverridesStore Store) RouterWithStore(
        Dictionary<string, string?> cfg)
    {
        var config = ConfigWithTempData(cfg);
        var store = Store(config);
        return (new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance), store);
    }

    // Фейковый claude-раннер: помечает ответ, чтобы отличить claude-путь от локали.
    // failModel — модель, вызов которой имитирует сбой провайдера (как реальный раннер,
    // бросающий InvalidOperationException); emptyModel — успешный, но пустой ответ;
    // timeouts — сколько первых вызовов обрываются таймаутом (LlmTimeoutException).
    private sealed class FakeOneShot(string? failModel = null, string? emptyModel = null,
        int timeouts = 0) : IOneShotRunner
    {
        public readonly List<string?> Calls = [];
        public readonly List<TimeSpan?> Timeouts = [];
        private int _timeoutsLeft = timeouts;

        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Calls.Add(model);
            Timeouts.Add(timeout);
            if (_timeoutsLeft > 0) { _timeoutsLeft--; throw new LlmTimeoutException(); }
            if (model is not null && model == failModel)
                throw new InvalidOperationException($"claude завершился с кодом 1: провайдер {model} недоступен");
            if (model is not null && model == emptyModel) return Task.FromResult("");
            return Task.FromResult($"CLAUDE[{model}]:{prompt}");
        }
        public async Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            // Та же семантика, что у RunAsync (вкл. failModel/emptyModel), но в OneShotResult —
            // для тестов второй точки EffectiveFallback (RunDetailedAsync).
            var text = await RunAsync(prompt, model, timeout, ct, ownerId, effort, label);
            return new OneShotResult(text, null, 0);
        }
    }

    [Fact]
    public void OllamaOff_NeverLocal()
    {
        // Пустой Model → Ollama выключена → любое действие идёт на claude
        var router = Router(new() { ["Ollama:Model"] = "" });
        Assert.False(router.OllamaEnabled);
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.False(router.UsesLocal(LocalActionCatalog.ActionRank));
    }

    [Fact]
    public void OllamaOn_CatalogDefaultsApply()
    {
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b", ["Ollama:BaseUrl"] = "http://localhost:11434" });
        Assert.True(router.OllamaEnabled);
        // Рекомендованные (DefaultLocal=true) — на локаль
        Assert.True(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.True(router.UsesLocal(LocalActionCatalog.ChatExtractTasks));
        // Оставленные на claude (DefaultLocal=false)
        Assert.False(router.UsesLocal(LocalActionCatalog.SkillTranslate));
        Assert.False(router.UsesLocal(LocalActionCatalog.DailyBriefing));
    }

    [Fact]
    public void ActionsOverride_WinsOverDefault()
    {
        var router = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:notes-tags"] = "false",     // рекомендованное — насильно на claude
            ["Ollama:Actions:skill-translate"] = "true", // claude-дефолт — насильно на локаль
        });
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.True(router.UsesLocal(LocalActionCatalog.SkillTranslate));
    }

    [Fact]
    public void UnknownActionKey_DoesNotThrow()
    {
        var router = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:bogus-action"] = "true",
        });
        // Неизвестный ключ игнорируется (лог-warning), роутер жив
        Assert.False(router.UsesLocal("bogus-action"));
        Assert.True(router.UsesLocal(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public void ProfileFor_UsesCatalogDefaults_AndConfigOverride()
    {
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b" });
        // notes-tags — профиль Small (дефолт num_ctx 4096)
        Assert.Equal(4096, router.ProfileFor(LocalActionCatalog.NotesTags).NumCtx);

        var overridden = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Profiles:small:NumCtx"] = "9000",
        });
        Assert.Equal(9000, overridden.ProfileFor(LocalActionCatalog.NotesTags).NumCtx);
    }

    // Локальный лимит вывода и облачный — разные потолки (прод 2026-08-05): локаль
    // бережёт память Ollama, облачный заходит в max_tokens запроса к провайдеру и должен
    // быть достаточным для крупного JSON-плана. Дефолты каталога это уже учитывают,
    // но конфиг может сократить — тест проверяет, что сокращение РАЗДЕЛЬНОЕ.
    [Fact]
    public void ProfileFor_CloudNumPredict_ОтдельноОтЛокального()
    {
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b" });
        var spec = router.ProfileFor(LocalActionCatalog.TeamImplementPlan);
        // Large по дефолту: 1024 локально, 8192 облачно — паритет 1:8, как раз под план.
        Assert.Equal(1024, spec.NumPredict);
        Assert.Equal(8192, spec.CloudNumPredict);
        Assert.True(spec.CloudNumPredict > spec.NumPredict,
            "облачный лимит вывода обязан быть больше локального — иначе план оборвётся");

        // Конфиг умеет сократить каждый по отдельности.
        var overridden = Router(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Profiles:large:CloudNumPredict"] = "4096",
        });
        Assert.Equal(4096, overridden.ProfileFor(LocalActionCatalog.TeamImplementPlan).CloudNumPredict);
        Assert.Equal(1024, overridden.ProfileFor(LocalActionCatalog.TeamImplementPlan).NumPredict);
    }

    // --- Админские оверрайды маршрута (рантайм-переключение из UI) ---

    [Fact]
    public void AdminOverride_WinsOverConfigAndDefault()
    {
        var (router, store) = RouterWithStore(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:notes-tags"] = "true",
        });
        Assert.True(router.UsesLocal(LocalActionCatalog.NotesTags));

        // Админ перевёл на claude — сильнее конфига, и БЕЗ пересоздания роутера (singleton в бою)
        Assert.True(store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.ClaudeRoute));
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.Equal(RouteSource.Admin, router.Resolve(LocalActionCatalog.NotesTags).Source);

        // Дефолт каталога тоже перебивается
        Assert.True(store.Set(LocalActionCatalog.DailyBriefing, LocalActionOverridesStore.LocalRoute));
        Assert.True(router.UsesLocal(LocalActionCatalog.DailyBriefing));
    }

    [Fact]
    public void AdminOverride_ConcreteModelRoute()
    {
        var (router, store) = RouterWithStore(new() { ["Ollama:Model"] = "qwen3:14b" });

        store.Set(LocalActionCatalog.NotesTags, "deepseek-chat");
        var route = router.Resolve(LocalActionCatalog.NotesTags);
        Assert.Equal(RouteKind.Model, route.Kind);
        Assert.Equal("deepseek-chat", route.Model);
        Assert.Equal(RouteSource.Admin, route.Source);
        // Первый шаг — не локаль, хотя Ollama настроена (локаль остаётся вторым шагом цепочки)
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public void LegacyBoolFormat_Migrates()
    {
        // Файл, записанный до появления выбора модели: true = локаль, false = claude
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        var dir = Path.GetDirectoryName(config["DataPath"]!)!;
        File.WriteAllText(Path.Combine(dir, "local-actions.json"),
            """{"notes-tags":false,"daily-briefing":true}""");

        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        // false («не локаль») в v2 читается как слот «средняя»: при пустом слоте поведение
        // прежнее (модель действия), при заданном — управляется настройкой тиров
        var notesRoute = router.Resolve(LocalActionCatalog.NotesTags);
        Assert.Equal(RouteKind.Tier, notesRoute.Kind);
        Assert.Equal(ModelTier.Medium, notesRoute.Tier);
        Assert.Equal(RouteKind.Local, router.Resolve(LocalActionCatalog.DailyBriefing).Kind);
    }

    [Fact]
    public void AdminReset_ReturnsToConfigThenDefault()
    {
        var (router, store) = RouterWithStore(new()
        {
            ["Ollama:Model"] = "qwen3:14b",
            ["Ollama:Actions:skill-translate"] = "true",
        });

        store.Set(LocalActionCatalog.SkillTranslate, LocalActionOverridesStore.ClaudeRoute);
        store.Reset(LocalActionCatalog.SkillTranslate);
        // Вернулись к значению конфига, а не к дефолту каталога (там claude)
        var route = router.Resolve(LocalActionCatalog.SkillTranslate);
        Assert.Equal(RouteKind.Local, route.Kind);
        Assert.Equal(RouteSource.Config, route.Source);

        // У действия без записи в конфиге источник — дефолт каталога
        Assert.Equal(RouteSource.Default, router.Resolve(LocalActionCatalog.NotesTags).Source);
    }

    [Fact]
    public void AdminOverride_SurvivesRestart()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "qwen3:14b" });
        Store(config).Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.ClaudeRoute);

        // Новый стор поверх той же папки = перезапуск сервера
        var reloaded = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        Assert.False(reloaded.UsesLocal(LocalActionCatalog.NotesTags));
        Assert.Equal(RouteSource.Admin, reloaded.Resolve(LocalActionCatalog.NotesTags).Source);
    }

    [Fact]
    public void AdminOverride_UnknownKeyRejected()
    {
        var (_, store) = RouterWithStore(new() { ["Ollama:Model"] = "qwen3:14b" });
        Assert.False(store.Set("bogus-action", LocalActionOverridesStore.LocalRoute));
        Assert.False(store.Reset("bogus-action"));
        // Пустой маршрут — тоже мусор
        Assert.False(store.Set(LocalActionCatalog.NotesTags, "   "));
    }

    [Fact]
    public void AdminOverride_IgnoredWhenOllamaOff()
    {
        // Оверрайд сохраняется, но без настроенной Ollama маршрут всё равно claude
        var (router, store) = RouterWithStore(new() { ["Ollama:Model"] = "" });
        store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.LocalRoute);
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
        // Сам выбор при этом сохранён — вернётся, как только Ollama настроят
        Assert.Equal(RouteKind.Local, router.Resolve(LocalActionCatalog.NotesTags).Kind);
    }

    [Fact]
    public async Task CheapRunner_OllamaOff_GoesToClaude()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), new FakeOneShot(),
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");
        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.False(runner.UsesLocal(LocalActionCatalog.NotesTags));
    }

    // --- Цепочка «выбранная модель → локаль → claude» ---
    // Ollama в тестах недоступна по сети (запросы к localhost:11434 падают в null), поэтому
    // шаг локали всегда «не сработал» — это ровно тот случай, который проверяем.

    [Fact]
    public async Task ВыбраннаяМодель_ИспользуетсяПервой()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "deepseek-chat");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude, NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[deepseek-chat]:prompt-text", result);
        Assert.Equal(["deepseek-chat"], claude.Calls);   // до фолбэка на haiku дело не дошло
    }

    [Fact]
    public async Task ВыбраннаяМодельУпала_УходитНаClaude()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "deepseek-chat");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(failModel: "deepseek-chat");
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude, NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        // Сбой выбранной модели не роняет действие: локаль выключена → последний шаг claude
        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.Equal(["deepseek-chat", "haiku"], claude.Calls);
    }

    // --- Бесплатная цепочка (RunFreeAsync): direct-адаптер → локаль, claude НИКОГДА ---

    [Fact]
    public async Task RunFree_БезБесплатныхИсполнителей_ОтдаётNull()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunFreeAsync(LocalActionCatalog.ChatTitle, "prompt-text");

        Assert.Null(result);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task RunFree_ВыбранаПровайдерскаяМодель_НеПлатитClaude()
    {
        // Модель без префикса direct: идёт через claude CLI — в бесплатной цепочке ей не место,
        // даже будучи выбранной админом. Иначе фоновое «украшение» молча стало бы платным.
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.ChatTitle, "deepseek-chat");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunFreeAsync(LocalActionCatalog.ChatTitle, "prompt-text");

        Assert.Null(result);
        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task ВыбраннаяМодельВернулаПустое_УходитНаClaude()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "glm-4");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(emptyModel: "glm-4");
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude, NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");
        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
    }

    [Fact]
    public async Task ПоследнийШагУпал_ИсключениеНаверх()
    {
        // Claude — конечный рубеж без страховки: его отказ обязан дойти до потребителя,
        // иначе фича молча получит пустой результат вместо честной ошибки.
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), new FakeOneShot(failModel: "haiku"),
            NullLogger<CheapTextRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku"));
    }

    [Fact]
    public async Task ПрямойМаршрут_БезАдаптера_УходитНаClaude()
    {
        // Маршрут с префиксом "direct:" — прямой HTTP-адаптер. Провайдер openrouter в тестовом
        // конфиге не настроен → адаптер Enabled=false → шаг отдаёт null, цепочка идёт на claude.
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, CloudCheapClient.RoutePrefix + "nvidia/nemotron:free");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        // Маршрут распознан как Model (не local/claude), но адаптер выключен → фолбэк на claude
        Assert.Equal(RouteKind.Model, router.Resolve(LocalActionCatalog.NotesTags).Kind);
        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.Equal(["haiku"], claude.Calls);  // выбранная модель шла через адаптер, не через claude CLI
    }

    [Theory]
    [InlineData("direct:nvidia/nemotron:free", true)]
    [InlineData("nvidia/nemotron:free", false)]
    [InlineData("local", false)]
    [InlineData("claude", false)]
    public void IsDirectRoute_РаспознаётПрефикс(string route, bool expected)
    {
        Assert.Equal(expected, CloudCheapClient.IsDirectRoute(route));
        if (expected) Assert.Equal("nvidia/nemotron:free", CloudCheapClient.StripPrefix(route));
    }

    // --- Пропуск шага локали для «сильных» действий (DefaultLocal=false) ---
    // Kind=Local (явный выбор админа) уважаем всегда; Kind=Model (локаль как страховка) —
    // только там, где локаль вообще уместна; Kind=Claude — локаль никогда.
    [Theory]
    // Лёгкое действие (DefaultLocal=true)
    [InlineData(LocalActionCatalog.NotesTags, RouteKind.Local, true)]
    [InlineData(LocalActionCatalog.NotesTags, RouteKind.Model, true)]
    [InlineData(LocalActionCatalog.NotesTags, RouteKind.Claude, false)]
    // «Сильное» действие (DefaultLocal=false)
    [InlineData(LocalActionCatalog.SkillTranslate, RouteKind.Local, true)]   // явный выбор — уважаем
    [InlineData(LocalActionCatalog.SkillTranslate, RouteKind.Model, false)]  // страховку пропускаем
    [InlineData(LocalActionCatalog.SkillTranslate, RouteKind.Claude, false)]
    public void LocalStepApplies_SkipsFallbackForStrong(string key, RouteKind kind, bool expected)
    {
        Assert.Equal(expected, CheapTextRunner.LocalStepApplies(key, kind));
    }

    [Fact]
    public void Catalog_AllKeysUnique()
    {
        var keys = LocalActionCatalog.All.Select(a => a.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(LocalActionCatalog.All, a => Assert.True(LocalActionCatalog.IsKnown(a.Key)));
    }

    // --- Таймауты по маршруту: локаль и облако живут со своими потолками ---
    // Локальные значения калибровались под Ollama; облачная сильная модель на сложной
    // задаче отвечает заметно дольше (прод 2026-08-04: планировщик КР на opus).

    [Fact]
    public void Профили_ОблачныйТаймаутОтдельныйИБольшеЛокального()
    {
        foreach (var (profile, spec) in LocalActionCatalog.ProfileDefaults)
            Assert.True(spec.CloudTimeoutMs > spec.TimeoutMs,
                $"профиль {profile}: облачный потолок обязан быть больше локального");
    }

    [Fact]
    public async Task CheapRunner_ОблачныйМаршрут_ТаймаутНеЛокальный()
    {
        // Дефолт team-implement-plan — маршрут-слот; Ollama выключена → цепочка
        // заканчивается на claude. Раннер обязан получить ОБЛАЧНЫЙ потолок профиля,
        // а не локальный (90 с), который оборвал планировщик на проде.
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        await runner.RunAsync(LocalActionCatalog.TeamImplementPlan, "prompt-text", "haiku");

        var spec = router.ProfileFor(LocalActionCatalog.TeamImplementPlan);
        var timeout = Assert.Single(claude.Timeouts);
        Assert.Equal(TimeSpan.FromMilliseconds(spec.CloudTimeoutMs), timeout);
        Assert.NotEqual(TimeSpan.FromMilliseconds(spec.TimeoutMs), timeout);
    }

    [Fact]
    public async Task CheapRunnerDetailed_ОблачныйТаймаут_ПереопределяетсяИзКонфига()
    {
        var config = ConfigWithTempData(new()
        {
            ["Ollama:Model"] = "",
            ["Ollama:Profiles:large:CloudTimeoutMs"] = "600000",
        });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        await runner.RunDetailedAsync(LocalActionCatalog.TeamImplementPlan, "prompt-text", "haiku");

        Assert.Equal(TimeSpan.FromMilliseconds(600_000), Assert.Single(claude.Timeouts));
    }

    [Fact]
    public void TimeoutMsFor_ЗависитОтМаршрута()
    {
        // Ollama настроена, действие рекомендовано локали → локальный потолок
        var router = Router(new() { ["Ollama:Model"] = "qwen3:14b", ["Ollama:BaseUrl"] = "http://localhost:11434" });
        Assert.Equal(router.ProfileFor(LocalActionCatalog.NotesTags).TimeoutMs,
            router.TimeoutMsFor(LocalActionCatalog.NotesTags));
        // Действие не на локали → облачный потолок
        Assert.Equal(router.ProfileFor(LocalActionCatalog.TeamImplementPlan).CloudTimeoutMs,
            router.TimeoutMsFor(LocalActionCatalog.TeamImplementPlan));

        // Ollama выключена → цепочка любого действия может закончиться на claude
        var off = Router(new() { ["Ollama:Model"] = "" });
        Assert.Equal(off.ProfileFor(LocalActionCatalog.NotesTags).CloudTimeoutMs,
            off.TimeoutMsFor(LocalActionCatalog.NotesTags));
    }

    // --- Один ретрай при таймауте финального claude-шага ---

    [Fact]
    public async Task CheapRunner_ТаймаутClaude_ОдинПовтор()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(timeouts: 1);
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunAsync(LocalActionCatalog.TeamImplementPlan, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.Equal(2, claude.Calls.Count); // обрыв + ровно один повтор
    }

    [Fact]
    public async Task CheapRunner_ДваТаймаута_ОтказБезТретьейПопытки()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(timeouts: 2);
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        await Assert.ThrowsAsync<LlmTimeoutException>(
            () => runner.RunAsync(LocalActionCatalog.TeamImplementPlan, "prompt-text", "haiku"));
        Assert.Equal(2, claude.Calls.Count); // третий раз не пробуем
    }

    [Fact]
    public async Task CheapRunner_ОбычныйСбойClaude_БезПовтора()
    {
        // Не-таймаут (exit code, провайдер недоступен) повторяется прежним путём —
        // фолбэком по цепочке, а не слепым ретраем.
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(failModel: "haiku");
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku"));
        Assert.Single(claude.Calls);
    }

    [Fact]
    public async Task CheapRunnerDetailed_ТаймаутClaude_ОдинПовтор()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot(timeouts: 1);
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance);

        var result = await runner.RunDetailedAsync(LocalActionCatalog.TeamImplementPlan, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[haiku]:prompt-text", result.Text);
        Assert.Equal(2, claude.Calls.Count);
    }

    // --- Маршруты-слоты (tier:strong|medium|weak) ---

    [Fact]
    public void Route_TierИзСтора_Резолвится()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "tier:strong");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);

        var route = router.Resolve(LocalActionCatalog.NotesTags);
        Assert.Equal(RouteKind.Tier, route.Kind);
        Assert.Equal(ModelTier.Strong, route.Tier);
        Assert.False(router.UsesLocal(LocalActionCatalog.NotesTags));
    }

    [Fact]
    public void ЛегасиЗначения_ЧитаютсяКакСредняя()
    {
        // "default" и "claude" из v1 оба означали «обычная модель, не локаль» → tier:medium
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.DefaultRoute);
        store.Set(LocalActionCatalog.NoteTitle, LocalActionOverridesStore.ClaudeRoute);
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);

        Assert.Equal((RouteKind.Tier, (ModelTier?)ModelTier.Medium),
            (router.Resolve(LocalActionCatalog.NotesTags).Kind, router.Resolve(LocalActionCatalog.NotesTags).Tier));
        Assert.Equal((RouteKind.Tier, (ModelTier?)ModelTier.Medium),
            (router.Resolve(LocalActionCatalog.NoteTitle).Kind, router.Resolve(LocalActionCatalog.NoteTitle).Tier));
    }

    [Fact]
    public async Task CheapRunner_МаршрутСлот_БерётМодельСлота()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierMedium = "glm-5.2" });
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance, appSettings);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        // Маршрут-слот берёт модель слота, а НЕ fallbackModel действия ("haiku")
        Assert.Equal("CLAUDE[glm-5.2]:prompt-text", result);
        Assert.Equal(["glm-5.2"], claude.Calls);
    }

    [Fact]
    public async Task CheapRunner_ПустойСлот_ОткатываетсяКМоделиДействия()
    {
        // Слот не задан → маршрут-слот откатывается к модели действия (haiku), а НЕ к дефолту
        // CLI: слот — дефолтный маршрут всех действий, и без отката ненастроенный инстанс
        // гонял бы теги и заголовки на дорогой модели
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var store = Store(config);
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance, new AppSettingsService(config));

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
    }

    [Fact]
    public void Дефолт_БезНастроек_СлотПоПрофилю()
    {
        // Ни оверрайда админа, ни конфига, действие НЕ рекомендовано локали → RouteKind.Tier
        // со слотом из профиля сложности (Large → средняя)
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);

        var route = router.Resolve(LocalActionCatalog.Changelog);

        Assert.Equal(RouteKind.Tier, route.Kind);
        Assert.Equal(ModelTier.Medium, route.Tier);
        Assert.Equal(RouteSource.Default, route.Source);
    }

    [Fact]
    public async Task Дефолт_БезНастроек_ИдётНаМодельСлота()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var router = new LocalActionRouter(Ollama(config), Store(config), config,
            NullLogger<LocalActionRouter>.Instance);
        var claude = new FakeOneShot();
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierMedium = "glm-5.2" });
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance, appSettings);

        var result = await runner.RunAsync(LocalActionCatalog.Changelog, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[glm-5.2]:prompt-text", result);
    }

    // --- Слот-пресет: значение слота разворачивается в конкретную модель (дефект места
    //     project-icon: маркер preset:… уходил в CLI как имя модели → no-model) ---

    // Раннер с полным набором зависимостей разворачивания (как DI): слоты, пресеты,
    // ModelAssignmentResolver — та же точка, что и у маршрутных пресетов.
    private static (CheapTextRunner Runner, FakeOneShot Claude, AppSettingsService App,
        UserStore Users, SpecialtySettingsStore Specialty) BuildCheapRunnerWithResolver()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var userTiers = new UserModelTierResolver(users, appSettings);
        var store = Store(config);
        var specialty = Specialty(config);
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var assignment = new ModelAssignmentResolver(appSettings, store, userTiers, specialty);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), Cloud(config), claude,
            NullLogger<CheapTextRunner>.Instance, appSettings, userTiers, assignment);
        return (runner, claude, appSettings, users, specialty);
    }

    [Fact]
    public async Task CheapRunner_СлотПресет_РазворачиваетсяВМодельПервогоШага()
    {
        // Место БЕЗ явного админ-маршрута (дефолт каталога — слот уровня): слот medium =
        // preset:p1. Дефект: строка "preset:p1" уходила в CLI как имя модели → no-model.
        var (runner, claude, app, _, specialty) = BuildCheapRunnerWithResolver();
        app.Save(new AppSettings { ModelTierMedium = "preset:p1" });
        specialty.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "glm-5.2", "deepseek") } });

        var result = await runner.RunAsync(LocalActionCatalog.Changelog, "prompt-text", "haiku");

        // В CLI идёт конкретная модель первого шага цепочки, а не маркер preset:p1
        Assert.Equal("CLAUDE[glm-5.2]:prompt-text", result);
        Assert.Equal(["glm-5.2"], claude.Calls);
    }

    [Fact]
    public async Task CheapRunner_СлотПресет_БитаяСсылка_ОткатНаМодельДействия()
    {
        // Слот ссылается на удалённый пресет: fail-open на модель действия (haiku) + warning,
        // а не невнятный no-model от CLI с "preset:no-such" в качестве имени модели.
        var (runner, claude, app, _, _) = BuildCheapRunnerWithResolver();
        app.Save(new AppSettings { ModelTierMedium = "preset:no-such" });

        var result = await runner.RunAsync(LocalActionCatalog.Changelog, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[haiku]:prompt-text", result);
        Assert.Equal(["haiku"], claude.Calls);
        claude.Calls.Should().NotContain(c => c != null && c.StartsWith("preset:"),
            "маркер пресета не должен доходить до CLI как имя модели");
    }

    [Fact]
    public async Task CheapRunner_СлотПресет_TierШаг_РезолвитсяПоСлотуВладельца()
    {
        // Первый шаг цепочки — tier:weak: разворачивается по слоту ВЛАДЕЛЬЦА действия
        // (личный слот сильнее глобального), как и любой tier-шаг агентной ветки.
        var (runner, claude, app, users, specialty) = BuildCheapRunnerWithResolver();
        app.Save(new AppSettings { ModelTierMedium = "preset:p1", ModelTierWeak = "slot-haiku" });
        var u1 = users.Add("u1", "p", "user");
        users.SetModelTiers(u1.Id, null, null, weak: "user-haiku");
        specialty.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "tier:weak", "glm-5.2") } });

        var result = await runner.RunAsync(LocalActionCatalog.Changelog, "prompt-text", "haiku", ownerId: u1.Id);

        Assert.Equal("CLAUDE[user-haiku]:prompt-text", result);
        Assert.Equal(["user-haiku"], claude.Calls);
    }

    // --- Агентные места (группа «Чаты и персоны») и ModelAssignmentResolver ---

    [Fact]
    public void Resolver_ЯвнаяМодель_СильнееВсего()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "glm-5.2" });
        var resolver = new ModelAssignmentResolver(appSettings, Store(config));

        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatNew, "opus"));
    }

    [Fact]
    public void Resolver_УровеньВместоМодели_РазворачиваетсяВМодельСлота()
    {
        // Уровень задачи/персоны приходит маркером «tier:*» — резолвер обязан развернуть его
        // в модель слота, иначе маркер ушёл бы в --model и осел в сессии
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus", ModelTierWeak = "haiku" });
        var resolver = new ModelAssignmentResolver(appSettings, Store(config));

        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.TasksExecutor, "tier:strong"));
        Assert.Equal("haiku", resolver.Resolve(LocalActionCatalog.TasksExecutor, "tier:weak"));
    }

    [Fact]
    public void Resolver_УровеньВместоМодели_ЛичныйСлотВладельца()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, strong: "user-opus", null, null);
        var resolver = new ModelAssignmentResolver(appSettings, Store(config),
            new UserModelTierResolver(users, appSettings));

        Assert.Equal("user-opus", resolver.Resolve(LocalActionCatalog.TasksExecutor, "tier:strong", user.Id));
        // Владелец без своего слота — глобальный слот инстанса
        Assert.Equal("global-opus", resolver.Resolve(LocalActionCatalog.TasksExecutor, "tier:strong", "no-such-user"));
    }

    [Fact]
    public void Resolver_УровеньВместоМодели_ПустойСлот_УходитНаНазначениеМеста()
    {
        // Слот не настроен — маркер наружу не отдаём (CLI его не поймёт): место идёт
        // своим назначением, как будто уровень и не задавали
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierMedium = "sonnet" });
        var store = Store(config);
        store.Set(LocalActionCatalog.TasksExecutor, "tier:medium");
        var resolver = new ModelAssignmentResolver(appSettings, store);

        Assert.Equal("sonnet", resolver.Resolve(LocalActionCatalog.TasksExecutor, "tier:strong"));
    }

    [Fact]
    public void Resolver_УстаревшийModelTierПерсоны_БольшеНеВлияет()
    {
        // Сторож (упрощение модели 15.08.2026): Persona.ModelTier выведен из цепочки
        // приоритета — резолв его не читает. Уровень задаётся задачей, специальностью
        // или дефолтом места; заданный ранее уровень мигрирует в TierMedium (см. тесты
        // PersonaModelTierMigrationTests).
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus", ModelTierMedium = "sonnet" });
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, null, weak: "user-haiku");
        var resolver = new ModelAssignmentResolver(appSettings, Store(config),
            new UserModelTierResolver(users, appSettings));

        // Явная модель персоны работает и при заданном (устаревшем) ModelTier
        Assert.Equal("glm-5.2", resolver.PersonaModel(
            new Persona { Model = "glm-5.2", ModelTier = ModelTier.Weak }, user.Id));
        // Без модели/ячеек уровень персоны НЕ разворачивается — место решает само
        Assert.Null(resolver.PersonaModel(new Persona { ModelTier = ModelTier.Weak }, user.Id));
        Assert.Null(resolver.PersonaModel(new Persona { ModelTier = ModelTier.Strong }, user.Id));
        // Ячейка на уровне места — то, чем после миграции заменён уровень персоны
        Assert.Equal("user-haiku", resolver.PersonaModel(
            new Persona { TierMedium = "user-haiku" }, user.Id, ModelTier.Medium));
        Assert.Null(resolver.PersonaModel(new Persona(), user.Id));
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatPersona,
            resolver.PersonaModel(new Persona(), user.Id), user.Id));
    }

    // --- Матрицы уровней и пресеты-цепочки (ADR-007 §2, §3) ---

    private static SpecialtySettingsStore Specialty(IConfiguration config) =>
        ClaudeHomeServer.Tests.Helpers.TestSpecialtyStore.Create(config);

    private static SpecialtyTemplateSettings Tmpl(string? strong = null, string? medium = null,
        string? weak = null, ModelTier? defaultTier = null) => new()
    {
        TierStrong = strong,
        TierMedium = medium,
        TierWeak = weak,
        DefaultTier = defaultTier,
    };

    // Резолвер с полным набором зависимостей (appSettings + store + userTiers + specialty)
    private static (ModelAssignmentResolver Resolver, AppSettingsService App, UserStore Users,
        UserModelTierResolver Tiers, SpecialtySettingsStore Specialty, LocalActionOverridesStore Store)
        BuildResolverWithSpecialty()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var userTiers = new UserModelTierResolver(users, appSettings);
        var store = Store(config);
        var specialty = Specialty(config);
        var resolver = new ModelAssignmentResolver(appSettings, store, userTiers, specialty);
        return (resolver, appSettings, users, userTiers, specialty, store);
    }

    // --- Матрицы: персона → специальность → слоты владельца (ADR-007 §2, §8) ---

    [Fact]
    public void Resolver_ЯчейкаПерсоны_БьётЯчейкуСпециальности()
    {
        // §8: при уровне T модель берётся из ячейки персоны, даже если у специальности своё.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus") },
        });

        Assert.Equal("persona-opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor, TierStrong = "persona-opus" },
            u1.Id, ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ЯчейкаПерсонаПуста_БерётсяИзСпециальности()
    {
        // §8: пустая ячейка персоны → ячейка специальности.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierMedium = "slot-sonnet" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(medium: "spec-sonnet") },
        });

        Assert.Equal("spec-sonnet", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor },
            u1.Id, ModelTier.Medium));
    }

    [Fact]
    public void Resolver_МатрицыПусты_СлотВладельца()
    {
        // §8: пустые матрицы персоны и специальности → слот владельца (эквивалент старому пути).
        var (resolver, app, users, _, _, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierWeak = "slot-haiku" });
        var u1 = users.Add("u1", "p", "user");
        users.SetModelTiers(u1.Id, null, null, weak: "user-haiku");

        Assert.Equal("user-haiku", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor },
            u1.Id, ModelTier.Weak));
    }

    [Fact]
    public void Resolver_DefaultTierСпециальности_ИсточникУровня()
    {
        // §8: при пустых TaskItem.ModelTier/Persona.ModelTier уровень даёт DefaultTier специальности.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            // У специальности НЕТ своих ячеек, только DefaultTier=Strong → разворот падает на слот
            Specialties = { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Strong) },
        });

        // DefaultTier задаёт уровень; матрица специальности пуста → разворот падает на слот владельца
        Assert.Equal("slot-opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor }, u1.Id));
    }

    [Fact]
    public void Resolver_МатрицаСпециальности_ОднаДляВсехВладельцев()
    {
        // Слоёв у специальностей нет (ADR-012): матрица роли — контракт инстанса
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user").Id;
        var u2 = users.Add("u2", "p", "user").Id;
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "global-opus") },
        });

        foreach (var who in new[] { u1, u2 })
            Assert.Equal("global-opus", resolver.PersonaModel(
                new Persona { Specialty = PersonaSpecialty.BackendExecutor }, who, ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ЯвнаяМодельПерсоны_СильнееМатриц()
    {
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus") },
        });

        Assert.Equal("haiku", resolver.PersonaModel(
            new Persona { Model = "haiku", Specialty = PersonaSpecialty.BackendExecutor,
                TierStrong = "persona-opus", ModelTier = ModelTier.Strong }, "u1"));
    }

    [Fact]
    public void Resolver_СпециальностьNone_НеЗадействована()
    {
        var (resolver, app, _, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus") },
        });

        // Specialty == None — матрица специальности не опрашивается; без модели/уровня → null
        Assert.Null(resolver.PersonaModel(new Persona { Specialty = PersonaSpecialty.None }, "u1"));
    }

    // --- Дефолтный уровень места в резолве персоны (de-факто bugfix 2-й итерации): когда
    //     ни Specialty.DefaultTier, ни уровень задачи не заданы, уровень даёт дефолт места
    //     (Strong для чата персоны) — им матрица персоны и разворачивается. Иначе ячейка
    //     персоны молча не срабатывала. Порядок (после вывода Persona.ModelTier 15.08.2026):
    //     уровень задачи → Specialty.DefaultTier → место.

    [Fact]
    public void Resolver_ЯчейкаПерсоныБезУровня_ДефолтМестаСильная()
    {
        // Персона с заполненной ячейкой «сильная», без ModelTier и без DefaultTier специальности,
        // в чате персоны (дефолт места Strong) идёт моделью своей ячейки.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl() }, // без ячеек, без DefaultTier
        });

        Assert.Equal("persona-opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor, TierStrong = "persona-opus" },
            u1.Id, ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ЯчейкаПерсоныБезУровня_ДефолтМестаСредняя()
    {
        // Тот же сценарий, дефолт места Medium → берётся средняя ячейка персоны.
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(medium: "spec-sonnet") },
        });

        Assert.Equal("persona-sonnet", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor, TierMedium = "persona-sonnet" },
            "u1", ModelTier.Medium));
    }

    [Fact]
    public void Resolver_УстаревшийУровеньПерсоны_ДефолтМестаПобеждает()
    {
        // Сторож (упрощение 15.08.2026): ModelTier=Weak больше не перекрывает дефолт места —
        // уровень Strong берёт СИЛЬНУЮ ячейку, а не слабую. До упрощения ожидание было
        // обратным («persona-haiku»).
        var (resolver, _, users, _, _, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");

        Assert.Equal("persona-opus", resolver.PersonaModel(
            new Persona { TierStrong = "persona-opus", TierWeak = "persona-haiku", ModelTier = ModelTier.Weak },
            "u1", ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ПустаяЯчейкаПриДефолтеМеста_ПадаетНаСпециальность()
    {
        // placeDefaultTier задаёт уровень, но ячейка персоны пуста → ячейка специальности,
        // а если и она пуста — слот владельца (проверяет отдельный тест ниже).
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus") },
        });

        Assert.Equal("spec-opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor }, u1.Id, ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ПустаяЯчейкаИСпециальностьПриДефолтеМеста_СлотВладельца()
    {
        // Ячейки персоны и специальности пусты → слот владельца для уровня места.
        var (resolver, app, users, _, _, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");

        Assert.Equal("slot-opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.BackendExecutor }, u1.Id, ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ПресетВЯчейкеПриДефолтеМеста_Разворачивается()
    {
        // Ячейка персоны = preset:{id}, уровень из дефолта места → первый шаг цепочки.
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "opus", "glm-5.2") },
        });

        Assert.Equal("opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.None, TierStrong = "preset:p1" },
            "u1", ModelTier.Strong));
    }

    // --- Пресет preset:{id} в четырёх местах выбора модели (ADR-007 §3, §8) ---

    private static ModelRoutePreset Preset(string id, params string[] steps) => new()
    {
        Id = id,
        Name = "P" + id,
        Steps = steps.ToList(),
    };

    [Fact]
    public void Resolver_ПресетВЯчейкеПерсоны_ПервыйШагЦепочки()
    {
        // §8 место «персона»: ячейка матрицы персоны = preset → первый шаг цепочки.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "opus", "glm-5.2", "deepseek") },
        });

        Assert.Equal("opus", resolver.PersonaModel(
            new Persona { Specialty = PersonaSpecialty.None, TierStrong = "preset:p1" }, "u1", ModelTier.Strong));
    }

    [Fact]
    public void Resolver_ПресетВЯвнойМоделиПерсоны_ПервыйШагЦепочки()
    {
        // §8 место «персона» (явная Model): Persona.Model = preset → первый шаг.
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "glm-5.2", "deepseek") } });

        Assert.Equal("glm-5.2", resolver.PersonaModel(
            new Persona { Model = "preset:p1" }, "u1"));
    }

    [Fact]
    public void Resolver_ПресетВМестеКаталога_ПервыйШагЦепочки()
    {
        // §8 место «Кто что выполняет»: назначение админа = preset → первый шаг цепочки.
        var (resolver, _, _, _, specialty, store) = BuildResolverWithSpecialty();
        specialty.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "opus", "glm-5.2") } });
        store.Set(LocalActionCatalog.ChatNew, "preset:p1");

        // chat-new — агентное место, local/direct пропускаются; первый шаг opus
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatNew, ownerId: "u1"));
    }

    [Fact]
    public void Resolver_ПресетВСлотеВладельца_ПервыйШагЦепочки()
    {
        // §8 место «модели по умолчанию» (слот): User.ModelTierStrong = preset → первый шаг.
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        var u1 = users.Add("u1", "p", "user");
        users.SetModelTiers(u1.Id, strong: "preset:p1", null, null);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "tier:weak", "glm-5.2") },
        });
        // В пресете первый шаг tier:weak — разворачивается по слоту владельца (слабая = пусто → null),
        // значит цепочка даёт пусто для первого шага и берёт второй: glm-5.2. Проверяем разворот:
        resolver.ResolveChain(LocalActionCatalog.ChatNew, ownerId: u1.Id)
            .Should().ContainInOrder("glm-5.2");
    }

    [Fact]
    public void ResolveChain_Пресет_ВозвращаетВсеШагиКакМодели()
    {
        // §8 / §4: ResolveChain разворачивает пресет в упорядоченный список конкретных моделей
        // (план фолбэка). tier:*-шаг разворачивается по слоту владельца.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus", ModelTierWeak = "slot-haiku" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "glm-5.2", "tier:strong", "deepseek") },
        });

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatNew, "preset:p1", u1.Id);
        chain.Should().BeEquivalentTo(new[] { "glm-5.2", "slot-opus", "deepseek" }, opts => opts.WithStrictOrdering(),
            "tier:strong разворачивается по слоту владельца, остальные — как есть");
    }

    [Fact]
    public void Resolver_БитаяСсылкаПресет_МестоКаталога_FailOpenНеПадает()
    {
        // §8: ссылка на удалённый пресет в месте каталога — fail-open (не падает, цепочка пуста
        // → модель места не определена, решает CLI/дефолт). Битый пресет в ячейке матрицы
        // персоны — см. в SpecialistsStoreTests/Resolver: разворачивается в пусто.
        var (resolver, _, _, _, _, store) = BuildResolverWithSpecialty();
        store.Set(LocalActionCatalog.ChatNew, "preset:no-such");

        // Цепочка пуста → Resolve отдаёт null (не исключение, не маркер наружу)
        Assert.Null(resolver.Resolve(LocalActionCatalog.ChatNew, ownerId: "u1"));
        Assert.Empty(resolver.ResolveChain(LocalActionCatalog.ChatNew, ownerId: "u1"));
    }

    [Fact]
    public void ResolveChain_ВложенныйПресетНевозможен_СторЕгоОтвергает()
    {
        // Валидация запрещает шаг preset:* внутри пресета —两层невозможна конструктивно.
        var (resolver, _, _, _, specialty, _) = BuildResolverWithSpecialty();
        var error = specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("outer", "preset:inner") },
        });
        Assert.NotNull(error);
    }

    // --- Волна 1: цепочка хода есть всегда (явная модель + хвост тира, ADR-007 §4) ---
    // Инцидент 2026-08-08: чат с явной моделью персоны (opus) не имел цепочки → алфавитный
    // автоподбор. Теперь явная конкретная модель M = [M] + хвост цепочки слота её тира.

    // Сильный слот = пресет «Основной — Сильный» (5 шагов). Образец инцидента Софьи.
    private static (ModelAssignmentResolver Resolver, string OwnerId) BuildChainResolver()
    {
        var (resolver, appSettings, users, _, spec, _) = BuildResolverWithSpecialty();
        appSettings.Save(new AppSettings { ModelTierStrong = "preset:main-strong" });
        spec.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("main-strong", "opus", "kimi-k3", "glm-5.2", "qwen3.8-max", "deepseek-v4-pro") },
        });
        var owner = users.Add("u1", "p", "user");
        return (resolver, owner.Id);
    }

    [Fact]
    public void ResolveChain_ЯвнаяМодельПервыйШагПресета_ПолнаяЦепочка()
    {
        // (а) opus — первый шаг сильного пресета → TierOfModel=Strong, хвост = остаток пресета.
        var (resolver, ownerId) = BuildChainResolver();

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatPersona, "opus", ownerId);

        chain.Should().BeEquivalentTo(
            new[] { "opus", "kimi-k3", "glm-5.2", "qwen3.8-max", "deepseek-v4-pro" },
            opts => opts.WithStrictOrdering(),
            "явная модель = первый шаг пресета сильного слота → цепочка = пресет целиком");
    }

    [Fact]
    public void ResolveChain_МодельИзСерединыПресета_ХвостПослеПозиции()
    {
        // (б) glm-5.2 — третий шаг пресета → хвост = шаги после неё, без opus/kimi-k3.
        var (resolver, ownerId) = BuildChainResolver();

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatPersona, "glm-5.2", ownerId);

        chain.Should().BeEquivalentTo(
            new[] { "glm-5.2", "qwen3.8-max", "deepseek-v4-pro" },
            opts => opts.WithStrictOrdering(),
            "модель из середины пресета → хвост после её позиции");
    }

    [Fact]
    public void ResolveChain_МодельВнеПресетов_ВсяЦепочкаДефолтногоТираМеста()
    {
        // (в) модель не принадлежит ни одному слоту → тир = дефолт места (chat-persona = Strong),
        // модель не найдена в цепочке слота → хвост = вся цепочка слота.
        var (resolver, ownerId) = BuildChainResolver();

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatPersona, "stranger-model", ownerId);

        chain.Should().ContainInOrder(
            new[] { "stranger-model", "opus", "kimi-k3", "glm-5.2", "qwen3.8-max", "deepseek-v4-pro" },
            "модель вне пресетов → тир места Strong, хвост = вся цепочка сильного слота");
        chain.First().Should().Be("stranger-model", "явная модель всегда первая");
    }

    [Fact]
    public void ResolveChain_ПустыеСлоты_ЦепочкаИзОдногоЭлемента()
    {
        // (г) ни слотов, ни пресетов → TierOfModel=null, слот пуст → хвоста нет, цепочка = [M].
        var (resolver, _, _, _, _, _) = BuildResolverWithSpecialty();
        var owner = Guid.NewGuid().ToString(); // слотов не настроено

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatPersona, "opus", owner);

        chain.Should().BeEquivalentTo(new[] { "opus" },
            "пустые слоты → без хвоста, цепочка из одного элемента (после пула честная ошибка)");
    }

    [Fact]
    public void TierOfModel_НаходитТирЧерезСлотПресет()
    {
        // (д) TierOfModel определяет тир по членству в развёрнутых шагах слота-пресета
        // (чинит дефект «слот-пресет ломает реверс-эвристику»): литерал слота — preset:main-strong,
        // а сравнение идёт по развёрнутым моделям [opus, kimi-k3, …].
        var (resolver, ownerId) = BuildChainResolver();

        Assert.Equal(ModelTier.Strong, resolver.TierOfModel("opus", ownerId));
        Assert.Equal(ModelTier.Strong, resolver.TierOfModel("deepseek-v4-pro", ownerId));
        // Регистронезависимо
        Assert.Equal(ModelTier.Strong, resolver.TierOfModel("KIMI-K3", ownerId));
        // Модель вне слота → null
        Assert.Null(resolver.TierOfModel("stranger-model", ownerId));
        Assert.Null(resolver.TierOfModel(null, ownerId));
    }

    // --- Цепочка хода с матрицами персоны (инцидент 2026-08-26) ---
    // Дефект: стартовая модель резолвилась по матрицам специальности (замораживалась в
    // Session.Model), а хвост цепочки брался из общего слота владельца — у персоны со
    // своим пресетом цепочка обрубалась. Починка: перегрузка ResolveChain с персоной
    // строит хвост по узким матрицам (персона → специальность → слоты владельца).

    // Обстановка инцидента: общий слот владельца «Мощный» = [opus[1m], glm-5.3[1m], kimi-k3,
    // deepseek-v4-pro]; специальность designer, TierStrong = preset «Сильный — Кими» =
    // [kimi-k3, opus[1m], glm-5.3[1m]]. Личных слотов у владельца нет (падают на инстанс).
    private static (ModelAssignmentResolver Resolver, string OwnerId, Persona Persona) BuildIncidentResolver()
    {
        var (resolver, appSettings, users, _, spec, _) = BuildResolverWithSpecialty();
        appSettings.Save(new AppSettings { ModelTierStrong = "preset:powerful" });
        spec.SetGlobal(new SpecialtySettingsLayer
        {
            Presets =
            {
                Preset("powerful", "opus[1m]", "glm-5.3[1m]", "kimi-k3", "deepseek-v4-pro"),
                Preset("strong-kimi", "kimi-k3", "opus[1m]", "glm-5.3[1m]"),
            },
            Specialties = { ["designer"] = Tmpl(strong: "preset:strong-kimi") },
        });
        var owner = users.Add("u1", "p", "user");
        return (resolver, owner.Id, new Persona { Specialty = PersonaSpecialty.Designer });
    }

    [Fact]
    public void ResolveChain_ПерсонаСоСпециальностью_ЦепочкаИзМатрицыСпециальности()
    {
        // Персона со специальностью, чей TierStrong — пресет [A, B, C]; общий слот владельца
        // другой ([X, Y]). Старт на A → цепочка [A, B, C] из матрицы специальности,
        // а НЕ хвост общего слота владельца.
        var (resolver, appSettings, users, _, spec, _) = BuildResolverWithSpecialty();
        appSettings.Save(new AppSettings { ModelTierStrong = "preset:owner-strong" });
        spec.SetGlobal(new SpecialtySettingsLayer
        {
            Presets =
            {
                Preset("owner-strong", "owner-x", "owner-y"),
                Preset("spec-strong", "model-a", "model-b", "model-c"),
            },
            Specialties = { ["designer"] = Tmpl(strong: "preset:spec-strong") },
        });
        var owner = users.Add("u1", "p", "user");
        var persona = new Persona { Specialty = PersonaSpecialty.Designer };

        var chain = resolver.ResolveChain(LocalActionCatalog.ChatPersona, "model-a", owner.Id, persona);

        chain.Should().BeEquivalentTo(new[] { "model-a", "model-b", "model-c" },
            opts => opts.WithStrictOrdering(),
            "хвост цепочки строится по матрице специальности, а не по общему слоту владельца");
    }

    [Fact]
    public void ResolveChain_БезПерсоны_ХвостОбщегоСлотаВладельца()
    {
        // Регресс: персоны нет → поведение прежнее, хвост берётся из общего слота
        // владельца (в обстановке инцидента после kimi-k3 остаётся только deepseek-v4-pro).
        var (resolver, ownerId, _) = BuildIncidentResolver();

        var chain = resolver.ResolveChain(LocalActionCatalog.TasksExecutor, "kimi-k3", ownerId);

        chain.Should().BeEquivalentTo(new[] { "kimi-k3", "deepseek-v4-pro" },
            opts => opts.WithStrictOrdering(),
            "без персоны хвост — из общего слота владельца, как до починки");
    }

    [Fact]
    public void ResolveChain_СценарийИнцидента_DesignerСПресетомКими()
    {
        // Инцидент 2026-08-26: персона Майя (designer), TierStrong = preset «Сильный — Кими».
        // Старт на kimi-k3 → цепочка обязана продолжиться opus[1m] и glm-5.3[1m] из её
        // матрицы, а не обрубаться на хвосте общего слота «Мощный».
        var (resolver, ownerId, persona) = BuildIncidentResolver();

        var chain = resolver.ResolveChain(LocalActionCatalog.TasksExecutor, "kimi-k3", ownerId, persona);

        chain.Should().BeEquivalentTo(new[] { "kimi-k3", "opus[1m]", "glm-5.3[1m]" },
            opts => opts.WithStrictOrdering(),
            "цепочка строится по матрице специальности дизайнера");
    }

    [Fact]
    public void ExecutorModel_ЗадачаБерётМатрицуПерсоны()
    {
        // §8 связка: уровень задачи разворачивается по матрице персоны-исполнителя.
        var (resolver, app, users, _, _, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        var task = new TaskItem { Title = "t", OwnerId = u1.Id, ModelTier = ModelTier.Strong };
        var persona = new Persona { Specialty = PersonaSpecialty.None, TierStrong = "persona-opus" };

        Assert.Equal("persona-opus", resolver.ExecutorModel(task, persona, u1.Id));
    }

    [Fact]
    public void ExecutorModel_ЯчейкаПерсоныБезУровня_ДефолтМестаStrong()
    {
        // Исполнение задачи: персона с ячейкой «сильная», без ModelTier и без уровня задачи —
        // место tasks-executor (Strong) разворачивает её ячейку (дефект 2-й итерации: раньше
        // такая ячейка молча не срабатывала).
        var (resolver, _, users, _, _, _) = BuildResolverWithSpecialty();
        var u1 = users.Add("u1", "p", "user");
        var task = new TaskItem { Title = "t", OwnerId = u1.Id }; // без ModelTier
        var persona = new Persona { Specialty = PersonaSpecialty.None, TierStrong = "persona-opus" };

        Assert.Equal("persona-opus", resolver.ExecutorModel(task, persona, u1.Id));
    }

    [Fact]
    public void Resolver_БезНазначения_СлотКаталога()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus", ModelTierMedium = "sonnet" });
        var resolver = new ModelAssignmentResolver(appSettings, Store(config));

        // chat-new/chat-persona — сильная; сабагенты и LLM-канал модулей — средняя
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatNew));
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatPersona));
        Assert.Equal("sonnet", resolver.Resolve(LocalActionCatalog.SubagentConsultant));
        Assert.Equal("sonnet", resolver.Resolve(LocalActionCatalog.ModulesLlm));
    }

    [Fact]
    public void Resolver_НазначениеАдмина_ПеребиваетСлотКаталога()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus", ModelTierWeak = "haiku" });
        var store = Store(config);
        store.Set(LocalActionCatalog.ChatNew, "tier:weak");
        store.Set(LocalActionCatalog.ChatPersona, "glm-5.2");
        var resolver = new ModelAssignmentResolver(appSettings, store);

        Assert.Equal("haiku", resolver.Resolve(LocalActionCatalog.ChatNew));
        Assert.Equal("glm-5.2", resolver.Resolve(LocalActionCatalog.ChatPersona));
    }

    [Fact]
    public void Resolver_ПустыеСлоты_ОтдаютРешениеCLI()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var resolver = new ModelAssignmentResolver(new AppSettingsService(config), Store(config));

        Assert.Null(resolver.Resolve(LocalActionCatalog.ChatNew));
    }

    [Fact]
    public void Resolver_ЛокальИDirect_АгентномуМестуНепригодны()
    {
        // «local» и direct:-модель в назначении агентного места игнорируются — место уходит
        // на свой слот каталога (агентной сессии нужны инструменты CLI)
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus" });
        var store = Store(config);
        store.Set(LocalActionCatalog.ChatNew, LocalActionOverridesStore.LocalRoute);
        store.Set(LocalActionCatalog.ChatPersona, "direct:free/model");
        var resolver = new ModelAssignmentResolver(appSettings, store);

        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatNew));
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatPersona));
    }

    [Fact]
    public async Task Пресеты_НеТрогаютАгентныеМеста()
    {
        // Применение пресета не должно сносить назначения агентных мест
        var config = ConfigWithTempData(new()
        {
            ["Ollama:Model"] = "",
            ["ModelCatalog:QueryCli"] = "false", // каталог не спавнит настоящий claude
        });
        var store = Store(config);
        store.Set(LocalActionCatalog.ChatNew, "tier:weak");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var presets = new LocalActionPresetService(store, router, Ollama(config),
            new ModelCatalogService(new LlmProviderRegistry(config),
                new NullHttpFactory(), config),
            config, NullLogger<LocalActionPresetService>.Instance);

        await presets.ApplyAsync(ActionPreset.Recommended);

        Assert.Equal("tier:weak", store.TryGet(LocalActionCatalog.ChatNew));
        // Фоновое действие получило маршрут пресета (слот по профилю: Small → слабая)
        Assert.Equal(LocalActionOverridesStore.TierRoute(ModelTier.Weak),
            store.TryGet(LocalActionCatalog.NotesTags));
    }

    // --- Per-user слоты тиров ---

    private static (AppSettingsService AppSettings, UserStore Users, UserModelTierResolver UserTiers, ModelAssignmentResolver Resolver)
        BuildResolverWithUserTiers(Dictionary<string, string?>? extraCfg = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new Dictionary<string, string?> { ["Ollama:Model"] = "" };
        if (extraCfg is not null)
            foreach (var kv in extraCfg) cfg[kv.Key] = kv.Value;
        var config = ConfigWithTempData(cfg);
        var appSettings = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var userTiers = new UserModelTierResolver(users, appSettings);
        var store = Store(config);
        var resolver = new ModelAssignmentResolver(appSettings, store, userTiers);
        return (appSettings, users, userTiers, resolver);
    }

    [Fact]
    public void Resolver_UserTierOverride_WinsOverGlobal()
    {
        var (settings, users, _, resolver) = BuildResolverWithUserTiers();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, strong: "user-sonnet", null, null);

        Assert.Equal("user-sonnet", resolver.Resolve(LocalActionCatalog.ChatNew, ownerId: user.Id));
    }

    [Fact]
    public void Resolver_WithoutOwnerId_IgnoresUserTiers()
    {
        var (settings, users, _, resolver) = BuildResolverWithUserTiers();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, strong: "user-sonnet", null, null);

        // Без ownerId — старое поведение: общий слот
        Assert.Equal("global-opus", resolver.Resolve(LocalActionCatalog.ChatNew));
    }

    [Fact]
    public void Resolver_TierRoute_UsesUserSlot()
    {
        var (settings, users, _, resolver) = BuildResolverWithUserTiers();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus", ModelTierWeak = "global-haiku" });
        var user = users.Add("u1", "password123", "user");
        // chat-new по каталогу — Strong tier
        users.SetModelTiers(user.Id, "user-sonnet", null, null);

        Assert.Equal("user-sonnet", resolver.Resolve(LocalActionCatalog.ChatNew, ownerId: user.Id));
    }

    [Fact]
    public void Resolver_EmptyUserTier_FallsBackToGlobal()
    {
        var (settings, users, _, resolver) = BuildResolverWithUserTiers();
        settings.Save(new AppSettings { ModelTierMedium = "global-sonnet" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "user-sonnet", null);
        users.SetModelTiers(user.Id, null, "", null);

        Assert.Equal("global-sonnet", resolver.Resolve(LocalActionCatalog.SubagentConsultant, ownerId: user.Id));
    }

    [Fact]
    public void Resolver_ExplicitAssignment_AdminStillWinsOverUserTier()
    {
        var (settings, users, _, resolver) = BuildResolverWithUserTiers();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, strong: "user-sonnet", null, null);

        // Явная модель в назначении админа всё ещё сильнее per-user слота
        Assert.Equal("admin-opus", resolver.Resolve(LocalActionCatalog.ChatNew, "admin-opus", user.Id));
    }

    // --- Per-user слоты в фоновой ветке (CheapTextRunner) ---
    // Зеркало тестов ModelAssignmentResolver выше, но для дешёвого раннера: личный слот
    // владельца должен пробиваться в фон, а не только в агентные места.

    // Дешёвый раннер с per-user слотами: config нужен, чтобы строить Ollama/Cloud для раннера.
    private static (IConfiguration Config, AppSettingsService Settings, UserStore Users,
        UserModelTierResolver UserTiers, LocalActionOverridesStore Store, LocalActionRouter Router)
        BuildRunnerWithUserTiers(Dictionary<string, string?>? extraCfg = null)
    {
        var cfg = new Dictionary<string, string?> { ["Ollama:Model"] = "" };
        if (extraCfg is not null)
            foreach (var kv in extraCfg) cfg[kv.Key] = kv.Value;
        var config = ConfigWithTempData(cfg);
        var appSettings = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var userTiers = new UserModelTierResolver(users, appSettings);
        var store = Store(config);
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        return (config, appSettings, users, userTiers, store, router);
    }

    private static CheapTextRunner Runner(IConfiguration config, LocalActionRouter router, IOneShotRunner claude,
        AppSettingsService appSettings, UserModelTierResolver userTiers) =>
        new(router, Ollama(config), Cloud(config), claude, NullLogger<CheapTextRunner>.Instance,
            appSettings, userTiers);

    [Fact]
    public async Task CheapRunner_МаршрутСлот_БерётЛичныйСлотВладельца()
    {
        var (config, settings, users, userTiers, store, router) = BuildRunnerWithUserTiers();
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        settings.Save(new AppSettings { ModelTierMedium = "global-glm" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "user-sonnet", null);
        var claude = new FakeOneShot();
        var runner = Runner(config, router, claude, settings, userTiers);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku", ownerId: user.Id);

        Assert.Equal("CLAUDE[user-sonnet]:prompt-text", result);
        Assert.Equal(["user-sonnet"], claude.Calls);
    }

    [Fact]
    public async Task CheapRunner_БезOwnerId_БерётГлобальныйСлот()
    {
        // Личный слот есть, но ownerId не передан — поведение прежнее: общий слот.
        // Регресс-страховка для действий категории C (системные, без владельца).
        var (config, settings, users, userTiers, store, router) = BuildRunnerWithUserTiers();
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        settings.Save(new AppSettings { ModelTierMedium = "global-glm" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "user-sonnet", null);
        var claude = new FakeOneShot();
        var runner = Runner(config, router, claude, settings, userTiers);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku");

        Assert.Equal("CLAUDE[global-glm]:prompt-text", result);
    }

    [Fact]
    public async Task CheapRunner_ПустойЛичныйСлот_ПадаетНаГлобальный()
    {
        var (config, settings, users, userTiers, store, router) = BuildRunnerWithUserTiers();
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        settings.Save(new AppSettings { ModelTierMedium = "global-glm" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "", null); // пустой личный medium → откат на глобальный
        var claude = new FakeOneShot();
        var runner = Runner(config, router, claude, settings, userTiers);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku", ownerId: user.Id);

        Assert.Equal("CLAUDE[global-glm]:prompt-text", result);
    }

    [Fact]
    public async Task CheapRunner_НеизвестныйOwnerId_НеПадает()
    {
        // Удалённый пользователь — ownerId в системе не найден → тихо на общий слот.
        var (config, settings, _, userTiers, store, router) = BuildRunnerWithUserTiers();
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        settings.Save(new AppSettings { ModelTierMedium = "global-glm" });
        var claude = new FakeOneShot();
        var runner = Runner(config, router, claude, settings, userTiers);

        var result = await runner.RunAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku", ownerId: "ghost-user");

        Assert.Equal("CLAUDE[global-glm]:prompt-text", result);
    }

    [Fact]
    public async Task CheapRunnerDetailed_МаршрутСлот_БерётЛичныйСлот()
    {
        // Вторая точка EffectiveFallback — RunDetailedAsync (действия с расходом).
        var (config, settings, users, userTiers, store, router) = BuildRunnerWithUserTiers();
        store.Set(LocalActionCatalog.NotesTags, "tier:medium");
        settings.Save(new AppSettings { ModelTierMedium = "global-glm" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "user-sonnet", null);
        var claude = new FakeOneShot();
        var runner = Runner(config, router, claude, settings, userTiers);

        var result = await runner.RunDetailedAsync(LocalActionCatalog.NotesTags, "prompt-text", "haiku", ownerId: user.Id);

        Assert.Equal("CLAUDE[user-sonnet]:prompt-text", result.Text);
        Assert.Equal(["user-sonnet"], claude.Calls);
    }

    // Direct-маршрут через CloudCheapClient пробрасывает ОБЛАЧНЫЙ лимит вывода
    // (CloudNumPredict профиля), а не локальный NumPredict — иначе план с большим JSON
    // обрежется по лимиту (прод 2026-08-05). Тест перехватывает HTTP и читает max_tokens
    // прямо из тела запроса к провайдеру: 8192 (Large.CloudNumPredict), не 1024 (Large.NumPredict).
    [Fact]
    public async Task CheapRunner_DirectМаршрут_ПробрасываетОблачныйЛимит()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = new Dictionary<string, string?>
        {
            ["Ollama:Model"] = "",
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            // Включаем openrouter в Cloud, чтобы direct-маршрут реально пошёл в HTTP.
            ["LlmProviders:openrouter:ApiKey"] = "test-key",
            ["LlmProviders:openrouter:AnthropicBaseUrl"] = "https://openrouter.ai/api",
            ["LlmProviders:openrouter:ApiBaseUrl"] = "https://openrouter.ai/api/v1",
            ["OpenRouter:Provider"] = "openrouter",
            ["OpenRouter:DirectModels:0:Id"] = "nvidia/nemotron:free",
        };
        var config = TestConfig.Build(cfg);
        var store = Store(config);
        // Перенаправляем team-implement-plan на direct-модель: это Large-профиль
        // (1024 локально, 8192 облачно). Без этого fix'a 1024 уйдёт в max_tokens.
        store.Set(LocalActionCatalog.TeamImplementPlan, CloudCheapClient.RoutePrefix + "nvidia/nemotron:free");
        var router = new LocalActionRouter(Ollama(config), store, config, NullLogger<LocalActionRouter>.Instance);
        var capture = new CapturingHttpHandler();
        var cloud = new CloudCheapClient(new SingleFactory(capture), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var claude = new FakeOneShot();
        var runner = new CheapTextRunner(router, Ollama(config), cloud, claude,
            NullLogger<CheapTextRunner>.Instance);

        await runner.RunAsync(LocalActionCatalog.TeamImplementPlan, "p", ownerId: "u");

        var body = capture.LastBody;
        Assert.NotNull(body);
        Assert.Contains("\"max_tokens\":8192", body);
        Assert.DoesNotContain("\"max_tokens\":1024", body);
    }

    // --- Эффективный резолв для показа «Сейчас пойдёт» (Preview, ADR-007 §5 п.5) ---
    // Та же кодовая дорога, что боевой резолв: источник, эффективный уровень и раскрытие пресета.

    [Fact]
    public void Preview_ЯчейкаПерсоныБезУровня_ИсточникИУровеньМеста()
    {
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer { Specialties = { ["backendExecutor"] = Tmpl() } });

        var d = resolver.Preview(LocalActionCatalog.ChatPersona,
            new Persona { Specialty = PersonaSpecialty.BackendExecutor, TierStrong = "persona-opus" },
            PersonaSpecialty.BackendExecutor, "u1", null);

        Assert.Equal("persona-opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.PersonaCell, d.Source);
        Assert.Equal(ModelTier.Strong, d.EffectiveTier);
        Assert.Equal("place", d.TierOrigin);
    }

    [Fact]
    public void Preview_ЯвнаяМодельПерсоны_ИсточникPersonaModel()
    {
        var (resolver, _, users, _, _, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");

        var d = resolver.Preview(null,
            new Persona { Model = "glm-5.2" }, PersonaSpecialty.None, "u1", null);

        Assert.Equal("glm-5.2", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.PersonaModel, d.Source);
        Assert.Null(d.Preset);
    }

    [Fact]
    public void Preview_PresetВЯчейке_РаскрытиеИЦепочка()
    {
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "opus", "glm-5.2") } });

        var d = resolver.Preview(LocalActionCatalog.ChatPersona,
            new Persona { Specialty = PersonaSpecialty.None, TierStrong = "preset:p1" },
            PersonaSpecialty.None, "u1", null);

        Assert.Equal("opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.PersonaCell, d.Source);
        Assert.NotNull(d.Preset);
        Assert.Equal("p1", d.Preset!.Id);
        Assert.False(d.Preset.Broken);
        Assert.Equal(new[] { "opus", "glm-5.2" }, d.Chain);
    }

    [Fact]
    public void Preview_БитаяСсылкаПресета_ModelNullBroken()
    {
        var (resolver, _, users, _, _, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");

        var d = resolver.Preview(LocalActionCatalog.ChatPersona,
            new Persona { Specialty = PersonaSpecialty.None, TierStrong = "preset:missing" },
            PersonaSpecialty.None, "u1", null);

        Assert.Null(d.Model);
        Assert.True(d.PresetBroken);
        Assert.Equal("missing", d.Preset!.Id);
    }

    [Fact]
    public void Preview_ПерсоныНет_НазначениеАдминаМеста()
    {
        var (resolver, _, users, _, _, store) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        store.Set(LocalActionCatalog.ChatPersona, "sonnet-5");

        var d = resolver.Preview(LocalActionCatalog.ChatPersona, null, PersonaSpecialty.None, "u1", null);

        Assert.Equal("sonnet-5", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.PlaceAssignment, d.Source);
    }

    [Fact]
    public void Preview_ПерсоныНет_ДефолтМеста_СлотВладельца()
    {
        var (resolver, app, users, _, _, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var u1 = users.Add("u1", "p", "user");
        users.SetModelTiers(u1.Id, strong: "user-opus", null, null);

        var d = resolver.Preview(LocalActionCatalog.ChatPersona, null, PersonaSpecialty.None, u1.Id, null);

        Assert.Equal("user-opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.OwnerSlot, d.Source);
        Assert.Equal(ModelTier.Strong, d.EffectiveTier);
        Assert.Equal("place", d.TierOrigin);
    }

    [Fact]
    public void Preview_OverrideTierЗадачи_СильнееУровняПерсоны()
    {
        var (resolver, _, users, _, _, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");

        // overrideTier=Weak при уровне персоны Strong берёт слабую ячейку; источник уровня — задача
        var d = resolver.Preview(LocalActionCatalog.ChatPersona,
            new Persona { TierStrong = "persona-opus", TierWeak = "persona-haiku", ModelTier = ModelTier.Strong },
            PersonaSpecialty.None, "u1", ModelTier.Weak);

        Assert.Equal("persona-haiku", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.PersonaCell, d.Source);
        Assert.Equal("task", d.TierOrigin);
        Assert.Equal(ModelTier.Weak, d.EffectiveTier);
    }

    // --- Ветка specialty-only: Preview по специальности без персоны (карточка специальности) ---

    [Fact]
    public void Preview_СпециальностьБезПерсоны_ЯчейкаСпециальности()
    {
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus", defaultTier: ModelTier.Strong) },
        });

        var d = resolver.Preview(null, null, PersonaSpecialty.BackendExecutor, "u1", null);

        Assert.Equal("spec-opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.SpecialtyCell, d.Source);
        Assert.Equal("specialty", d.TierOrigin);
        Assert.Equal(ModelTier.Strong, d.EffectiveTier);
    }

    [Fact]
    public void Preview_СпециальностьБезПерсоны_ПресетВЯчейке()
    {
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "opus", "glm-5.2") },
            Specialties = { ["backendExecutor"] = Tmpl(strong: "preset:p1", defaultTier: ModelTier.Strong) },
        });

        var d = resolver.Preview(null, null, PersonaSpecialty.BackendExecutor, "u1", null);

        Assert.Equal("opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.SpecialtyCell, d.Source);
        Assert.NotNull(d.Preset);
        Assert.False(d.Preset!.Broken);
        Assert.Equal(new[] { "opus", "glm-5.2" }, d.Chain);
    }

    [Fact]
    public void Preview_СпециальностьБезПерсоны_ПустаяЯчейка_СлотВладельца()
    {
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus" });
        var u1 = users.Add("u1", "p", "user");
        users.SetModelTiers(u1.Id, strong: "user-opus", null, null);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Strong) }, // матрица пуста, только уровень
        });

        var d = resolver.Preview(null, null, PersonaSpecialty.BackendExecutor, u1.Id, null);

        Assert.Equal("user-opus", d.Model);
        Assert.Equal(ModelAssignmentResolver.ModelSource.OwnerSlot, d.Source);
        Assert.Equal("specialty", d.TierOrigin);
    }

    [Fact]
    public void Preview_СпециальностьБезПерсоны_БезУровня_Пусто()
    {
        // Нет ни overrideTier, ни DefaultTier, ни place — уровень не определён, превью
        // пустое (не падает на место: его нет).
        var (resolver, _, users, _, specialty, _) = BuildResolverWithSpecialty();
        users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus") }, // ячейка есть, но уровень не задан
        });

        var d = resolver.Preview(null, null, PersonaSpecialty.BackendExecutor, "u1", null);

        Assert.Null(d.Model);
        Assert.Null(d.Source);
    }

    // --- Сторож соответствия превью боевой дороге (волна настройки моделей 2026-08-14) ---
    // Превью задачи/чата обязано совпадать с моделью боевого запуска хода: TaskExecutionService
    // → ExecutorModel → Session.Model → ClaudeSession.EffectiveTurnChain → ResolveChain.
    // Тесты прогоняют один и тот же вход через обе дороги и сверяют результат.

    private static Persona PersonaSnapshot(Persona p, bool dropModel) => new()
    {
        Model = dropModel ? null : p.Model,
        ModelTier = p.ModelTier,
        Specialty = p.Specialty,
        TierStrong = p.TierStrong,
        TierMedium = p.TierMedium,
        TierWeak = p.TierWeak,
    };

    [Fact]
    public void Сторож_ПревьюЗадачи_СовпадаетСБоевойExecutorModel()
    {
        // Матрица сценариев «уровень задачи × модель персоны × матрицы»: превью контроллера
        // (Preview с санитизацией персоны) обязано давать ту же модель, что боевой запуск.
        // Полная боевая дорога: SessionManager.ResolveDefaultModel(TasksExecutor,
        // ExecutorModel(task, persona)) — Resolve места добирает ответ, когда формула
        // задачи отдала null.
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus", ModelTierWeak = "slot-haiku" });
        var u1 = users.Add("u1", "p", "user");
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = Tmpl(strong: "spec-opus", weak: "spec-haiku") },
        });

        var personas = new (string? Model, ModelTier? Tier, string? Cell)[]
        {
            (null, null, "persona-opus"),                    // ячейка без уровня
            ("persona-glm", null, null),                     // явная модель, без уровня задачи
            (null, ModelTier.Strong, "persona-opus"),        // уровень персоны
            ("persona-glm", ModelTier.Weak, "persona-haiku"),// модель+уровень (модель победит без уровня задачи)
        };
        foreach (var (model, tier, cell) in personas)
        {
            var persona = new Persona
            {
                Model = model,
                ModelTier = tier,
                Specialty = PersonaSpecialty.BackendExecutor,
                TierStrong = cell,
                TierWeak = cell is null ? null : "persona-haiku",
            };
            foreach (var taskTier in new ModelTier?[] { null, ModelTier.Strong, ModelTier.Weak })
            {
                var task = new TaskItem { Title = "t", OwnerId = u1.Id, ModelTier = taskTier };
                var combat = resolver.Resolve(LocalActionCatalog.TasksExecutor,
                    resolver.ExecutorModel(task, persona, u1.Id), u1.Id);

                // Дорога превью: санитизация персоны при заданном уровне задачи (ModelsController.
                // TaskPreview) + Preview(tasks-executor).
                var effective = PersonaSnapshot(persona, dropModel: taskTier is not null);
                var d = resolver.Preview(LocalActionCatalog.TasksExecutor, effective,
                    effective.Specialty, u1.Id, taskTier);

                d.Model.Should().Be(combat,
                    $"превью задачи обязано совпадать с боевой моделью (persona model={model}, tier={tier}, taskTier={taskTier})");
            }
        }
    }

    [Fact]
    public void Сторож_ПревьюЗадачи_БезПерсоны_СовпадаетСБоевойДорогой()
    {
        // Задача без персоны: бой — ExecutorModel(task, null) → null → ResolveDefaultModel
        // берёт слот дефолта места; превью — Preview с заглушкой-персоной (ModelsController.
        // TaskPreview) — обязано дать тот же ответ.
        var (resolver, app, users, _, _, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "slot-opus", ModelTierWeak = "slot-haiku" });
        var u1 = users.Add("u1", "p", "user");

        foreach (var taskTier in new ModelTier?[] { null, ModelTier.Strong, ModelTier.Weak })
        {
            var task = new TaskItem { Title = "t", OwnerId = u1.Id, ModelTier = taskTier };
            var combat = resolver.Resolve(LocalActionCatalog.TasksExecutor,
                resolver.ExecutorModel(task, null, u1.Id), u1.Id);
            var d = resolver.Preview(LocalActionCatalog.TasksExecutor, new Persona(),
                PersonaSpecialty.None, u1.Id, taskTier);
            d.Model.Should().Be(combat, $"без персоны: taskTier={taskTier}");
        }
    }

    [Fact]
    public void Сторож_ПревьюЧата_СовпадаетСEffectiveTurnChain()
    {
        // Контекст чата: цепочка превью = боевой EffectiveTurnChain (ResolveChain места по
        // Session.Model), модель превью = первый шаг. Сценарии: замороженная модель из
        // пресета слота, модель вне пресетов (хвост тира), пустая модель (место решает).
        var (resolver, app, users, _, specialty, _) = BuildResolverWithSpecialty();
        app.Save(new AppSettings { ModelTierStrong = "preset:main" });
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("main", "opus", "kimi-k3", "glm-5.2") },
        });
        var u1 = users.Add("u1", "p", "user");

        foreach (var sessionModel in new string?[] { null, "glm-5.2", "stranger-model" })
        {
            var combatChain = resolver.ResolveChain(LocalActionCatalog.ChatNew, sessionModel, u1.Id);
            // Дорога превью: замороженная модель — явная для места; пустая — Preview места
            var frozen = !string.IsNullOrWhiteSpace(sessionModel);
            var previewModel = frozen ? sessionModel!.Trim()
                : resolver.Preview(LocalActionCatalog.ChatNew, null, PersonaSpecialty.None, u1.Id, null).Model;
            previewModel.Should().Be(combatChain.FirstOrDefault(),
                $"модель превью чата = первый шаг боевой цепочки (sessionModel={sessionModel})");
        }
    }

    private sealed class SingleFactory(System.Net.Http.HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHttpHandler : System.Net.Http.HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        private const string OpenRouterJson = """
            {"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"OK"}}],
             "usage":{"prompt_tokens":1,"completion_tokens":1}}
            """;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(OpenRouterJson, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
