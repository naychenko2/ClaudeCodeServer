using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Маршрутизация фоновых действий локаль(Ollama)/claude: LocalActionRouter + CheapTextRunner.
public class LocalActionRoutingTests
{
    private static IConfiguration Config(Dictionary<string, string?> d) =>
        new ConfigurationBuilder().AddInMemoryCollection(d).Build();

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
    // бросающий InvalidOperationException); emptyModel — успешный, но пустой ответ.
    private sealed class FakeOneShot(string? failModel = null, string? emptyModel = null) : IOneShotRunner
    {
        public readonly List<string?> Calls = [];

        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Calls.Add(model);
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
    public void Resolver_МодельПерсоны_СвояСильнееУровня_УровеньСильнееМеста()
    {
        var config = ConfigWithTempData(new() { ["Ollama:Model"] = "" });
        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings { ModelTierStrong = "opus", ModelTierMedium = "sonnet" });
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, null, weak: "user-haiku");
        var resolver = new ModelAssignmentResolver(appSettings, Store(config),
            new UserModelTierResolver(users, appSettings));

        // Явная модель персоны сильнее её уровня
        Assert.Equal("glm-5.2", resolver.PersonaModel(
            new Persona { Model = "glm-5.2", ModelTier = ModelTier.Weak }, user.Id));
        // Без своей модели — уровень персоны, развёрнутый по слотам ВЛАДЕЛЬЦА (не маркер)
        Assert.Equal("user-haiku", resolver.PersonaModel(
            new Persona { ModelTier = ModelTier.Weak }, user.Id));
        Assert.Equal("opus", resolver.PersonaModel(new Persona { ModelTier = ModelTier.Strong }, user.Id));
        // Ни модели, ни уровня — null: место решает само
        Assert.Null(resolver.PersonaModel(new Persona(), user.Id));
        Assert.Equal("opus", resolver.Resolve(LocalActionCatalog.ChatPersona,
            resolver.PersonaModel(new Persona(), user.Id), user.Id));
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
}
