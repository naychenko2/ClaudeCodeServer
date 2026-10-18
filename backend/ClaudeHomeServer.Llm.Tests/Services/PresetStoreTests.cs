using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// PresetStore: точечные переименование/удаление пресета поверх SpecialtySettingsStore
// (волна настройки моделей 2026-08-14). Слой-структуру стора PresetStore не заменяет —
// правит только список пресетов, остальные значения слоя обязаны пережить операцию.
// После снятия слоёв (ADR-012) слой один — общий: личных и назначенных пресетов нет,
// PresetScope у найденного всегда Global.
public class PresetStoreTests
{
    private static SpecialtySettingsStore Store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
        });
        return TestSpecialtyStore.Create(config);
    }

    private static ModelRoutePreset Preset(string id, string name, params string[] steps) => new()
    {
        Id = id,
        Name = name,
        Steps = steps.ToList(),
    };

    [Fact]
    public void Rename_МеняетИмяНеТрогаяОстальное()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "Старое", "opus"), Preset("p2", "Сосед", "glm-5.2") },
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { TierStrong = "spec-opus" } },
        });

        PresetStore.Rename(store, "u1", "p1", "Новое имя").Should().BeNull();

        var layer = store.Snapshot.Global;
        layer.Presets.Single(p => p.Id == "p1").Name.Should().Be("Новое имя");
        layer.Presets.Single(p => p.Id == "p1").Steps.Should().BeEquivalentTo(["opus"]);
        // Соседний пресет и специальность пережили перезапись слоя
        layer.Presets.Single(p => p.Id == "p2").Name.Should().Be("Сосед");
        layer.Specialties["backendExecutor"].TierStrong.Should().Be("spec-opus");
    }

    [Fact]
    public void Rename_ПресетВиденВсемВладельцам()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("g1", "Общий", "tier:strong") } });

        PresetStore.Rename(store, "любой-владелец", "g1", "Общий 2").Should().BeNull();

        store.Snapshot.Global.Presets.Single().Name.Should().Be("Общий 2");
        store.EffectivePresets("u9").Single().Name.Should().Be("Общий 2");
    }

    [Fact]
    public void Rename_НеизвестныйId_Ошибка()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "Есть", "opus") } });

        PresetStore.Rename(store, "u1", "no-such", "X").Should().Be("Пресет не найден");
        store.Snapshot.Global.Presets.Should().HaveCount(1);
    }

    [Fact]
    public void Rename_ПустоеИмя_ОтклоняетсяВалидацией()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("p1", "Есть", "opus") } });

        PresetStore.Rename(store, "u1", "p1", "  ").Should().NotBeNull("пустое имя — ошибка валидации");
        store.Snapshot.Global.Presets.Single().Name.Should().Be("Есть");
    }

    [Fact]
    public void Delete_УдаляетТолькоВыбранный()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "Удаляем", "opus"), Preset("p2", "Остаётся", "glm-5.2") },
        });

        var deleted = PresetStore.Delete(store, "u1", "p1");

        deleted.Should().NotBeNull();
        deleted!.Preset.Id.Should().Be("p1");
        deleted.Scope.Should().Be(PresetScope.Global, "слой один — общий");
        store.Snapshot.Global.Presets.Single(p => p.Id == "p2").Name.Should().Be("Остаётся");
    }

    [Fact]
    public void Delete_ПоследнийПресет_ОстальныеЗначенияСлояЦелы()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { Preset("g1", "Единственный", "opus") },
            Specialties = { ["analyst"] = new SpecialtyTemplateSettings { TierMedium = "glm-5.2" } },
        });

        PresetStore.Delete(store, "u1", "g1");

        store.Snapshot.Global.Presets.Should().BeEmpty();
        store.Snapshot.Global.Specialties["analyst"].TierMedium.Should().Be("glm-5.2",
            "удаление пресета не трогает настройки специальностей");
    }

    [Fact]
    public void Delete_НеизвестныйId_Null()
    {
        var store = Store();
        PresetStore.Delete(store, "u1", "no-such").Should().BeNull();
    }
}
