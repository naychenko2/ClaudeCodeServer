using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Учёт расхода серверной генерации картинок (дефект 22068e9d, шаг S-SPEND-2).
// Проверяется конечное состояние после полного вызова: трата адресуется владельцу-арендатору
// (для персоны проекта — владельцу проекта, п.3-4) за ФАКТИЧЕСКИ отработавшего провайдера
// (после фолбэка), первой — всегда запись без суммы, сумму догоняет отдельная запись без
// генераций (прецедент ImageEditJobService).
public class ImageGenerationSpendTests
{
    private const string Avatar = ImagePlaces.PersonaAvatar;
    private const string Owner = "user-a";

    // Догоняющая запись при исправном опросе появляется сразу: окно — страховка от
    // зависшего опроса, а не ожидание реального бэкоффа FalCostService.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private sealed class CapturingSpend : ISpendCollector
    {
        private readonly List<SpendRecord> _records = [];
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];

        public IReadOnlyList<SpendRecord> Records
        {
            get { lock (_records) return [.. _records]; }
        }

        public void Record(SpendRecord record)
        {
            lock (_records)
            {
                _records.Add(record);
                foreach (var (n, tcs) in _waiters.Where(w => w.Key <= _records.Count).ToArray())
                {
                    _waiters.Remove(n);
                    tcs.TrySetResult();
                }
            }
        }

        // Дожидаться N записей: не дождался — значит их никто и не писал. Ждать вторую — это
        // догоняющая запись одного вызова, ждать четвёртую — полный цикл двух вызовов.
        public async Task WaitForCount(int n, TimeSpan timeout)
        {
            TaskCompletionSource? tcs = null;
            lock (_records)
            {
                if (_records.Count >= n) return;
                if (!_waiters.TryGetValue(n, out tcs)) _waiters[n] = tcs = new();
            }
            await Task.WhenAny(tcs!.Task, Task.Delay(timeout));
        }

        public Task WaitForFirst(TimeSpan timeout) => WaitForCount(1, timeout);

