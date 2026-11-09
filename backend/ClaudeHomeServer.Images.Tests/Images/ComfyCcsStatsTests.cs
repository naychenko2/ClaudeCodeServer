using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Клиент маршрута /ccs/stats узла ComfyUI (шаг 31): «загружено сейчас» (имена моделей) и
// карты узла. Ответ узла НЕДОВЕРЕННЫЙ: 404/сеть/не-JSON → null, версия != 1 → unsupported;
// тело ≤ 64 КБ, ≤ 16 моделей, ≤ 8 карт, имена ≤ 96 (basename, без управляющих символов).
public class ComfyCcsStatsTests
{
    // Фейковый узел: настраиваемое тело /ccs/stats, 404, счётчик вызовов
    private sealed class CcsFake : HttpMessageHandler
    {
        public string? Body { get; set; }
        public bool NotFound { get; set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path == "/ccs/stats")
            {
                Calls++;
                // 404 с телом: без обработки 404 код прочитал бы тело и вернул данные → тест краснеет
                if (NotFound) return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(Body ?? "", Encoding.UTF8, "application/json"),
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Body ?? "", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static (ComfyClient Client, CcsFake Fake) Build(string? body = null, bool notFound = false)
    {
        var fake = new CcsFake { Body = body, NotFound = notFound };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        return (new ComfyClient(new FakeComfyFactory(fake), config), fake);
    }

    // ─── Парсинг по example-response.json ─────────────────────────────────────────────

    [Fact]
    public async Task Парсинг_ПоПримеру_ЗагруженныеМодели_И_Карты()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Images", "CcsStats", "example-response.json"));
        var (client, _) = Build(body);

        var result = await client.GetCcsStatsAsync(CancellationToken.None);

        result.Should().NotBeNull("маршрут ответил, версия 1");
        result!.HasData.Should().BeTrue();
        // Имена моделей — как в примере (3 модели, порядок сохранён)
        result.LoadedModels.Should().Equal(
            "qwen_image_2.1_int8_convrot.safetensors",
            "qwen_2.5_vl_7b_fp8_scaled.safetensors",
            "AutoencoderKL");
        // Карта узла: одна RTX 3090, index 0
        result.Gpus.Should().ContainSingle();
        var gpu = result.Gpus[0];
        gpu.Index.Should().Be(0);
        gpu.Name.Should().Be("NVIDIA GeForce RTX 3090");
        gpu.UtilPerc.Should().Be(97);
        gpu.MemUsedMb.Should().Be(21874);
        gpu.MemTotalMb.Should().Be(24576);
        gpu.TempC.Should().Be(71);
        gpu.PowerW.Should().Be(338);
        gpu.PowerLimitW.Should().Be(350);
    }

    // ─── 404 / нет маршрута → null ─────────────────────────────────────────────────────

    [Fact]
    public async Task МаршрутНеУстановлен_404_ВернулNull()
    {
        // 404 с валидным телом: без обработки 404 код прочитал бы тело и вернул данные → тест краснеет
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":"m.safetensors"}],"gpus":null}""", notFound: true);
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result.Should().BeNull("404 — маршрут не установлен, данных нет (тело не читаем)");
    }

    // ─── Битый JSON → null ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task БитыйJson_ВернулNull()
    {
        var (client, _) = Build(body: "{not valid json");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result.Should().BeNull("не-JSON — данных нет");
    }

    // ─── Версия 2 → unsupported (HasData=false) ─────────────────────────────────────────

