using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
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
        catalog.Reason.Should().Be(ImageEditCatalogReasons.NoProviderConfigured);
    }

    // Лежащий ComfyUI: заведён (LocalMedia:Enabled), но не отвечает
    private static LocalImageEditor LocalDown() =>
        new(new LocalImageEditorTests.FakeMedia { Available = false });

    [Fact]
    public void Явный_local_админа_при_лежащем_ComfyUI_не_подменяется_облаком()
    {
        var catalog = ImageEditCatalog.Build([Fal, LocalDown()], LocalImageEditor.ProviderKey, null);

        catalog.Default.Should().Be(new ImageEditDefaultDto(LocalImageEditor.ProviderKey, ImageEditCatalog.AutoModelId));
        var local = catalog.Providers.Single(p => p.Key == LocalImageEditor.ProviderKey);
        local.Available.Should().BeFalse("пункт виден с пометкой «не отвечает», а не скрыт");
        catalog.Providers.Single(p => p.Key == "fal").Available.Should().BeTrue();
    }

    [Fact]
    public void Авто_при_лежащем_local_берёт_первого_доступного()
    {
        var catalog = ImageEditCatalog.Build([LocalDown(), Fal], "auto", null);

        catalog.Default.Should().Be(new ImageEditDefaultDto("fal", ImageEditCatalog.AutoModelId));
        catalog.Providers.Select(p => p.Key).Should().Equal("fal", LocalImageEditor.ProviderKey);
    }

    // Приёмка Д7: при local-media-default «Авто» берёт свою видеокарту первой, облако — только явным выбором
    [Fact]
    public void Авто_при_флаге_local_media_default_берёт_local_даже_при_настроенном_облаке()
    {
        var local = new LocalImageEditor(new LocalImageEditorTests.FakeMedia());

        ImageEditCatalog.Build([Fal, local], "auto", null, preferLocal: true).Default.Provider
            .Should().Be(LocalImageEditor.ProviderKey);
        ImageEditCatalog.Build([Fal, local], "auto", null).Default.Provider.Should().Be("fal", "без флага — как раньше");
        ImageEditCatalog.Build([Fal, local], "fal", null, preferLocal: true).Default.Provider
            .Should().Be("fal", "явный выбор админа флаг не трогает");
        ImageEditCatalog.Build([Fal, LocalDown()], "auto", null, preferLocal: true).Default.Provider
            .Should().Be("fal", "лежащая видеокарта — не повод молча ждать её");
    }

    [Fact]
    public void Строка_Авто_при_флаге_бесплатная_локальная_а_операция_без_local_уходит_в_облако()
    {
        var local = new LocalImageEditor(new LocalImageEditorTests.FakeMedia());
        IImageEditor[] editors = [Fal, local];
        var catalog = ImageEditCatalog.Build(editors, "auto", null, preferLocal: true);

        var auto = ClaudeHomeServer.Services.ImageEditor.ChatContext.ImageExecutorRows
            .Build(catalog, editors, ImageEditOp.Edit, hasImage: true, hasMask: false).First(r => r.Id == "auto");
        auto.Free.Should().BeTrue();
        auto.Sub.Should().StartWith("сейчас: локально");

        ClaudeHomeServer.Services.ImageEditor.ChatContext.ImageExecutorRows.LocalCan(catalog, ImageEditOp.Edit, true, false).Should().BeTrue();
        ClaudeHomeServer.Services.ImageEditor.ChatContext.ImageExecutorRows.LocalCan(catalog, ImageEditOp.RemoveBackground, true, false)
            .Should().BeFalse("в каталоге local такой операции нет — контроллер вернёт «Авто» прежний");
    }

    [Fact]
    public void Незаведённый_local_отсутствует_в_каталоге()
    {
        var off = new LocalImageEditor(new LocalImageEditorTests.FakeMedia { Configured = false, Available = false });
        var noSeam = new LocalImageEditor(null);

        ImageEditCatalog.Build([Fal, off], LocalImageEditor.ProviderKey, null).Providers.Select(p => p.Key)
            .Should().Equal("fal");
        ImageEditCatalog.Build([Fal, noSeam], LocalImageEditor.ProviderKey, null).Default.Provider
            .Should().Be("fal", "без подсистемы images поставщика local нет вовсе");
    }

    [Fact]
    public void Есть_один_поставщик_причины_нет()
    {
        var catalog = ImageEditCatalog.Build([HiggsfieldOff, Fal], null, null);

        catalog.Providers.Should().ContainSingle();
        catalog.Reason.Should().BeNull();
    }
}