        public Task WaitForSecond(TimeSpan timeout) => WaitForCount(2, timeout);
    }

    // Цена по request_id: известная / её нет вовсе. Считает обращения к Core-шову.
    private sealed class FakeCost(double? usd) : IFalRequestCost
    {
        private readonly TaskCompletionSource _asked = new();

        public int Calls { get; private set; }
        public Task AskedOrTimeout => Task.WhenAny(_asked.Task, Task.Delay(Settle));

        public Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct)
        {
            Calls++;
            _asked.TrySetResult();
            return Task.FromResult(usd);
        }
    }

    // Драйвер-заглушка: отдаёт варианты; requestId — то, что вернул поставщик
    // (у glif его нет, и это тоже проверяемый случай). defaultModel — дефолт, который
    // заглушка объявляет себе по тому же контракту, что и боевые драйверы.
    private sealed class FakeGenerator(string key, bool enabled, string? requestId = null,
        bool returnsImages = true, string? defaultModel = null)
        : IImageGenerator
    {
        public string Key => key;
        public string DisplayName => key;
        public bool Enabled => enabled;
        public IReadOnlyList<ImageModelInfo> Models { get; init; } = [];
        public string? DefaultModel => defaultModel;
        public int Calls { get; private set; }
        public string? LastModel { get; private set; }

        // Идентификатор запроса mutable: в реальности каждый вызов имеет свой, и тест повторного
        // вызова даёт второму его id — иначе «дубль догоняющей» неотличим от новой траты.
        public string? RequestId { get; set; } = requestId;

        public Task<ImageGenerationResult> GenerateManyAsync(
            string prompt, int count, string? model, CancellationToken ct = default)
        {
            Calls++;
            LastModel = model;
            IReadOnlyList<GeneratedImage> result = returnsImages
                ? Enumerable.Range(1, count).Select(_ => new GeneratedImage([1, 2, 3], "image/png")).ToList()
                : [];
            return Task.FromResult(new ImageGenerationResult(result, RequestId));
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_spend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ImageGenerationSettingsStore Store(string tempDir, params (string Key, string Value)[] config)
    {
        var values = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(tempDir, "projects.json") };
        foreach (var (key, value) in config) values[key] = value;
        return new ImageGenerationSettingsStore(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public async Task Фал_ТратаПишетсяСразу_СуммаДогоняетОтдельнойЗаписью()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.0123);
        var service = new ImageGenerationService([fal], Store(NewTempDir(), ("Images:Models:fal", "fal-ai/flux/dev")),
            null, spend, cost);

        var images = await service.GenerateManyAsync(Avatar, "кот", 2, Owner, SpendInitiators.Human);

        images.Should().HaveCount(2);
        await spend.WaitForSecond(Settle);

        var first = spend.Records.First();
        first.OwnerId.Should().Be(Owner, "трату пишем на того, кто её попросил");
        first.Source.Should().Be(SpendSources.Fal);
        first.Provider.Should().Be("fal");
        first.Model.Should().Be("fal-ai/flux/dev", "модель, с которой провайдер реально пошёл");
        first.Generations.Should().Be(2, "число вариантов — с первой записи");
        first.CostUsd.Should().BeNull("суммы ещё нет");
        first.Label.Should().Be(ImageGenerationService.SpendLabelPendingAmount);
        first.Initiator.Should().Be(SpendInitiators.Human);
        // Вызывающий не назвал ни актора, ни проекта (персона вне проекта): поля остаются
        // пустыми — для актора это штатное чтение стора «нет поля → владелец» (п.3-4)
        first.ActorUserId.Should().BeNull();
        first.ProjectId.Should().BeNull();

        var topUp = spend.Records.Last();
        topUp.Should().NotBeSameAs(first);
        // П.6 ревью R-S-SPEND-2: догоняющая запись привязана к более позднему моменту и не
        // может обогнать первую по времени — иначе срез «сегодня» и сортировка истории разъезжаются
        topUp.Timestamp.Should().BeOnOrAfter(first.Timestamp);
        topUp.Generations.Should().Be(0, "догоняющая запись не повторяет генерации");
        topUp.CostUsd.Should().Be(0.0123);
        topUp.Label.Should().Be(ImageGenerationService.SpendLabel);
        topUp.Source.Should().Be(SpendSources.Fal);
        topUp.Model.Should().Be("fal-ai/flux/dev", "догоняющая запись — про тот же вызов");
    }

    // Модель в настройках места не задана: драйвер пошёл на свой дефолт, и запись несёт
    // именно его. Пустая модель резолвится аналитикой в дефолт подписки («claude-…»), и
    // генерация картинок попадала в фильтр по модели вместе с настоящими LLM-тратами.
    [Fact]
    public async Task МодельНеНастроена_ВЗаписиФактическийДефолтДрайвера()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", defaultModel: "test/default-model");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.0123);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);
        await spend.WaitForSecond(Settle);

        var first = spend.Records.First();
        first.Model.Should().Be("test/default-model",
            "в учёте стоит модель, с которой провайдер реально пошёл, а не пустота");
        first.Generations.Should().Be(1);
        // Самому драйверу модель по-прежнему не навязываем: свой дефолт он берёт сам
        fal.LastModel.Should().BeNull();

        var topUp = spend.Records.Last();
        topUp.Should().NotBeSameAs(first, "догоняющая запись обязана существовать: без неё Last() — это первая запись");
        topUp.Model.Should().Be("test/default-model", "догоняющая запись — про тот же вызов");
    }

    [Fact]
    public async Task Glif_ТратаПишетсяСчётчиком_ЦенуНеОпрашиваем()
    {
        var glif = new FakeGenerator("glif", enabled: true);   // идентификатора запроса у glif нет
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.01);
        var service = new ImageGenerationService([glif], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);
        await spend.WaitForSecond(TimeSpan.FromMilliseconds(300));

        var rec = spend.Records.Single();
        rec.Source.Should().Be(SpendSources.Glif);
        rec.Generations.Should().Be(1);
        rec.CostUsd.Should().BeNull();
        // У glif суммы не будет никогда: в его ответе нет идентификатора запроса, по которому
        // спрашивают цену. Значит и обещания уточнения в подписи нет — это счётчик вызовов.
        rec.Label.Should().Be(ImageGenerationService.SpendLabel);
        cost.Calls.Should().Be(0, "опрашивать цену не по чему: request_id не приходил");
    }

    // Обещание уточнения ставят только там, где его будут исполнять (минор ревью R-S-SPEND-2):
    // оно выводится из ручки опроса (id запроса в ответе + средство спросить), а не из ключа
    // провайдера. fal без x-fal-request-id в ответе — опрос не начнётся никогда, подпись
    // остаётся обычным счётчиком вызовов.
    [Fact]
    public async Task ФалБезИдентификатораЗапроса_ОбещанияУточненияНет()
    {
        var fal = new FakeGenerator("fal", enabled: true);   // ответ без заголовка x-fal-request-id
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.01);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);

        var rec = spend.Records.Single();
        rec.Source.Should().Be(SpendSources.Fal);
        rec.Label.Should().Be(ImageGenerationService.SpendLabel,
            "по чему спрашивать цену нет — обещание «сумма уточняется» не ставят");
        cost.Calls.Should().Be(0, "опрашивать цену не по чему: request_id не приходил");
    }

    // Та же граница со стороны средства опроса: id есть, но IFalRequestCost не подключён —
    // опрос не начнётся, и обещания тоже нет
    [Fact]
    public async Task ФалБезСредстваОпроса_ОбещанияУточненияНет()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);

        spend.Records.Single().Label.Should().Be(ImageGenerationService.SpendLabel,
            "спросить цену нечем — обещание «сумма уточняется» не ставят");
    }

    [Fact]
    public async Task ФолбэкНаФала_ТратаЗаФактическиОтработавшегоПровайдера()
    {
        var glif = new FakeGenerator("glif", enabled: true, returnsImages: false);
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null);
        // Порядок auto-выбора: glif → fal; glif отдал пусто — пошёл fal
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);

        spend.Records.Should().ContainSingle().Which.Source.Should().Be(SpendSources.Fal,
            "трата за того, кто отдал картинки: у пустого ответа нет носителя траты (п.5)");
        glif.Calls.Should().Be(1);
        fal.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ЦеныНет_ОстаётсяОднаЗаписьБезСуммы()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);

        // Отсутствие цены — штатный исход: опрос был, догоняющей записи нет
        await cost.AskedOrTimeout;
        await spend.WaitForSecond(TimeSpan.FromMilliseconds(300));
        cost.Calls.Should().Be(1);
        spend.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task БезВладельца_ГенерацияРаботает_ТратаНеПишется()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.01);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        // Пустой владелец — осознанный выбор вызывающего (S-SPEND-8): параметр обязателен,
        // пустую строку компилятор пропустит, но это уже не забытый аргумент
        var images = await service.GenerateManyAsync(Avatar, "кот", 1, "", SpendInitiators.Human);

        images.Should().HaveCount(1, "генерация без владельца работает как раньше");
        spend.Records.Should().BeEmpty("трату не на кого повесить — в «системную» не кладём");
        cost.Calls.Should().Be(0);
    }

    // Инициатор записи совпадает с инициатором вызова (находка ревью R-S-SPEND-2):
    // агентский путь (personas_generate_avatar из чата) доходит до RecordSpend и обязан
    // быть записан как Agent, а не как Human.
    [Fact]
    public async Task ИнициаторАгента_ЗаписываетсяКакAgent()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(null);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Agent);

        var rec = spend.Records.Single();
        rec.OwnerId.Should().Be(Owner);
        rec.Initiator.Should().Be(SpendInitiators.Agent, "инициатор записи — тот, кто подал операцию");
    }

    // Вызывающий, который не назвал инициатора (пустая строка), не должен получать в записи
    // выдуманного человека: поле остаётся null — «не размечено» (S-SPEND-8)
    [Fact]
    public async Task ИнициаторНеНазван_ЗаписьБезИнициатора()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, new FakeCost(null));

        var images = await service.GenerateAsync(Avatar, "кот", Owner, "", ct: CancellationToken.None);

        images.Should().NotBeNull("генерация от пустого инициатора не отказывается");
        spend.Records.Single().Initiator.Should().BeNull("неизвестный инициатор не выдумываем");
    }

    // Мелочь п.2 хвоста R-S-SPEND-2: в догоняющую запись идёт нормализованный инициатор, а не
    // сырой: пустой дал бы второй записи «""» при первой с null — у одного факта два признака
    [Fact]
    public async Task ИнициаторНеНазван_ДогоняющаяЗаписьНесётТоЖеЧтоПервая()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.0123);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, "");
        await spend.WaitForSecond(Settle);

        var first = spend.Records.First();
        var topUp = spend.Records.Last();
        topUp.Should().NotBeSameAs(first, "цена пришла — догоняющая запись есть");
        first.Initiator.Should().BeNull("неизвестный инициатор не выдумываем");
        topUp.Initiator.Should().BeNull(
            "у одного факта траты один признак инициатора: догоняющая не несёт \"\" против null первой");
    }

    // П.3-4 ревью R-S-SPEND-2: вызывающий назвал арендатора, актора и проект — запись несёт их,
    // и догоняющая запись того же вызова несёт те же. Ключом хранилища остаётся арендатор:
    // актор — разрез, не владелец (SpendRecord.ActorUserId), и трата проектной персоны видна
    // проекту, а не только позвавшему её человеку.
    [Fact]
    public async Task АтрибуцияТратыПереходитВОбеЗаписи()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.0123);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, "tenant-o", SpendInitiators.Agent,
            "actor-u", "project-1");
        await spend.WaitForSecond(Settle);

        var first = spend.Records.First();
        first.OwnerId.Should().Be("tenant-o", "ключ записи — арендатор, а не исполнивший");
        first.ActorUserId.Should().Be("actor-u", "актор назван отдельно от владельца");
        first.ProjectId.Should().Be("project-1", "трата проекта видна в разрезе по проекту");

        var topUp = spend.Records.Last();
        topUp.Should().NotBeSameAs(first);
        topUp.OwnerId.Should().Be("tenant-o");
        topUp.ActorUserId.Should().Be("actor-u", "догоняющая запись — про тот же вызов");
        topUp.ProjectId.Should().Be("project-1", "догоняющая запись — про тот же вызов");
    }

    // П.6 ревью R-S-SPEND-2: догоняющая запись — одна на вызов. Второй вызов — новый запрос
    // со своей тратой (в реальности — со своим id), и догоняющая первого не повторяется:
    // два вызова дают ровно две первых записи и две догоняющие, ничего сверх.
    [Fact]
    public async Task ПовторныйВызов_ДогоняющихДублейНет()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.0123);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);
        await spend.WaitForSecond(Settle);
        spend.Records.Should().HaveCount(2, "первый вызов: запись без суммы + догоняющая");

        fal.RequestId = "req-2";   // каждый вызов имеет свой запрос
        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);
        await spend.WaitForCount(4, Settle);

        spend.Records.Should().HaveCount(4, "по одной догоняющей на вызов: второй не повторил первый");
        spend.Records.Count(r => r.Generations == 0).Should().Be(2);
        cost.Calls.Should().Be(2, "по одному опосу цены на вызов");
    }


    // Пустой результат: картинок нет, а id запроса fal есть (скачивания упали / 4xx после GPU).
    // Запись без картинок — тот же факт траты, что и обычная: она несёт владельца, актора,
    // проект и инициатора (инвариант слияния линии траты), а не урезанный набор признаков.
    [Fact]
    public async Task ПустойFal_ЦенаБольшеНуля_РовноОднаЗаписьБезКартинок()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = new ImageGenerationService([fal], Store(NewTempDir(), ("Images:Models:fal", "fal-ai/flux/dev")),
            null, spend, cost);

        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1, "tenant-o", SpendInitiators.Agent,
            "actor-u", "project-1"));
        await spend.WaitForFirst(Settle);
        await spend.WaitForSecond(TimeSpan.FromMilliseconds(300));

        var rec = spend.Records.Should().ContainSingle("pending-записи нет: платность запроса неизвестна").Subject;
        rec.Generations.Should().Be(0);
        rec.CostUsd.Should().Be(0.04);
        rec.Label.Should().Be(ImageGenerationService.SpendLabelNoImages);
        rec.Provider.Should().Be("fal");
        rec.Source.Should().Be(SpendSources.Fal);
        rec.Model.Should().Be("fal-ai/flux/dev");
        rec.OwnerId.Should().Be("tenant-o");
        rec.Initiator.Should().Be(SpendInitiators.Agent);
        rec.ActorUserId.Should().Be("actor-u", "запись без картинок несёт актора, как и обычная");
        rec.ProjectId.Should().Be("project-1", "запись без картинок несёт проект, как и обычная");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public async Task ПустойFal_ЦенаНеизвестнаИлиНоль_ЗаписейНет(double? usd)
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(usd);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human));

        await cost.AskedOrTimeout;
        await spend.WaitForFirst(TimeSpan.FromMilliseconds(300));
        cost.Calls.Should().Be(1, "цену спросили — иначе тест проходил бы вакуумно");
        spend.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ПустойFal_БезIdЗапроса_ЦенуНеСпрашиваем()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: null, returnsImages: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human);

        await spend.WaitForFirst(TimeSpan.FromMilliseconds(300));
        spend.Records.Should().BeEmpty();
        cost.Calls.Should().Be(0);
    }

    // Фолбэк не съедает трату пустого fal. ProviderOrder статичен (glif → fal), поэтому
    // «fal, затем glif» в проде недостижим; механизм проверяем цепочкой fal → драйвер с
    // неизвестным ключом (OrderOf кладёт его в хвост): трата fal пишется в своей итерации.
    [Fact]
    public async Task Auto_ПустойFal_ПотомДругойПровайдерСКартинками_ДвеТраты()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var next = new FakeGenerator("next", enabled: true);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = new ImageGenerationService([next, fal], Store(NewTempDir()), null, spend, cost);

        var images = await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human, ct: CancellationToken.None);

        images.Should().HaveCount(1);
        await spend.WaitForSecond(Settle);
        spend.Records.Should().HaveCount(2);
        spend.Records.Should().ContainSingle(r => r.Source == SpendSources.Fal && r.Label == ImageGenerationService.SpendLabelNoImages
            && r.CostUsd == 0.04 && r.Generations == 0);
        spend.Records.Should().ContainSingle(r => r.Source == "next" && r.Generations == 1
            && r.Label == ImageGenerationService.SpendLabel,
            "у провайдера без id запроса цены не будет — «сумма уточняется» не обещаем");
    }

    // Боевой порядок auto — glif → fal (ProviderOrder): пустой glif идёт первым, пустой fal с id
    // последним, трата fal пишется на последней итерации ровно один раз.
    [Fact]
    public async Task Auto_БоевойПорядок_ПустойGlif_ПустойFalСId_РовноОднаЗаписьFal()
    {
        var glif = new FakeGenerator("glif", enabled: true, returnsImages: false);
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.04);
        var service = new ImageGenerationService([fal, glif], Store(NewTempDir()), null, spend, cost);

        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human, ct: CancellationToken.None));

        await spend.WaitForFirst(Settle);
        await spend.WaitForSecond(TimeSpan.FromMilliseconds(300));
        glif.Calls.Should().Be(1);
        fal.Calls.Should().Be(1);
        var rec = spend.Records.Should().ContainSingle().Subject;
        rec.Source.Should().Be(SpendSources.Fal);
        rec.Label.Should().Be(ImageGenerationService.SpendLabelNoImages);
        rec.CostUsd.Should().Be(0.04);
        rec.Generations.Should().Be(0);
    }

    private sealed class CapturingLog : ILogger<ImageGenerationService>
    {
        private readonly List<(LogLevel Level, string Text)> _items = [];
        public IReadOnlyList<(LogLevel Level, string Text)> Items
        {
            get { lock (_items) return [.. _items]; }
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_items) _items.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task ПустойFal_ЦенаНеПоявилась_ЛогНеОбещаетЗапись()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var log = new CapturingLog();
        var cost = new FakeCost(null);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), log, new CapturingSpend(), cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, Owner, SpendInitiators.Human, ct: CancellationToken.None);
        await cost.AskedOrTimeout;
        await Task.Delay(300);

        var warn = log.Items.Should().ContainSingle(i => i.Level == LogLevel.Warning).Subject;
        warn.Text.Should().Contain("записи нет").And.NotContain("трата остаётся без суммы");
    }

    [Fact]
    public async Task ПустойFal_ПустойВладелец_РанийВыходПишетЛогБезОбещанияЗаписи()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var log = new CapturingLog();
        var cost = new FakeCost(0.04);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), log, new CapturingSpend(), cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, "", SpendInitiators.Human, ct: CancellationToken.None);

        log.Items.Should().Contain(i => i.Level == LogLevel.Warning && i.Text.Contains("записи нет"));
        cost.Calls.Should().Be(0);
    }

    // Регресс: с картинками — прежний путь (pending с Generations = N + догоняющая)
    [Fact]
    public async Task ФалСКартинками_ПутьНеИзменился()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1");
        var spend = new CapturingSpend();
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, new FakeCost(0.02));

        await service.GenerateManyAsync(Avatar, "кот", 3, Owner, SpendInitiators.Human, ct: CancellationToken.None);
        await spend.WaitForSecond(Settle);

        spend.Records.Should().HaveCount(2);
        spend.Records[0].Should().Match<SpendRecord>(r => r.Generations == 3 && r.CostUsd == null
            && r.Label == ImageGenerationService.SpendLabelPendingAmount);
        spend.Records[1].Should().Match<SpendRecord>(r => r.Generations == 0 && r.CostUsd == 0.02
            && r.Label == ImageGenerationService.SpendLabel);
    }
}
