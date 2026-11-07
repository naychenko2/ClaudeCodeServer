using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Учёт расхода серверной генерации картинок (дефект 22068e9d, шаг S-SPEND-2).
// Проверяется конечное состояние после полного вызова: трата пишется на попросившего,
// за ФАКТИЧЕСКИ отработавшего провайдера (после фолбэка), первой — всегда запись без
// суммы, сумму догоняет отдельная запись без генераций (прецедент ImageEditJobService).
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
        private readonly TaskCompletionSource _second = new();

        public IReadOnlyList<SpendRecord> Records
        {
            get { lock (_records) return [.. _records]; }
        }

        public void Record(SpendRecord record)
        {
            lock (_records)
            {
                _records.Add(record);
                if (_records.Count >= 2) _second.TrySetResult();
            }
        }

        // Дожидаться второй записи: не дождался — значит её никто и не писал
        public async Task WaitForSecond(TimeSpan timeout) =>
            await Task.WhenAny(_second.Task, Task.Delay(timeout));
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
    // (у glif его нет, и это тоже проверяемый случай).
    private sealed class FakeGenerator(string key, bool enabled, string? requestId = null, bool returnsImages = true)
        : IImageGenerator
    {
        public string Key => key;
        public string DisplayName => key;
        public bool Enabled => enabled;
        public IReadOnlyList<ImageModelInfo> Models { get; init; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
            string prompt, int count, string? model, CancellationToken ct = default)
        {
            Calls++;
            IReadOnlyList<GeneratedImage> result = returnsImages
                ? Enumerable.Range(1, count).Select(_ => new GeneratedImage([1, 2, 3], "image/png", requestId)).ToList()
                : [];
            return Task.FromResult(result);
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

        var images = await service.GenerateManyAsync(Avatar, "кот", 2, CancellationToken.None, Owner);

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
        first.ProjectId.Should().BeNull("генерация аватара не относится к проекту");

        var topUp = spend.Records.Last();
        topUp.Should().NotBeSameAs(first);
        topUp.Generations.Should().Be(0, "догоняющая запись не повторяет генерации");
        topUp.CostUsd.Should().Be(0.0123);
        topUp.Label.Should().Be(ImageGenerationService.SpendLabel);
        topUp.Source.Should().Be(SpendSources.Fal);
    }

    [Fact]
    public async Task Glif_ТратаПишетсяСчётчиком_ЦенуНеОпрашиваем()
    {
        var glif = new FakeGenerator("glif", enabled: true);   // идентификатора запроса у glif нет
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.01);
        var service = new ImageGenerationService([glif], Store(NewTempDir()), null, spend, cost);

        await service.GenerateManyAsync(Avatar, "кот", 1, CancellationToken.None, Owner);
        await spend.WaitForSecond(TimeSpan.FromMilliseconds(300));

        var rec = spend.Records.Single();
        rec.Source.Should().Be(SpendSources.Glif);
        rec.Generations.Should().Be(1);
        rec.CostUsd.Should().BeNull();
        rec.Label.Should().Be(ImageGenerationService.SpendLabelPendingAmount);
        cost.Calls.Should().Be(0, "опрашивать цену не по чему: request_id не приходил");
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

        await service.GenerateManyAsync(Avatar, "кот", 1, CancellationToken.None, Owner);

        spend.Records.Should().ContainSingle().Which.Source.Should().Be(SpendSources.Fal,
            "пустой провайдер ничего не стоил — трата за того, кто отдал картинки");
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

        await service.GenerateManyAsync(Avatar, "кот", 1, CancellationToken.None, Owner);

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

        var images = await service.GenerateManyAsync(Avatar, "кот", 1);

        images.Should().HaveCount(1, "генерация без владельца работает как раньше");
        spend.Records.Should().BeEmpty("трату не на кого повесить — в «системную» не кладём");
        cost.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ПровайдерНеОтдалКартинок_ТратыНет()
    {
        var fal = new FakeGenerator("fal", enabled: true, requestId: "req-1", returnsImages: false);
        var spend = new CapturingSpend();
        var cost = new FakeCost(0.01);
        var service = new ImageGenerationService([fal], Store(NewTempDir()), null, spend, cost);

        Assert.Empty(await service.GenerateManyAsync(Avatar, "кот", 1, CancellationToken.None, Owner));

        spend.Records.Should().BeEmpty();
        cost.Calls.Should().Be(0);
    }
}
