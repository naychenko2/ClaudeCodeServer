using ClaudeHomeServer.DeviceAgent.Tray;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>
/// Кнопка «Стоп» плашки без Win32: срабатывает только на полном клике по ней самой. Инцидент
/// 28.09 — ход прерывался отпусканием кнопки мыши над «Стоп» после нажатия в чужом окне.
/// </summary>
public class PlateStopButtonTests
{
    [Fact]
    public void Нажатие_и_отпускание_на_кнопке_это_стоп()
    {
        var stop = new PlateStopButton();

        stop.Down(onStop: true).Should().BeTrue("взведённая кнопка захватывает мышь");
        stop.Move(onStop: true).Should().BeFalse();
        stop.Up(onStop: true).Should().BeTrue();
        stop.Armed.Should().BeFalse();
    }

    [Fact]
    public void Отпускание_на_кнопке_без_своего_нажатия_не_стоп()
    {
        // Кнопку зажали в окне человека и отпустили над плашкой: WM_LBUTTONDOWN сюда не приходил
        new PlateStopButton().Up(onStop: true).Should().BeFalse();
    }

    [Fact]
    public void Нажатие_вне_кнопки_и_отпускание_на_ней_не_стоп()
    {
        var stop = new PlateStopButton();

        stop.Down(onStop: false).Should().BeFalse("нажатие мимо «Стоп» — перетаскивание, не взвод");
        stop.Move(onStop: true);
        stop.Up(onStop: true).Should().BeFalse();
    }

    [Fact]
    public void Нажатие_на_кнопке_и_увод_курсора_снимают_взвод_насовсем()
    {
        var stop = new PlateStopButton();
        stop.Down(onStop: true);

        stop.Move(onStop: false).Should().BeTrue("ушли с кнопки — захват пора отпустить");
        stop.Up(onStop: false).Should().BeFalse();

        stop.Down(onStop: true);
        stop.Move(onStop: false);
        stop.Move(onStop: true).Should().BeFalse();
        stop.Up(onStop: true).Should().BeFalse("вернуться на кнопку и отпустить — уже не «Стоп»");
    }

    [Fact]
    public void Потеря_захвата_снимает_взвод()
    {
        var stop = new PlateStopButton();
        stop.Down(onStop: true);

        stop.Cancel();

        stop.Up(onStop: true).Should().BeFalse();
    }

    [Fact]
    public void Взвод_одноразовый()
    {
        var stop = new PlateStopButton();
        stop.Down(onStop: true);
        stop.Up(onStop: true).Should().BeTrue();

        // Второе отпускание без нового нажатия (двойное сообщение, отпускание из чужого окна)
        stop.Up(onStop: true).Should().BeFalse();
    }
}