    [Fact]
    public async Task Версия2_ВернулUnsupported()
    {
        var (client, _) = Build(body: """{"version":2,"loadedModels":[],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result.Should().NotBeNull("ответ разобран, но версия не поддерживается");
        result!.HasData.Should().BeFalse("version 2 — unsupported");
        result.Reason.Should().Be("unsupported");
        result.LoadedModels.Should().BeEmpty();
        result.Gpus.Should().BeEmpty();
    }

    // Отсутствие version → тоже unsupported (контракт требует version=1)
    [Fact]
    public async Task ВерсииНет_ВернулUnsupported()
    {
        var (client, _) = Build(body: """{"loadedModels":[],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeFalse("version отсутствует — unsupported");
        result.Reason.Should().Be("unsupported");
    }

    // ─── Огромное тело (> 64 КБ) → null (DoS-защита) ───────────────────────────────────

    [Fact]
    public async Task ОгромноеТело_Более64Кб_ВернулNull()
    {
        // version 1, но loadedModels раздут до > 64 КБ → тело отбрасываем, не парсим
        var big = new string('x', 70 * 1024);
        var (client, _) = Build(body: $$"""{"version":1,"loadedModels":[{"name":"{{big}}"}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result.Should().BeNull("тело > 64 КБ — не парсим (DoS)");
    }

    // ─── Сверхдлинное имя → обрезано до 96 ──────────────────────────────────────────────

    [Fact]
    public async Task СверхдлинноеИмя_ОбрезаноДо96()
    {
        var longName = new string('m', 200);
        var (client, _) = Build(body: $$"""{"version":1,"loadedModels":[{"name":"{{longName}}"}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().ContainSingle();
        result.LoadedModels[0].Should().HaveLength(96, "имя обрезано до 96");
    }

    // ─── Имя с путём ФС → basename ─────────────────────────────────────────────────────

    [Fact]
    public async Task ИмяСПутём_ВернулBasename()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":"/models/checkpoints/some_model.safetensors"}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.LoadedModels.Should().ContainSingle();
        result.LoadedModels[0].Should().Be("some_model.safetensors", "путь ФС не пропускаем — только basename");
    }

    // ─── Управляющие символы в имени → '_' ──────────────────────────────────────────────

    [Fact]
    public async Task УправляющиеСимволы_ЗамененыНаПодчёркивание()
    {
        // \u0001 (SOH) и \t — управляющие → '_'
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":"a\u0001b\tc"}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.LoadedModels.Should().ContainSingle();
        result.LoadedModels[0].Should().Be("a_b_c", "управляющие символы заменены на '_'");
    }

    // ─── Потолок числа моделей (≤ 16) ──────────────────────────────────────────────────

    [Fact]
    public async Task Потолок_16_Моделей_ЛишниеОтброшены()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"model_{i}").ToList();
        var items = string.Join(",", names.Select(n => $$"""{"name":"{{n}}"}"""));
        var (client, _) = Build(body: $$"""{"version":1,"loadedModels":[{{items}}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.LoadedModels.Should().HaveCount(16, "потолок 16 моделей");
    }

    // ─── gpus: null (pynvml-сбой) → пусто, но данные есть ───────────────────────────────

    [Fact]
    public async Task GpusNull_ДанныеЕсть_КартыПусты()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":"m.safetensors"}],"gpus":null,"gpusError":"pynvml: ModuleNotFoundError"}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().ContainSingle();
        result.Gpus.Should().BeEmpty("gpus null (pynvml-сбой) — карт нет, но данные есть");
    }

    // ─── Кэш: повторный вызов в TTL не ходит в сеть (монитор, фейковые часы) ────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class QueueFake : HttpMessageHandler
    {
        public int CcsCalls { get; private set; }
        public string? CcsBody { get; set; }
        public bool CcsNotFound { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path == "/ccs/stats")
            {
                CcsCalls++;
                if (CcsNotFound) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CcsBody ?? "", Encoding.UTF8, "application/json"),
                };
            }
            if (request.Method == HttpMethod.Get && path == "/queue")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"queue_running":[],"queue_pending":[]}""", Encoding.UTF8, "application/json"),
                };
            if (request.Method == HttpMethod.Get && path == "/system_stats")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"system":{"comfyui_version":"v0.37"}}""", Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    // Кэш: первый тик ходит в сеть, повторный в TTL — нет; после истечения TTL — снова ходит
    [Fact]
    public async Task Кэш_ПовторныйВызов_В_ТTL_НеХодитВСеть()
    {
        var fake = new QueueFake
        {
            CcsBody = """{"version":1,"loadedModels":[{"name":"m.safetensors"}],"gpus":null}""",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:Enabled"] = "true",
        }).Build();
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        var client = new ComfyClient(new FakeComfyFactory(fake), config);
        var monitor = new ComfyQueueMonitor(client, ledger, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<ComfyQueueMonitor>.Instance, time)
        {
            CcsTtl = TimeSpan.FromSeconds(5),
            CcsIdleTtl = TimeSpan.FromSeconds(30),
        };

        // Первый тик (активный: панель трогала) → опрос ccs
        monitor.Touch();
        await monitor.TickAsync(CancellationToken.None);
        fake.CcsCalls.Should().Be(1, "первый тик — опрос ccs");
        monitor.CcsSupported.Should().BeTrue("валидный ответ — маршрут установлен");

        // Второй тик в TTL (1 с) → не ходит в сеть
        time.Advance(TimeSpan.FromSeconds(1));
        monitor.Touch();
        await monitor.TickAsync(CancellationToken.None);
        fake.CcsCalls.Should().Be(1, "повторный вызов в TTL — не ходит в сеть");

        // Тик после истечения TTL (6 с от первого) → ходит в сеть
        time.Advance(TimeSpan.FromSeconds(5));
        monitor.Touch();
        await monitor.TickAsync(CancellationToken.None);
        fake.CcsCalls.Should().Be(2, "TTL истёк — снова опрос");
    }

    // Мутация: убрать ограничение размера тела — тест «огромное тело» должен покраснеть.
    // Проверяем, что 64 КБ — реальный порог: ровно 64 КБ парсится, 64 КБ + 1 — нет.
    // Overhead (всё, кроме имени) считается из шаблона, не угадывается.
    [Fact]
    public async Task Тело_Ровно_64Кб_Парсится_А_Более_Нет()
    {
        const int limit = 64 * 1024;
        const string template = """{"version":1,"loadedModels":[{"name":"__NAME__"}],"gpus":null}""";
        var overhead = template.Length - "__NAME__".Length;
        var exactName = new string('n', limit - overhead);
        var (clientExact, _) = Build(body: template.Replace("__NAME__", exactName));
        (await clientExact.GetCcsStatsAsync(CancellationToken.None))!.HasData.Should().BeTrue("ровно 64 КБ — парсится");

        var overName = new string('n', limit - overhead + 1);
        var (clientOver, _) = Build(body: template.Replace("__NAME__", overName));
        (await clientOver.GetCcsStatsAsync(CancellationToken.None)).Should().BeNull("64 КБ + 1 — не парсится");
    }

    // ─── Мусорные (нестроковые) значения полей узла → безопасный результат, без исключения ───
    // Ревью шага 31: GetValue<string> на нестроковом значении бросал InvalidOperationException,
    // который ронял фоновый монитор. Каждый случай ниже — version 1, но поле мусорного типа.

    [Fact]
    public async Task ИмяМодели_Число_ИгнорируетсяБезИсключения()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":42}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result.Should().NotBeNull("version 1 — данные есть, мусорное имя не роняет");
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().BeEmpty("число вместо имени — запись пропущена");
    }

    [Fact]
    public async Task ИмяМодели_Массив_ИгнорируетсяБезИсключения()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":[]}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().BeEmpty("массив вместо имени — запись пропущена");
    }

    [Fact]
    public async Task ИмяМодели_Объект_ИгнорируетсяБезИсключения()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":{}}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().BeEmpty("объект вместо имени — запись пропущена");
    }

    [Fact]
    public async Task ИмяМодели_Null_ИгнорируетсяБезИсключения()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[{"name":null}],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().BeEmpty("null вместо имени — запись пропущена");
    }

    [Fact]
    public async Task ЗаписьМодели_ЧислоВместеОбъекта_Игнорируется()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[42],"gpus":null}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.LoadedModels.Should().BeEmpty("число вместо объекта — запись пропущена");
    }

    [Fact]
    public async Task Gpus_Строка_КартыПусты()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[],"gpus":"nvidia"}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.Gpus.Should().BeEmpty("gpus строка — не массив, карт нет");
    }

    [Fact]
    public async Task Gpus_Число_КартыПусты()
    {
        var (client, _) = Build(body: """{"version":1,"loadedModels":[],"gpus":42}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.Gpus.Should().BeEmpty("gpus число — не массив, карт нет");
    }

