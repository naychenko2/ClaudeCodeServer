using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// PresetStore: точечные переименование/удаление пресета поверх SpecialtySettingsStore
// (волна настройки моделей 2026-08-14). Слой-структуру стора PresetStore не заменяет —
// правит только список пресетов, остальные значения слоя обязаны пережить операцию.
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
        return new SpecialtySettingsStore(config, NullLogger<SpecialtySettingsStore>.Instance);
    }

    private static ModelRoutePreset Preset(string id, string name, params string[] steps) => new()
    {
        Id = id,
        Name = name,
        Steps = steps.ToList(),
    };

    [Fact]
    public void Rename_ЛичныйПресет_МеняетИмяНеТрогаяОстальное()
    {
        var store = Store();
        store.SetOwner("u1", new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "Старое", "opus"), Preset("p2", "Сосед", "glm-5.2") },
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { TierStrong = "spec-opus" } },
        });

        PresetStore.Rename(store, "u1", "p1", "Новое имя").Should().BeNull();

        var layer = store.Snapshot.Owners["u1"];
        layer.Presets.Single(p => p.Id == "p1").Name.Should().Be("Новое имя");
        layer.Presets.Single(p => p.Id == "p1").Steps.Should().BeEquivalentTo(["opus"]);
        // Соседний пресет и специальность пережили перезапись слоя
        layer.Presets.Single(p => p.Id == "p2").Name.Should().Be("Сосед");
        layer.Specialties["backendExecutor"].TierStrong.Should().Be("spec-opus");
    }

    [Fact]
    public void Rename_ГлобальныйПресет_МеняетИмяВОбщемСлое()
    {
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("g1", "Общий", "tier:strong") } });

        PresetStore.Rename(store, "любой-владелец", "g1", "Общий 2").Should().BeNull();

        store.Snapshot.Global.Presets.Single().Name.Should().Be("Общий 2");
        // И видно всем владельцам (эффективный список)
        store.EffectivePresets("u9").Single().Name.Should().Be("Общий 2");
    }

    [Fact]
    public void Rename_НеизвестныйId_Ошибка()
    {
        var store = Store();
        store.SetOwner("u1", new SpecialtySettingsLayer { Presets = { Preset("p1", "Есть", "opus") } });

        PresetStore.Rename(store, "u1", "no-such", "X").Should().Be("Пресет не найден");
        store.Snapshot.Owners["u1"].Presets.Should().HaveCount(1);
    }

    [Fact]
    public void Rename_ПустоеИмя_ОтклоняетсяВалидацией()
    {
        var store = Store();
        store.SetOwner("u1", new SpecialtySettingsLayer { Presets = { Preset("p1", "Есть", "opus") } });

        PresetStore.Rename(store, "u1", "p1", "  ").Should().NotBeNull("пустое имя — ошибка валидации");
        store.Snapshot.Owners["u1"].Presets.Single().Name.Should().Be("Есть");
    }

    [Fact]
    public void Delete_ЛичныйПресет_УдаляетТолькоВыбранный()
    {
        var store = Store();
        store.SetOwner("u1", new SpecialtySettingsLayer
        {
            Presets = { Preset("p1", "Удаляем", "opus"), Preset("p2", "Остаётся", "glm-5.2") },
        });

        var deleted = PresetStore.Delete(store, "u1", "p1");

        deleted.Should().NotBeNull();
        deleted!.Preset.Id.Should().Be("p1");
        deleted.Scope.Should().Be(PresetScope.Owner);
        store.Snapshot.Owners["u1"].Presets.Single(p => p.Id == "p2").Name.Should().Be("Остаётся");
    }

    [Fact]
    public void Delete_ПоследнийЛичныйПресет_СнимаетЛичныйСлой()
    {
        // Пустой личный слой SetOwner убирает — владелец возвращается к глобальным значениям
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("g1", "Общий", "opus") } });
        store.SetOwner("u1", new SpecialtySettingsLayer { Presets = { Preset("p1", "Единственный", "glm-5.2") } });

        PresetStore.Delete(store, "u1", "p1");

        store.Snapshot.Owners.Should().NotContainKey("u1",
            "личный слой без пресетов пуст — стор снимает его сам");
        // Глобальные не задеты
        store.Snapshot.Global.Presets.Should().HaveCount(1);
    }

    [Fact]
    public void Delete_ЛичныйИГлобальныйСОднимId_УдаляетЛичныйРезолвящийсяПервым()
    {
        // Find ищет в порядке EffectivePresets (личные раньше глобальных): при совпадении id
        // правится/удаляется личний — тот, что реально резолвится по ссылке preset:{id}.
        var store = Store();
        store.SetGlobal(new SpecialtySettingsLayer { Presets = { Preset("dup", "Глобальный", "opus") } });
        store.SetOwner("u1", new SpecialtySettingsLayer { Presets = { Preset("dup", "Личный", "glm-5.2") } });

        var deleted = PresetStore.Delete(store, "u1", "dup");

        deleted!.Scope.Should().Be(PresetScope.Owner);
        store.Snapshot.Global.Presets.Single().Name.Should().Be("Глобальный",
            "одноимённый глобальный живёт рядом и не затирается");
    }

    [Fact]
    public void Delete_НеизвестныйId_Null()
    {
        var store = Store();
        PresetStore.Delete(store, "u1", "no-such").Should().BeNull();
    }
}
