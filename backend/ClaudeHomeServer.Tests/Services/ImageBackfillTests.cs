using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Images;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
    private readonly List<ServerMessage> _broadcasts = [];
    private readonly Mock<IHubContext<SessionHub>> _hub;

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

        // Перехват broadcast-сообщений в группу (паттерн GlifCostPipelineTests)
        _hub = new Mock<IHubContext<SessionHub>>();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync("message", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage m) _broadcasts.Add(m);
            })
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hub.Setup(h => h.Clients).Returns(clients.Object);
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
    {
        var settings = new ImageGenerationSettingsStore(_config);
        var images = new ImageGenerationService([generator], settings);
        return new ImageBackfillService(store ?? NewStore(), images, _personas,
            _hub.Object, new FakeLifetime(), NullLogger<ImageBackfillService>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };
    }

    [Fact]
    public async Task Прогон_догоняет_аватар_сПромптомЗаявки_и_снимает_заявку()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, null);

        var summary = await service.RunAsync(_owner.Id);

        Assert.Equal(new ImageBackfillSummary(1, 0, 0), summary);
        var saved = _personas.GetByIdInternal(persona.Id)!;
        Assert.Equal(PersonaAvatarKind.Image, saved.Avatar.Kind);
        Assert.True(File.Exists(Path.Combine(_personas.AssetsDir, persona.Id, saved.Avatar.ImageFile!)));

        var backfilled = Assert.Single(_broadcasts.OfType<ImageBackfilledMessage>());
        Assert.Equal(ImageBackfillKinds.PersonaAvatar, backfilled.Kind);
        Assert.Equal(persona.Id, backfilled.EntityId);
        // Раздел «Персоны» слушает штатное personas_changed — его шлём вдобавок
        Assert.Contains(_broadcasts.OfType<PersonasChangedMessage>(),
            m => m.Action == "updated" && m.PersonaId == persona.Id);
    }

    [Fact]
    public async Task Повторный_прогон_ничего_не_генерирует()
    {
        var persona = NewPersona();
        var store = NewStore();
        var generator = new FakeGenerator();
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, "нет-такой-персоны", _owner.Id, "аватар");

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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
        offline.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");
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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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
            service.Enqueue(ImageBackfillKinds.PersonaAvatar, NewPersona().Id, _owner.Id, "аватар");
        foreach (var _ in Enumerable.Range(0, 4))
            service.Enqueue(ImageBackfillKinds.PersonaAvatar, NewPersona(second.Id).Id, second.Id, "аватар");

        var summary = await service.RunAllAsync();

        Assert.Equal(8, summary.Generated);
        Assert.True(generator.MaxConcurrent <= 2, $"одновременных вызовов: {generator.MaxConcurrent}");
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Прогон_владельца_не_запускается_дважды_одновременно()
    {
        var persona = NewPersona();
        var store = NewStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(gate: (entered, gate));
        var service = Service(generator, store);
        service.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар");

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

        Assert.True(store.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар"));
        Assert.False(store.Enqueue(ImageBackfillKinds.PersonaAvatar, persona.Id, _owner.Id, "аватар"));
        Assert.Single(store.All);

        // Новый экземпляр читает тот же файл — заявка на месте
        var reloaded = NewStore();
        var queued = Assert.Single(reloaded.All);
        Assert.Equal(persona.Id, queued.EntityId);
        Assert.Equal("аватар", queued.Prompt);

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
