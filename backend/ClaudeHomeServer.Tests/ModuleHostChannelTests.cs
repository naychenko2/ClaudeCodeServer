using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests;

// LLM-канал модулей (контракт §10, ТЗ R10–R13): host-токен и middleware /api/host/**
// (CT-13), каталог LLM-действий модулей (CT-12), эндпоинт complete (CT-14, CT-15), учёт R13.
public class ModuleHostChannelTests
{
    // ---------- инфраструктура ----------

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string EchoManifest(string id) =>
        """
        {"schemaVersion":"1.0","id":"__ID__","version":"1.0.0","displayName":"Echo (__ID__)",
         "backend":{"baseUrl":"http://localhost:9/__ID__","healthPath":"/health","routePrefix":"/api/modules/__ID__"},
         "scopes":["echo.read"],
         "llm":{"actions":[{"key":"echo-echo","title":"Эхо через модель","profile":"small","defaultLocal":true}]}}
        """.Replace("__ID__", id);

    private static IConfiguration Config(string dataDir, string modulesRoot) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            ["Modules:Path"] = modulesRoot,
        }).Build();

    // Каталог модулей + реестр с одним манифестом (конструктор реестра регистрирует
    // llm-действия в динамическом слое LocalActionCatalog — как на старте сервера)
    private static (ServiceProvider Sp, LoadedModule Module) BuildEcho(string dataDir, string id = "echo")
    {
        var modulesRoot = Path.Combine(dataDir, "modules");
        Directory.CreateDirectory(Path.Combine(modulesRoot, id));
        File.WriteAllText(Path.Combine(modulesRoot, id, "module.json"), EchoManifest(id));

        var services = new ServiceCollection();
        services.AddSingleton(Config(dataDir, modulesRoot));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new Helpers.FakeHostEnvironment("Production"));
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<UserStore>();
        services.AddSingleton<FeatureFlagService>();
        services.AddSingleton<ModuleTokenService>();
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<ModuleRegistry>().Get(id)!);
    }

    private static async Task<(int Status, string Body, bool ReachedNext, HttpContext Ctx)> InvokeHostAsync(
        ServiceProvider sp, string path, string? authorization)
    {
        var app = new ApplicationBuilder(sp);
        app.UseHostChannel();
        var reached = false;
        app.Run(ctx =>
        {
            reached = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var ctx2 = new DefaultHttpContext { RequestServices = sp };
        ctx2.Request.Method = "POST";
        ctx2.Request.Path = path;
        ctx2.Response.Body = new MemoryStream();
        if (authorization is not null)
            ctx2.Request.Headers.Authorization = authorization;

        await pipeline(ctx2);
        ctx2.Response.Body.Position = 0;
        var body = await new StreamReader(ctx2.Response.Body).ReadToEndAsync();
        return (ctx2.Response.StatusCode, body, reached, ctx2);
    }

    // ---------- R11 / CT-13: обмен токена и разграничение каналов ----------

    [Theory]
    [InlineData("gateway")]
    [InlineData("mcp")]
    public async Task Обмен_принимает_рабочие_каналы_модуля(string chan)
    {
        var (sp, module) = BuildEcho(TempDir());
        var token = sp.GetRequiredService<ModuleTokenService>().Issue(module, "user-1", "Tester", chan);

        var (status, _, reached, ctx) = await InvokeHostAsync(sp, "/api/host/token", $"Bearer {token}");

        Assert.Equal(200, status);
        Assert.True(reached);
        Assert.Same(module, ctx.Items[HostChannelMiddleware.ModuleItem]);
        Assert.Equal("user-1", ctx.Items[HostChannelMiddleware.UserIdItem]);
    }

    [Fact]
    public async Task Обмен_отвергает_host_токен()
    {
        // host-токен нельзя обменять на новый host-токен — вечная пролонгация без рабочего канала
        var (sp, module) = BuildEcho(TempDir());
        var token = sp.GetRequiredService<ModuleTokenService>().Issue(module, "user-1", "Tester", "host");

        var (status, body, reached, _) = await InvokeHostAsync(sp, "/api/host/token", $"Bearer {token}");

        Assert.Equal(401, status);
        Assert.False(reached);
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task Complete_отвергает_chan_gateway()
    {
        // CT-13: chan=gateway на /api/host/llm/complete → 401 (принимается только chan=host)
        var (sp, module) = BuildEcho(TempDir());
        var token = sp.GetRequiredService<ModuleTokenService>().Issue(module, "user-1", "Tester", "gateway");

        var (status, _, reached, _) = await InvokeHostAsync(sp, "/api/host/llm/complete", $"Bearer {token}");

        Assert.Equal(401, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task Complete_пропускает_host_токен()
    {
        var (sp, module) = BuildEcho(TempDir());
        var token = sp.GetRequiredService<ModuleTokenService>().Issue(module, "user-1", "Tester", "host");

        var (status, _, reached, ctx) = await InvokeHostAsync(sp, "/api/host/llm/complete", $"Bearer {token}");

        Assert.Equal(200, status);
        Assert.True(reached);
        Assert.Equal("user-1", ctx.Items[HostChannelMiddleware.UserIdItem]);
        Assert.Equal("Tester", ctx.Items[HostChannelMiddleware.UserNameItem]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not.a.jwt")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Мусор_и_отсутствие_токена_отбиваются_401(string? auth)
    {
        var (sp, _) = BuildEcho(TempDir());

        var (status, _, reached, _) = await InvokeHostAsync(sp, "/api/host/llm/complete", auth);

        Assert.Equal(401, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task Скрытый_флагом_модуль_даёт_403_module_hidden_уже_на_обмене()
    {
        // Пакет §10.2 v1.5: гейт module-{id} стоит на /api/host/token; код host-канала —
        // 403 module_hidden (на gateway тот же гейт даёт 404)
        var (sp, module) = BuildEcho(TempDir());
        var users = sp.GetRequiredService<UserStore>();
        var user = users.Add("bob", "secret-pw", "user");
        users.SetFeatureFlag(user.Id, module.FeatureFlagKey, false);
        var token = sp.GetRequiredService<ModuleTokenService>().Issue(module, user.Id, "Bob", "gateway");

        var (status, body, reached, _) = await InvokeHostAsync(sp, "/api/host/token", $"Bearer {token}");

        Assert.Equal(403, status);
        Assert.False(reached);
        Assert.Contains("module_hidden", body);
    }

    [Fact]
    public void Gateway_passthrough_отвергает_host_токен()
    {
        // CT-13: chan=host на /api/modules/** → 401. Gateway-ветка (б) валидирует строго
        // chan=mcp (TryValidate) — host-токен там негоден.
        var (sp, module) = BuildEcho(TempDir());
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var hostToken = tokens.Issue(module, "user-1", "Tester", "host");

        Assert.False(tokens.TryValidate(hostToken, module, out var sub));
        Assert.Null(sub);
    }

    [Fact]
    public void Host_токен_валиден_и_несёт_имя()
    {
        var (sp, module) = BuildEcho(TempDir());
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var token = tokens.Issue(module, "user-1", "Tester", "host");

        var validated = tokens.Validate(token, module, "host");

        Assert.NotNull(validated);
        Assert.Equal("user-1", validated!.UserId);
        Assert.Equal("Tester", validated.UserName);
        Assert.Equal("host", validated.Chan);
    }

    // ---------- R10 / CT-12: LLM-действия модулей в каталоге ----------

    [Fact]
    public void Действие_модуля_регистрируется_в_каталоге()
    {
        var (_, module) = BuildEcho(TempDir(), "llmcat");

        var action = LocalActionCatalog.Find(module.LlmActionKey("echo-echo"));

        Assert.NotNull(action);
        Assert.Equal("module:llmcat:echo-echo", action!.Key);
        Assert.Equal("Эхо через модель", action.Title);
        Assert.Equal("Echo (llmcat)", action.Group);          // группа в UI — displayName модуля
        Assert.Equal(CheapProfile.Small, action.Profile);
        Assert.True(action.DefaultLocal);
        Assert.Contains(LocalActionCatalog.All, a => a.Key == action.Key);
        Assert.True(LocalActionCatalog.IsKnown(action.Key));
    }

    [Theory]
    // profile вне small|text|large
    [InlineData("badprof", """{"key":"a-b","title":"T","profile":"huge","defaultLocal":true}""")]
    // ключ не slug
    [InlineData("badkey", """{"key":"Bad_Key","title":"T","profile":"small","defaultLocal":true}""")]
    // дубликат ключа
    [InlineData("dupkey", """{"key":"a-b","title":"T","profile":"small"},{"key":"a-b","title":"T2","profile":"text"}""")]
    public void Невалидное_llm_действие_отклоняет_модуль(string id, string actionsJson)
    {
        var dataDir = TempDir();
        var modulesRoot = Path.Combine(dataDir, "modules");
        Directory.CreateDirectory(Path.Combine(modulesRoot, id));
        File.WriteAllText(Path.Combine(modulesRoot, id, "module.json"),
            """
            {"schemaVersion":"1.0","id":"__ID__","version":"1.0.0","displayName":"Bad",
             "backend":{"baseUrl":"http://localhost:9/__ID__","healthPath":"/health","routePrefix":"/api/modules/__ID__"},
             "llm":{"actions":[__ACTIONS__]}}
            """.Replace("__ID__", id).Replace("__ACTIONS__", actionsJson));

        var registry = new ModuleRegistry(Config(dataDir, modulesRoot), NullLogger<ModuleRegistry>.Instance);

        Assert.Null(registry.Get(id));
        Assert.Null(LocalActionCatalog.Find($"module:{id}:a-b"));
    }

    [Fact]
    public void Маршрут_модульного_действия_настраивается_без_рестарта()
    {
        // CT-12: смена маршрута — через тот же стор оверрайдов, что у встроенных; роутер
        // читает стор на каждом вызове, рестарт не нужен
        var dataDir = TempDir();
        var (_, module) = BuildEcho(dataDir, "llmroute");
        var key = module.LlmActionKey("echo-echo");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            ["Ollama:Model"] = "",
        }).Build();
        var store = new LocalActionOverridesStore(cfg, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), cfg, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, cfg, NullLogger<LocalActionRouter>.Instance);

        // Дефолт из манифеста: defaultLocal=true → Kind=Local
        var before = router.Resolve(key);
        Assert.Equal(RouteKind.Local, before.Kind);
        Assert.Equal(RouteSource.Default, before.Source);

        Assert.True(store.Set(key, "haiku"));
        var after = router.Resolve(key);
        Assert.Equal(RouteKind.Model, after.Kind);
        Assert.Equal("haiku", after.Model);
        Assert.Equal(RouteSource.Admin, after.Source);
    }

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ---------- R12 / CT-14, CT-15: POST /api/host/llm/complete ----------

    private sealed class FakeCheap(Func<OneShotResult>? onDetailed = null) : ICheapTextRunner
    {
        public string? LastActionKey;
        public object? LastJsonFormat;
        public string? LastOwnerId;
        public TimeSpan? LastTimeout;

        public bool UsesLocal(string actionKey) => false;
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            LastActionKey = actionKey;
            LastJsonFormat = jsonFormat;
            LastOwnerId = ownerId;
            LastTimeout = timeout;
            if (onDetailed is null) return new OneShotResult("OK", null, 5);
            // Сигнальное значение «ждать отмены» — для теста таймаута
            var result = onDetailed();
            if (result.Text == "__wait__") await Task.Delay(Timeout.Infinite, ct);
            return result;
        }
    }

    private static (HostLlmController Controller, ModuleLlmUsageStore Usage, string DataDir) BuildController(
        LoadedModule module, string userId, ICheapTextRunner cheap,
        HostLlmConcurrencyLimiter? limiter = null, string? dataDir = null)
    {
        dataDir ??= TempDir();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            ["Ollama:Model"] = "",
        }).Build();
        var store = new LocalActionOverridesStore(cfg, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), cfg, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, cfg, NullLogger<LocalActionRouter>.Instance);
        var tokens = new ModuleTokenService(cfg, NullLogger<ModuleTokenService>.Instance);
        var usage = new ModuleLlmUsageStore(cfg, NullLogger<ModuleLlmUsageStore>.Instance);

        var controller = new HostLlmController(tokens, cheap, router,
            limiter ?? new HostLlmConcurrencyLimiter(), usage,
            NullLogger<HostLlmController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Items[HostChannelMiddleware.ModuleItem] = module;
        ctx.Items[HostChannelMiddleware.UserIdItem] = userId;
        ctx.Items[HostChannelMiddleware.UserNameItem] = "Tester";
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, usage, dataDir);
    }

    private static JsonElement Payload(IActionResult result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result).Value;
        return JsonSerializer.SerializeToElement(value);
    }

    private static int StatusOf(IActionResult result) =>
        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode!.Value;

    [Fact]
    public async Task Complete_объявленный_ключ_отдаёт_текст_и_пишет_учёт()
    {
        // CT-14 позитив + R13: факт вызова пишется всегда, маршрут в записи
        var (_, module) = BuildEcho(TempDir(), "llmok");
        var cheap = new FakeCheap();
        var (controller, _, dataDir) = BuildController(module, "user-1", cheap);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, null), default);

        Assert.Equal(200, StatusOf(result));
        var payload = Payload(result);
        Assert.Equal("OK", payload.GetProperty("text").GetString());
        Assert.Equal("local", payload.GetProperty("route").GetString());   // defaultLocal=true из манифеста
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("usage").ValueKind);
        // Неймспейс достроен ядром из aud — модуль назвал ключ без префикса
        Assert.Equal("module:llmok:echo-echo", cheap.LastActionKey);
        Assert.Equal("user-1", cheap.LastOwnerId);

        var lines = File.ReadAllLines(Path.Combine(dataDir, "module-llm-usage.jsonl"));
        var entry = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.Equal("llmok", entry.GetProperty("moduleId").GetString());
        Assert.Equal("echo-echo", entry.GetProperty("action").GetString());
        Assert.Equal("user-1", entry.GetProperty("sub").GetString());
        Assert.Equal("local", entry.GetProperty("route").GetString());
        Assert.True(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.False(entry.TryGetProperty("costUsd", out _));   // провайдер метрик не отдал — полей нет
    }

    [Fact]
    public async Task Complete_обрезает_пробелы_ключа_действия_в_учёте()
    {
        // Ревью: в usage.Record должен идти тот же нормализованный ключ, что пошёл в резолв —
        // иначе " echo-echo " и "echo-echo" расходятся отдельными ключами в агрегации
        var (_, module) = BuildEcho(TempDir(), "llmtrim");
        var cheap = new FakeCheap();
        var (controller, _, dataDir) = BuildController(module, "user-1", cheap);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest(" echo-echo ", "привет", null, null), default);

        Assert.Equal(200, StatusOf(result));
        var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(Path.Combine(dataDir, "module-llm-usage.jsonl")))).RootElement;
        Assert.Equal("echo-echo", entry.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Complete_отдаёт_usage_когда_провайдер_его_вернул()
    {
        var (_, module) = BuildEcho(TempDir(), "llmusage");
        var cheap = new FakeCheap(() => new OneShotResult("OK",
            new OneShotUsage(100, 0, 20, 30, 0.05, "claude-haiku"), 7));
        var (controller, _, dataDir) = BuildController(module, "user-1", cheap);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, null), default);

        var payload = Payload(result);
        var usage = payload.GetProperty("usage");
        Assert.Equal(120, usage.GetProperty("inputTokens").GetInt64());  // input + cache
        Assert.Equal(30, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0.05, usage.GetProperty("costUsd").GetDouble());
        Assert.Equal("claude-haiku", payload.GetProperty("model").GetString());

        var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(Path.Combine(dataDir, "module-llm-usage.jsonl")))).RootElement;
        Assert.Equal(120, entry.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0.05, entry.GetProperty("costUsd").GetDouble());
    }

    [Fact]
    public async Task Complete_необъявленный_ключ_403_action_not_declared()
    {
        // §10.4 v1.6: модуль объявил [echo-echo], но зовёт foo — ключа нет в манифесте
        // вызывающего ⇒ 403 action_not_declared (а не 404, как было до разводки кодов)
        var (_, module) = BuildEcho(TempDir(), "llm403");
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("no-such-action", "привет", null, null), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("action_not_declared", Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_объявленный_но_незарегистрированный_ключ_404_action_unknown()
    {
        // §10.4 v1.6: ключ есть в манифесте вызывающего, но отсутствует в каталоге ядра
        // (рассинхрон — действие отбраковано валидацией либо реестр не прогрет). Это не ошибка
        // модуля, а ядра ⇒ 404 action_unknown. Логируется warning.
        var manifest = new ModuleManifest
        {
            SchemaVersion = "1.0",
            Id = "unreg",
            Version = "1.0.0",
            DisplayName = "Unreg",
            Backend = new ModuleBackend
            {
                BaseUrl = "http://localhost:9/unreg",
                HealthPath = "/health",
                RoutePrefix = "/api/modules/unreg",
            },
            Llm = new ModuleLlm
            {
                Actions = [new ModuleLlmAction { Key = "ghost", Title = "Призрак", Profile = "small", DefaultLocal = true }],
            },
        };
        var module = new LoadedModule(manifest, "/unused");
        // Намеренно НЕ регистрируем module:unreg:ghost в LocalActionCatalog
        Assert.Null(LocalActionCatalog.Find(module.LlmActionKey("ghost")));
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("ghost", "привет", null, null), default);

        Assert.Equal(404, StatusOf(result));
        Assert.Equal("action_unknown", Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_чужой_неймспейс_не_пробивается()
    {
        // CT-14: попытка назвать полный ключ чужого модуля даёт 403 action_not_declared —
        // ядро достраивает неймспейс из aud, и «module:другой:ключ» не совпадает ни с одним
        // ключом манифеста вызывающего. Неймспейс не пробивается по построению.
        var (_, module) = BuildEcho(TempDir(), "llmown");
        BuildEcho(TempDir(), "llmalien");   // чужое действие module:llmalien:echo-echo существует
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("module:llmalien:echo-echo", "привет", null, null), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("action_not_declared", Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_модуль_без_llm_блока_403()
    {
        // §10.1: модуль без блока llm каналом не пользуется
        var manifest = new ModuleManifest
        {
            SchemaVersion = "1.0",
            Id = "nollm",
            Version = "1.0.0",
            DisplayName = "NoLlm",
            Backend = new ModuleBackend
            {
                BaseUrl = "http://localhost:9/nollm",
                HealthPath = "/health",
                RoutePrefix = "/api/modules/nollm",
            },
        };
        var module = new LoadedModule(manifest, "/unused");
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, null), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("action_not_declared", Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_без_prompt_или_action_400()
    {
        var (_, module) = BuildEcho(TempDir(), "llm400");
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());

        Assert.Equal(400, StatusOf(await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", null, null, null), default)));
        Assert.Equal(400, StatusOf(await controller.Complete(
            new HostLlmController.CompleteRequest(null, "привет", null, null), default)));
    }

    [Fact]
    public async Task Complete_промпт_сверх_лимита_413()
    {
        var (_, module) = BuildEcho(TempDir(), "llm413");
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap());
        var huge = new string('x', HostLlmController.MaxPromptBytes + 1);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", huge, null, null), default);

        Assert.Equal(413, StatusOf(result));
        Assert.Equal("prompt_too_large", Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_сверх_конкурентности_429_с_RetryAfter()
    {
        var (_, module) = BuildEcho(TempDir(), "llm429");
        var limiter = new HostLlmConcurrencyLimiter();
        // Занимаем все слоты модуля — пятый вызов должен отбиться
        for (var i = 0; i < HostLlmConcurrencyLimiter.MaxConcurrentPerModule; i++)
            Assert.True(limiter.TryEnter(module.Id));
        var (controller, _, _) = BuildController(module, "user-1", new FakeCheap(), limiter);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, null), default);

        Assert.Equal(429, StatusOf(result));
        Assert.Equal("5", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Complete_отказ_всей_цепочки_503_llm_unavailable()
    {
        // CT-15: последний шаг CheapTextRunner намеренно пробрасывает исключение —
        // граница канала оформляет его в 503, и факт вызова всё равно попадает в учёт
        var (_, module) = BuildEcho(TempDir(), "llm503");
        var cheap = new FakeCheap(() => throw new InvalidOperationException("claude завершился с кодом 1"));
        var (controller, _, dataDir) = BuildController(module, "user-1", cheap);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, null), default);

        Assert.Equal(503, StatusOf(result));
        Assert.Equal("llm_unavailable", Payload(result).GetProperty("error").GetString());

        var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(Path.Combine(dataDir, "module-llm-usage.jsonl")))).RootElement;
        Assert.False(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", entry.GetProperty("outcome").GetString());
        Assert.Equal("llm503", entry.GetProperty("moduleId").GetString());
    }

    [Fact]
    public async Task Complete_таймаут_504_llm_timeout()
    {
        // CT-15: watchdog запроса истёк (timeoutMs модуля может только уменьшить потолок
        // профиля) → 504 llm_timeout
        var (_, module) = BuildEcho(TempDir(), "llm504");
        var cheap = new FakeCheap(() => new OneShotResult("__wait__", null, 0));
        var (controller, _, dataDir) = BuildController(module, "user-1", cheap);

        var result = await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", null, TimeoutMs: 200), default);

        Assert.Equal(504, StatusOf(result));
        Assert.Equal("llm_timeout", Payload(result).GetProperty("error").GetString());

        var entry = JsonDocument.Parse(
            Assert.Single(File.ReadAllLines(Path.Combine(dataDir, "module-llm-usage.jsonl")))).RootElement;
        Assert.False(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("timeout", entry.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Complete_responseFormat_json_пробрасывается_невалидный_400()
    {
        var (_, module) = BuildEcho(TempDir(), "llmfmt");
        var cheap = new FakeCheap();
        var (controller, _, _) = BuildController(module, "user-1", cheap);

        var json = JsonSerializer.SerializeToElement("json");
        Assert.Equal(200, StatusOf(await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", json, null), default)));
        Assert.Equal("json", cheap.LastJsonFormat);

        var invalid = JsonSerializer.SerializeToElement(42);
        Assert.Equal(400, StatusOf(await controller.Complete(
            new HostLlmController.CompleteRequest("echo-echo", "привет", invalid, null), default)));
    }

    [Fact]
    public void Обмен_выпускает_host_токен_с_теми_же_sub_и_aud()
    {
        // §10.2: контроллер обмена выпускает chan=host на тот же sub и aud, expiresIn 3600
        var dataDir = TempDir();
        var (_, module) = BuildEcho(dataDir, "llmxchg");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            ["Ollama:Model"] = "",
        }).Build();
        var tokens = new ModuleTokenService(cfg, NullLogger<ModuleTokenService>.Instance);
        var store = new LocalActionOverridesStore(cfg, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), cfg, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, cfg, NullLogger<LocalActionRouter>.Instance);
        var controller = new HostLlmController(tokens, new FakeCheap(), router,
            new HostLlmConcurrencyLimiter(),
            new ModuleLlmUsageStore(cfg, NullLogger<ModuleLlmUsageStore>.Instance),
            NullLogger<HostLlmController>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Items[HostChannelMiddleware.ModuleItem] = module;
        ctx.Items[HostChannelMiddleware.UserIdItem] = "user-1";
        ctx.Items[HostChannelMiddleware.UserNameItem] = "Tester";
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var result = controller.ExchangeToken();

        Assert.Equal(200, StatusOf(result));
        var payload = Payload(result);
        Assert.Equal(3600, payload.GetProperty("expiresIn").GetInt32());
        var validated = tokens.Validate(payload.GetProperty("token").GetString(), module, "host");
        Assert.NotNull(validated);
        Assert.Equal("user-1", validated!.UserId);
        Assert.Equal("host", validated.Chan);
    }

    // ---------- Партиция rate-limit host-токена (§10.2 v1.5, ревью тимлида) ----------

    [Fact]
    public void Партиция_разная_для_разных_модулей_одного_пользователя()
    {
        // Сценарий шумного соседа: два модуля с одного IP (внутренний адрес ядра) не должны
        // делить счётчик — исчерпание лимита одним не мешает другому
        var dataDir = TempDir();
        BuildEcho(dataDir, "part1");
        var (sp, module2) = BuildEcho(dataDir, "part2");
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var module1 = registry.Get("part1")!;
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var token1 = tokens.Issue(module1, "user-1", "Tester", "host");
        var token2 = tokens.Issue(module2, "user-1", "Tester", "host");

        var key1 = HostChannelMiddleware.TryReadPartitionKey(token1, registry);
        var key2 = HostChannelMiddleware.TryReadPartitionKey(token2, registry);

        Assert.Equal("part1:user-1", key1);
        Assert.Equal("part2:user-1", key2);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Партиция_разная_для_разных_пользователей_одного_модуля()
    {
        var (sp, module) = BuildEcho(TempDir(), "part3");
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var tokenA = tokens.Issue(module, "user-1", "Tester A", "host");
        var tokenB = tokens.Issue(module, "user-2", "Tester B", "host");

        Assert.NotEqual(
            HostChannelMiddleware.TryReadPartitionKey(tokenA, registry),
            HostChannelMiddleware.TryReadPartitionKey(tokenB, registry));
    }

    [Fact]
    public void Партиция_одинакова_для_повторных_токенов_той_же_пары()
    {
        var (sp, module) = BuildEcho(TempDir(), "part4");
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var tokenA = tokens.Issue(module, "user-1", "Tester", "host");
        var tokenB = tokens.Issue(module, "user-1", "Tester", "host");

        Assert.Equal(
            HostChannelMiddleware.TryReadPartitionKey(tokenA, registry),
            HostChannelMiddleware.TryReadPartitionKey(tokenB, registry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not.a.jwt")]
    public void Партиция_null_для_нераспознанного_токена_вызывающий_откатится_на_IP(string? token)
    {
        var (sp, _) = BuildEcho(TempDir());
        var registry = sp.GetRequiredService<ModuleRegistry>();

        Assert.Null(HostChannelMiddleware.TryReadPartitionKey(token, registry));
    }
}
