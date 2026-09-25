using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Каталог редактора (ADR-017, раздел 2): в списке только доступные поставщики,
// ненастроенный скрыт, а не роняет ответ; умолчание админа — лишь предвыбор.
public class ImageEditCatalogTests
{
    private static readonly FakeImageEditor Fal = new("fal", models: FakeImageEditor.Model("fal-ai/nano-banana-2/edit"));
    private static readonly FakeImageEditor HiggsfieldOff = new("higgsfield", enabled: false,
        models: FakeImageEditor.Model("nano_banana_2"));
    private static readonly FakeImageEditor HiggsfieldOn = new("higgsfield", models: FakeImageEditor.Model("nano_banana_2"));

    [Fact]
    public void Ненастроенный_поставщик_отсутствует_в_каталоге()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOff, Fal], adminProvider: null, adminModel: null);

        catalog.Providers.Select(p => p.Key).Should().Equal("fal");
    }

    [Fact]
    public void Поставщик_с_бросающей_проверкой_доступности_скрыт_без_исключения()
    {
        var broken = new FakeImageEditor("higgsfield", throwOnEnabled: true);

        var catalog = ImageEditCatalog.Build([broken, Fal], null, null);

        catalog.Providers.Select(p => p.Key).Should().Equal("fal");
    }

    [Fact]
    public void Порядок_fal_затем_higgsfield_и_первым_пунктом_Авто_с_режимами()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOn, Fal], null, null);

        catalog.Providers.Select(p => p.Key).Should().Equal("fal", "higgsfield");
        var auto = catalog.Providers[0].Models[0];
        auto.Id.Should().Be(ImageEditCatalog.AutoModelId);
        auto.Modes.Should().Equal(EditMode.Fast, EditMode.Precise, EditMode.Photoreal);
        auto.Caps.Should().BeNull();
        catalog.Providers[0].Models[1].Caps!.Mask.Should().Be(MaskSupport.AsReference);
        catalog.Providers[1].PriceUnit.Should().Be(ImageEditPriceUnits.Credits);
    }

    [Fact]
    public void Умолчание_админа_на_доступного_поставщика_с_его_моделью()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOn, Fal], "higgsfield", "nano_banana_2");

        catalog.Default.Should().Be(new ImageEditDefaultDto("higgsfield", "nano_banana_2"));
    }

    [Fact]
    public void Умолчание_админа_с_незнакомой_моделью_даёт_Авто()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOn, Fal], "higgsfield", "soul_2");

        catalog.Default.Should().Be(new ImageEditDefaultDto("higgsfield", ImageEditCatalog.AutoModelId));
    }

    [Fact]
    public void Умолчание_админа_на_недоступного_молча_уступает_первому_доступному()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOff, Fal], "higgsfield", "nano_banana_2");

        catalog.Default.Should().Be(new ImageEditDefaultDto("fal", ImageEditCatalog.AutoModelId));
    }

    [Fact]
    public void Нет_доступных_поставщиков_пустой_список_и_умолчание_без_поставщика()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOff], "auto", null);

        catalog.Providers.Should().BeEmpty();
        catalog.Default.Provider.Should().BeNull();
        catalog.Limits.Should().Be(ImageEditCatalog.DefaultLimits);
    }
}