    [Fact]
    public async Task UtilPerc_Строка_И_ПамятьNull_БезИсключения()
    {
        // utilPerc строка (не число) → null; memTotalMb null → null; карта всё равно есть (имя/индекс)
        var (client, _) = Build(body: """{"version":1,"loadedModels":[],"gpus":[{"index":0,"name":"GPU","utilPerc":"x","memUsedMb":100,"memTotalMb":null}]}""");
        var result = await client.GetCcsStatsAsync(CancellationToken.None);
        result!.HasData.Should().BeTrue();
        result.Gpus.Should().ContainSingle();
        var gpu = result.Gpus[0];
        gpu.Index.Should().Be(0);
        gpu.Name.Should().Be("GPU");
        gpu.UtilPerc.Should().BeNull("utilPerc строка — не число");
        gpu.MemUsedMb.Should().Be(100);
        gpu.MemTotalMb.Should().BeNull("memTotalMb null");
    }

    // ─── Тест монитора: кривой ответ узла НЕ останавливает цикл ─────────────────────────────
    // Мутация: вернуть GetValue<string> (бросает на {"name":42}) — исключение дойдёт до ExecuteAsync
    // и убьёт фоновый сервис; здесь мы зовём TickAsync напрямую, поэтому цикл «умрёт» = исключение
    // из TickAsync. После фикса (StrOrNull + страховочный catch) — следующий тик отрабатывает.

