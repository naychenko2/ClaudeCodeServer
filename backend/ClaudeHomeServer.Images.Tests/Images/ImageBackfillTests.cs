using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Догоняющая генерация аватаров персон: очередь заявок, идемпотентность прогона, потолки
// попыток, повтор только транзиентных отказов и событие фронту после успеха. Очередь
// иконок проектов удалена вместе с растровыми иконками (ADR-009) — очередь одна.
public class ImageBackfillTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly User _owner;
    private readonly TestSessionBroadcaster _broadcaster;

    public ImageBackfillTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs_imgfill_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = _tempDir,
            })
            .Build();
        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _personas = new PersonaManager(_config);
        _owner = _users.Add("imgfill_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");

        // Перехват broadcast-сообщений через TestSessionBroadcaster (заменяет мок IHubContext)
        _broadcaster = new TestSessionBroadcaster();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Persona NewPersona(string? ownerId = null) =>
        _personas.Create(ownerId ?? _owner.Id, "Вера", "QA", "тестировщица", null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);

    private ImageBackfillStore NewStore() => new(_config, NullLogger<ImageBackfillStore>.Instance);

    private ImageBackfillService Service(FakeGenerator generator, ImageBackfillStore? store = null)
        => ServiceWithSpend(generator, null, store);

    // Тот же сервис, но с коллектором трат: чтобы видеть, что и на кого записывает очередь
    private ImageBackfillService ServiceWithSpend(FakeGenerator generator,
        ClaudeHomeServer.Services.Spend.ISpendCollector? spend, ImageBackfillStore? store = null)
    {
        var settings = new ImageGenerationSettingsStore(_config);
        var images = new ImageGenerationService([generator], settings, null, spend);
        return new ImageBackfillService(store ?? NewStore(), images, _personas, _personas,
            _broadcaster, new FakeLifetime(), NullLogger<ImageBackfillService>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };
    }

    private sealed class CapturingSpend : ClaudeHomeServer.Services.Spend.ISpendCollector
    {
        private readonly List<SpendRecord> _records = [];
        public IReadOnlyList<SpendRecord> Records { get { lock (_records) return [.. _records]; } }
        public void Record(SpendRecord record) { lock (_records) _records.Add(record); }
    }

    // Трата догоняющей генерации (S-SPEND-2c): она пишется на владельца заявки — он известен
    // с момента постановки, а Resolve снимает заявку, если персона ушла к другому владельцу
    [Fact]
    public async Task Прогон_пишет_трату_на_владельца_заявки()
    {
        var persona = NewPersona();
        var generator = new FakeGenerator();
        var spend = new CapturingSpend();
        var service = ServiceWithSpend(generator, spend);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(1, summary.Generated);
        // трата догоняющей генерации — на владельца заявки
        var rec = Assert.Single(spend.Records);
        Assert.Equal(_owner.Id, rec.OwnerId);
        Assert.Equal(1, rec.Generations);
        Assert.Equal(ImageGenerationService.SpendLabelPendingAmount, rec.Label);
        // Инициатор — тот, кто поставил заявку: он сохранён в ней и доходит до фоновой
        // траты, роутер его не выдумывает (S-SPEND-8)
        Assert.Equal(SpendInitiators.Human, rec.Initiator);
    }

    // Заявку поставил агент (путь MCP-тулсета): фоновая трата обязана нести agent —
    // признак агентской траты один, «null + очередь» вторым способом быть не может
    [Fact]
    public async Task ЗаявкаОтАгента_ФоноваяТратаСИнициаторомАгента()
    {
        var persona = NewPersona();
        var generator = new FakeGenerator();
        var spend = new CapturingSpend();
        var service = ServiceWithSpend(generator, spend);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Agent);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(1, summary.Generated);
        var rec = Assert.Single(spend.Records);
        Assert.Equal(_owner.Id, rec.OwnerId);
        Assert.Equal(SpendInitiators.Agent, rec.Initiator);
    }

    // Старая заявка (файл до появления поля Initiator): читается как human — так её и
    // ставили до S-SPEND-8, выдумывать другой инициатор не будем
    [Fact]
    public async Task СтараяЗаявкаБезИнициатора_ЧитаетсяКакЧеловек()
    {
        var persona = NewPersona();
        // Формат очереди до S-SPEND-8: поля Initiator в заявке нет вовсе
        File.WriteAllText(Path.Combine(_tempDir, ImageBackfillStore.FileName),
            $$"""
            {"Version":1,"Requests":[{"Kind":"persona-avatar","EntityId":"{{persona.Id}}","OwnerId":"{{_owner.Id}}","Prompt":"аватар","Attempts":0,"CreatedAt":"2026-11-01T00:00:00Z"}]}
            """);
        var old = NewStore();
        Assert.Single(old.All);

        var generator = new FakeGenerator();
        var spend = new CapturingSpend();
        var service = ServiceWithSpend(generator, spend, old);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(1, summary.Generated);
        Assert.Equal(SpendInitiators.Human, Assert.Single(spend.Records).Initiator);
    }

    // Инициатор относится к заявке, а не к конкретной попытке: транзиентный отказ оставил
    // заявку в очереди — следующий прогон обязан вести её с тем же инициатором (Copy
    // заявки несёт поле, а не пересобирает её заново)
    [Fact]
    public async Task ИнициаторЗаявкиПереживаетПовторВСледующемПрогоне()
    {
        var persona = NewPersona();
        var store = NewStore();
        // Первый прогон — провайдер молчит обе попытки, второй отвечает
        var generator = new FakeGenerator(answers: call => call > 2);
        var spend = new CapturingSpend();
        var service = ServiceWithSpend(generator, spend, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Agent);

        Assert.Equal(new ImageBackfillSummary(0, 0, 1), await service.RunAsync(_owner.Id));
        // Заявка осталась в очереди со своим инициатором
        Assert.Equal(SpendInitiators.Agent, Assert.Single(store.All).Initiator);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(1, summary.Generated);
        Assert.Equal(SpendInitiators.Agent, Assert.Single(spend.Records).Initiator);
    }

    [Fact]
    public async Task Прогон_догоняет_аватар_сПромптомЗаявки_и_снимает_заявку()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(1, 0, 0), summary);
        Assert.Equal(1, generator.Calls);
        // Промпт заявки доходит до генератора, а не собирается из имени сущности
        Assert.Equal("аватар", generator.LastPrompt);

        var saved = _personas.GetByIdInternal(persona.Id)!;
        Assert.Equal(PersonaAvatarKind.Image, saved.Avatar.Kind);
        Assert.True(File.Exists(Path.Combine(_personas.AssetsDir, persona.Id, saved.Avatar.ImageFile!)));
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Прогон_догоняет_аватар_персоны_и_шлёт_событие_фронту()
    {
        var persona = NewPersona();
        var service = Service(new FakeGenerator());
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, null, SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(1, 0, 0), summary);
        var saved = _personas.GetByIdInternal(persona.Id)!;
        Assert.Equal(PersonaAvatarKind.Image, saved.Avatar.Kind);
        Assert.True(File.Exists(Path.Combine(_personas.AssetsDir, persona.Id, saved.Avatar.ImageFile!)));

        var backfilled = Assert.Single(_broadcaster.Owner.Select(t => t.Message)
            .OfType<ImageBackfilledMessage>());
        Assert.Equal(ImageBackfillKinds.PersonaAvatar, backfilled.Kind);
        Assert.Equal(persona.Id, backfilled.EntityId);
        // Раздел «Персоны» слушает штатное personas_changed — его шлём вдобавок
        Assert.Contains(_broadcaster.Owner.Select(t => t.Message).OfType<PersonasChangedMessage>(),
            m => m.Action == "updated" && m.PersonaId == persona.Id);
    }

    [Fact]
    public async Task Повторный_прогон_ничего_не_генерирует()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        await service.RunAsync(_owner.Id);
        var avatarAfterFirst = _personas.GetByIdInternal(persona.Id)!.Avatar.ImageFile;

        var second = await service.RunAsync(_owner.Id);

        Assert.Equal(ImageBackfillSummary.Empty, second);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(avatarAfterFirst, _personas.GetByIdInternal(persona.Id)!.Avatar.ImageFile);
        Assert.Single(Directory.GetFiles(Path.Combine(_personas.AssetsDir, persona.Id)));
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Картинка_поставленная_руками_снимает_заявку_без_генерации()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        // Человек успел выбрать/загрузить свою картинку раньше прогона
        _personas.SetAvatarImage(persona.Id, _owner.Id, "avatar-manual.png");

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(0, 1, 0), summary);
        Assert.Equal(0, generator.Calls);
        Assert.Equal("avatar-manual.png", _personas.GetByIdInternal(persona.Id)!.Avatar.ImageFile);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Удалённая_сущность_снимает_заявку()
    {
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, "нет-такой-персоны", _owner.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(0, 1, 0), summary);
        Assert.Equal(0, generator.Calls);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Выключенный_генератор_не_трогает_заявку_и_не_жжёт_попытки()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator(enabled: false);
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(ImageBackfillSummary.Empty, summary);
        Assert.Equal(0, generator.Calls);
        var queued = Assert.Single(store.All);
        Assert.Equal(0, queued.Attempts);   // потолок не сожжён — ждём появления провайдера
        Assert.Equal(PersonaAvatarKind.Initials, _personas.GetByIdInternal(persona.Id)!.Avatar.Kind);
    }

    [Fact]
    public async Task Появившийся_провайдер_доводит_картинку_прежней_заявке()
    {
        var persona = NewPersona();
        var store = NewStore();
        // Заявка пережила прогон при выключенном провайдере…
        var offline = Service(new FakeGenerator(enabled: false), store);
        offline.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);
        await offline.RunAsync(_owner.Id);
        Assert.Single(store.All);

        var online = new FakeGenerator();
        var summary = await Service(online, store).RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(1, 0, 0), summary);
        Assert.Equal(1, online.Calls);
        Assert.Equal(PersonaAvatarKind.Image, _personas.GetByIdInternal(persona.Id)!.Avatar.Kind);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Транзиентный_отказ_повторяется_и_вторая_попытка_проходит()
    {
        var persona = NewPersona();
        var store = NewStore();
        // Первый вызов — провайдер картинок не отдал, второй отвечает
        var generator = new FakeGenerator(answers: call => call > 1);
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(1, 0, 0), summary);
        Assert.Equal(2, generator.Calls);
        Assert.Equal(PersonaAvatarKind.Image, _personas.GetByIdInternal(persona.Id)!.Avatar.Kind);
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Потолок_попыток_снимает_вечно_падающую_заявку()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator(answers: _ => false);
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        // По две попытки за прогон: на пятой упирается в потолок, следующий прогон снимает заявку
        for (var i = 0; i < 4; i++) await service.RunAsync(_owner.Id);

        Assert.Empty(store.All);
        Assert.Equal(5, generator.Calls);   // MaxTotalAttempts, не больше
        Assert.Equal(PersonaAvatarKind.Initials, _personas.GetByIdInternal(persona.Id)!.Avatar.Kind);

        // Очередь пуста — следующий прогон генератор не дёргает
        Assert.Equal(ImageBackfillSummary.Empty, await service.RunAsync(_owner.Id));
        Assert.Equal(5, generator.Calls);
    }

    [Fact]
    public async Task Одновременно_идёт_не_больше_двух_генераций_на_инстанс()
    {
        var store = NewStore();
        // Каждый вызов ждёт СОБЫТИЯ «нас уже двое» — так пик одновременности виден без таймеров.
        // Сорван потолок → в полёте оказалось бы 8 вызовов, и MaxConcurrent это покажет
        var generator = new FakeGenerator(
            pairUp: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var service = Service(generator, store);
        // Два владельца — прогоны идут параллельно, потолок держит общий семафор
        var second = _users.Add("imgfill2_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
        foreach (var _ in Enumerable.Range(0, 4))
            service.Enqueue(ImageBackfillKinds.PersonaAvatar, NewPersona().Id, _owner.Id, "аватар", SpendInitiators.Human);
        foreach (var _ in Enumerable.Range(0, 4))
            service.Enqueue(ImageBackfillKinds.PersonaAvatar, NewPersona(second.Id).Id, second.Id, "аватар", SpendInitiators.Human);

        var summary = await service.RunAllAsync();

        Assert.Equal(8, summary.Generated);
        Assert.True(generator.MaxConcurrent <= 2, $"одновременных вызовов: {generator.MaxConcurrent}");
        Assert.Empty(store.All);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Прогон_владельца_не_запускается_дважды_одновременно()
    {
        var persona = NewPersona();
        var store = NewStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(gate: (entered, gate));
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Human);

        var first = service.RunAsync(_owner.Id);
        // Ждём СОБЫТИЕ входа в генерацию, а не таймер (CI слабее)
        await Task.WhenAny(entered.Task, first);

        var second = await service.RunAsync(_owner.Id);
        Assert.Equal(ImageBackfillSummary.Empty, second);

        gate.SetResult();
        Assert.Equal(new ImageBackfillSummary(1, 0, 0), await first);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public void Очередь_идемпотентна_и_переживает_рестарт()
    {
        var persona = NewPersona();
        var store = NewStore();

        Assert.True(store.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Agent));
        Assert.False(store.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар", SpendInitiators.Agent));
        Assert.Single(store.All);

        // Новый экземпляр читает тот же файл — заявка на месте, вместе с инициатором:
        // догоняющая генерация назовёт его именно из заявки (S-SPEND-8)
        var reloaded = NewStore();
        var queued = Assert.Single(reloaded.All);
        Assert.Equal(persona.Id, queued.EntityId);
        Assert.Equal("аватар", queued.Prompt);
        Assert.Equal(SpendInitiators.Agent, queued.Initiator);

        Assert.Equal(1, reloaded.MarkFailed(ImageBackfillKinds.PersonaAvatar, persona.Id, "no-image"));
        Assert.Equal("no-image", NewStore().Find(ImageBackfillKinds.PersonaAvatar, persona.Id)!.LastFailReason);

        reloaded.Remove(ImageBackfillKinds.PersonaAvatar, persona.Id);
        Assert.Empty(NewStore().All);
    }

    // Драйвер-заглушка: считает вызовы и пик одновременности; answers(callNumber) = отдать ли
    // картинку (false — провайдер вернул пусто, транзиентный отказ). gate — пауза внутри
    // вызова на событии, без Task.Delay.
    private sealed class FakeGenerator(
        bool enabled = true,
        Func<int, bool>? answers = null,
        (TaskCompletionSource Entered, TaskCompletionSource Release)? gate = null,
        TaskCompletionSource? pairUp = null) : IImageGenerator
    {
        private int _calls;
        private int _concurrent;
        private int _maxConcurrent;

        public string Key => "fal";
        public string DisplayName => "fal";
        public bool Enabled => enabled;
        public IReadOnlyList<ImageModelInfo> Models => [];

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public string? LastPrompt { get; private set; }

        public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
            string prompt, int count, string? model, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            LastPrompt = prompt;
            var now = Interlocked.Increment(ref _concurrent);
            int peak;
            while (now > (peak = Volatile.Read(ref _maxConcurrent))
                   && Interlocked.CompareExchange(ref _maxConcurrent, now, peak) != peak) { }
            try
            {
                if (gate is { } g)
                {
                    g.Entered.TrySetResult();
                    await g.Release.Task.WaitAsync(ct);
                }
                if (pairUp is not null)
                {
                    // Отпускаем обоих, как только в полёте оказалось два вызова
                    if (now >= 2) pairUp.TrySetResult();
                    await pairUp.Task.WaitAsync(ct);
                }
                return answers?.Invoke(call) == false
                    ? []
                    : [new GeneratedImage([0x89, 0x50, 0x4E, 0x47], "image/png")];
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
