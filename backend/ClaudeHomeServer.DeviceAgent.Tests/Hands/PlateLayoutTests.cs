using ClaudeHomeServer.DeviceAgent.Tray;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>
/// Плашка «ИИ управляет компьютером» без Win32: прозрачность по наведению, место плашки,
/// сброс в угол, когда запомненное место ушло с экрана, и файл состояния трея.
/// </summary>
public class PlateLayoutTests
{
    private const int Width = 340;
    private const int Height = 112;

    // Основной 1920×1080 с панелью задач 48 px снизу; второй — слева, 2560×1440 при 150 %
    private static readonly PlateMonitor Primary = new(@"\\.\DISPLAY1",
        new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1032), 1.0, true);
    private static readonly PlateMonitor Left = new(@"\\.\DISPLAY2",
        new ScreenRect(-2560, 0, 0, 1440), new ScreenRect(-2560, 0, 0, 1392), 1.5, false);
    private static readonly PlateMonitor[] Both = [Primary, Left];

    [Fact]
    public void Полупрозрачна_а_под_мышью_и_при_перетаскивании_непрозрачна()
    {
        PlateLayout.Alpha(hover: false, dragging: false).Should().Be(PlateLayout.TranslucentAlpha);
        PlateLayout.Alpha(hover: true, dragging: false).Should().Be(PlateLayout.OpaqueAlpha);
        PlateLayout.Alpha(hover: false, dragging: true).Should().Be(PlateLayout.OpaqueAlpha);
        // «около 80 %»
        (PlateLayout.TranslucentAlpha / 255.0).Should().BeApproximately(0.8, 0.02);
    }

    [Fact]
    public void Без_запомненного_места_правый_нижний_угол_основного_монитора()
    {
        var place = PlateLayout.Place(null, [Left, Primary], Width, Height);

        place.Should().Be(new PlatePlacement(Primary, 1920 - 340 - 12, 1032 - 112 - 12, 340, 112));
    }

    [Fact]
    public void Запомненное_место_возвращается_на_тот_же_монитор()
    {
        var saved = PlateLayout.ToSaved(Primary, 200, 300);

        PlateLayout.Place(saved, Both, Width, Height).Should().Be(new PlatePlacement(Primary, 200, 300, 340, 112));
    }

    [Fact]
    public void Место_считается_в_DIP_монитора_и_переживает_смену_масштаба()
    {
        // Отпустили на втором мониторе (150 %) в точке (−2000, 600)
        var saved = PlateLayout.ToSaved(Left, -2000, 600);
        saved.Should().Be(new PlatePosition(@"\\.\DISPLAY2", 560 / 1.5, 400));

        PlateLayout.Place(saved, Both, Width, Height).Should().Be(new PlatePlacement(Left, -2000, 600, 510, 168));

        // Масштаб монитора сменили на 100 % — то же место экрана в долях, размер под новый DPI
        var rescaled = Left with { Scale = 1.0 };
        PlateLayout.Place(saved, [Primary, rescaled], Width, Height)
            .Should().Be(new PlatePlacement(rescaled, -2560 + 373, 400, 340, 112));
    }

    [Fact]
    public void Монитор_отключили_плашка_в_угол_основного()
    {
        var saved = PlateLayout.ToSaved(Left, -2000, 600);

        PlateLayout.Place(saved, [Primary], Width, Height).Should().Be(PlateLayout.Corner(Primary, Width, Height));
    }

    [Fact]
    public void Разрешение_уменьшили_и_место_ушло_за_экран_плашка_в_угол()
    {
        var saved = PlateLayout.ToSaved(Primary, 1700, 950);
        var small = Primary with { Bounds = new ScreenRect(0, 0, 1280, 720), Work = new ScreenRect(0, 0, 1280, 672) };

        PlateLayout.Place(saved, [small], Width, Height).Should().Be(PlateLayout.Corner(small, Width, Height));
    }

    [Fact]
    public void Край_торчит_за_рабочую_область_плашку_прижимает_целиком_на_экран()
    {
        // Правый край на 100 px за экраном, низ залез на панель задач
        var saved = PlateLayout.ToSaved(Primary, 1920 - 240, 980);

        PlateLayout.Place(saved, Both, Width, Height)
            .Should().Be(new PlatePlacement(Primary, 1920 - 340, 1032 - 112, 340, 112));
    }

    [Fact]
    public void Видна_полоска_меньше_порога_плашка_в_угол()
    {
        var saved = PlateLayout.ToSaved(Primary, 1920 - PlateLayout.MinVisible + 1, 100);

        PlateLayout.Place(saved, Both, Width, Height).Should().Be(PlateLayout.Corner(Primary, Width, Height));
    }

    [Fact]
    public void Файл_состояния_хранит_место_между_запусками()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tray-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrayStateStore(dir);
            store.LoadPlate().Should().BeNull();

            var saved = new PlatePosition(@"\\.\DISPLAY2", 373.33, 400);
            store.SavePlate(saved);

            new TrayStateStore(dir).LoadPlate().Should().Be(saved);
            File.Exists(store.FilePath + ".tmp").Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{\"plate\":{\"monitor\":\"\",\"x\":1,\"y\":2}}")]
    [InlineData("{\"plate\":null}")]
    public void Битый_файл_состояния_плашка_в_угол(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tray-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrayStateStore(dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(store.FilePath, content);

            store.LoadPlate().Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Без_аргументов_боевой_трей_с_аргументами_второй_экземпляр()
    {
        TrayOptions.Parse([]).Should().Be(new TrayOptions(null, null));
        TrayOptions.Parse(["--pipe", "AiHomeAgent.Tray.test", "--data", @"C:\ccs-test\plate"])
            .Should().Be(new TrayOptions("AiHomeAgent.Tray.test", @"C:\ccs-test\plate"));
        TrayOptions.Parse(["--pipe"]).Should().Be(new TrayOptions(null, null));
    }
}