    [Fact]
    public async Task Монитор_КривойОтветУзла_НеОстанавливаетЦикл()
    {
        var fake = new QueueFake
        {
            // Мусорный узел: name — число (GetValue<string> бросал), gpus — строка
            CcsBody = """{"version":1,"loadedModels":[{"name":42}],"gpus":"nvidia"}""",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:Enabled"] = "true",
        }).Build();
        var time = new ManualTime();
        var ledger = new ComfyPromptLedger(time);
        var client = new ComfyClient(new FakeComfyFactory(fake), config);
        var monitor = new ComfyQueueMonitor(client, ledger, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<ComfyQueueMonitor>.Instance, time)
        {
            CcsTtl = TimeSpan.FromSeconds(5),
            CcsIdleTtl = TimeSpan.FromSeconds(30),
        };

        // Первый тик (активный): опрос ccs с мусорным ответом — не бросает, Down не меняется
        monitor.Touch();
        await monitor.TickAsync(CancellationToken.None);
        monitor.Down.Should().BeFalse("мусорный ccs не значит «ComfyUI недоступен»");
        monitor.Ccs.Should().NotBeNull("version 1 — данные есть, мусорные поля утилизированы");
        monitor.Ccs!.HasData.Should().BeTrue();
        monitor.Ccs.LoadedModels.Should().BeEmpty("name:42 — запись пропущена");
        monitor.Ccs.Gpus.Should().BeEmpty("gpus строка — карт нет");

        // Второй тик (TTL истёк): цикл жив, снова ходит в сеть, Down/очередь не затронуты
        time.Advance(TimeSpan.FromSeconds(6));
        monitor.Touch();
        await monitor.TickAsync(CancellationToken.None);
        fake.CcsCalls.Should().Be(2, "второй тик — TTL истёк, снова опрос");
        monitor.Down.Should().BeFalse("цикл монитора не умер после мусорного ответа");
        monitor.Snapshot!.Running.Should().BeEmpty("очередь не затронута");
    }
}
