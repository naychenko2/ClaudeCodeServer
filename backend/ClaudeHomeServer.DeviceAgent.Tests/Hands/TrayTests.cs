using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.DeviceAgent.Tray;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>
/// Логика трея рук (Ш7) без UI: значок, подсказка, меню и плашка по кадрам pipe; «Стоп» — кадр
/// в pipe. Решения владельца 1в и 2б: ни сеанса, ни программ в меню быть не должно.
/// </summary>
public class TrayModelTests
{
    private static readonly HandsTrayDevice Device = new("https://home.example.ru/", "home-pc", "1.842.0",
        [@"C:\Проекты\Бухгалтерия", @"D:\work"], @"C:\Users\me\AppData\Local\ai-home-agent\logs");

    private static readonly HandsActiveTurn Turn = new("turn-1", @"C:\Проекты\Бухгалтерия\", DateTimeOffset.UtcNow);

    private static HandsPipeMessage Status(bool online = true, bool installed = true, params HandsActiveTurn[] turns) =>
        new(HandsPipeTypes.Status, Status: new HandsTrayStatus(installed, turns, online, Device));

    private static TrayModel Connected(HandsPipeMessage? status = null)
    {
        var model = new TrayModel();
        model.OnConnected();
        model.Apply(status ?? Status());
        return model;
    }

    private static IEnumerable<TrayMenuItem> Flatten(IEnumerable<TrayMenuItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children ?? [])));

    private static TrayMenuItem StopItem(TrayModel model) => model.Menu().Single(i => i.Command == TrayCommands.Stop);

    [Fact]
    public void Агент_не_отвечает_серый_значок_и_только_выход()
    {
        var model = new TrayModel();

        model.Icon.Should().Be(TrayIconKind.Offline);
        model.Tooltip.Should().Be("AI Home — агент устройства · агент не отвечает");
        model.Plate.Visible.Should().BeFalse();
        model.StopRequest().Should().BeNull();
        model.Menu().Where(i => i.Command is not null).Select(i => i.Command).Should().Equal(TrayCommands.Exit);
    }

    [Fact]
    public void На_связи_без_хода_контур_и_Стоп_недоступен()
    {
        var model = Connected();

        model.Icon.Should().Be(TrayIconKind.Idle);
        model.Tooltip.Should().Be("AI Home — агент устройства · на связи");
        model.Tooltip.Length.Should().BeLessThanOrEqualTo(63);
        model.Plate.Visible.Should().BeFalse();
        StopItem(model).Enabled.Should().BeFalse("руки сейчас не действуют");
        model.StopRequest().Should().BeNull();

        var menu = model.Menu();
        menu.Select(i => i.Text).Should().ContainInOrder(TrayModel.AppTitle, "На связи · home.example.ru",
            "Руки установлены · сейчас не действуют", "Остановить руки", "Разрешённые папки (2)",
            "Обновления: версия 1.842.0", "Открыть журнал агента", "Выйти из агента");
        var folders = menu.Single(i => i.Text == "Разрешённые папки (2)").Children!;
        folders.Select(f => (f.Command, f.Argument)).Should().Equal(
            (TrayCommands.OpenFolder, @"C:\Проекты\Бухгалтерия"), (TrayCommands.OpenFolder, @"D:\work"));
        menu.Single(i => i.Command == TrayCommands.OpenLog).Argument.Should().Be(Device.LogDirectory);
    }

    [Fact]
    public void В_меню_нет_сеанса_программ_и_паузы()
    {
        var texts = Flatten(Connected(Status(turns: Turn)).Menu()).Concat(Flatten(Connected().Menu()))
            .Select(i => i.Text.ToLowerInvariant()).ToList();

        texts.Should().NotContain(t => t.Contains("разрешить руки") || t.Contains("минут") || t.Contains("программ")
                                       || t.Contains("приостанов") || t.Contains("пауз"));
    }

    [Fact]
    public void Нет_связи_с_сервером_и_руки_не_установлены()
    {
        var model = Connected(Status(online: false, installed: false));

        model.Icon.Should().Be(TrayIconKind.Offline);
        model.Tooltip.Should().Be("AI Home — агент устройства · нет связи с сервером");
        model.Menu().Select(i => i.Text).Should().Contain(["Нет связи с сервером · home.example.ru", "Руки не установлены"]);
    }

    [Fact]
    public void Ход_действует_руками_плашка_Стоп_и_одно_уведомление_на_ход()
    {
        var model = Connected();

        var notification = model.Apply(new HandsPipeMessage(HandsPipeTypes.HandsActive, Turn: Turn));
        model.Apply(Status(turns: Turn)).Should().BeNull("о ходе уже сказано");
        model.Apply(Status(turns: Turn)).Should().BeNull();

        notification.Should().Be(new TrayNotification("ИИ начал работать с компьютером",
            "Проект «Бухгалтерия» управляет окнами. Остановить — «Стоп» в углу экрана или в меню значка."));
        model.Icon.Should().Be(TrayIconKind.HandsActive);
        model.Tooltip.Should().Be("AI Home — ИИ управляет компьютером");
        model.Plate.Should().Be(new PlateView(true, "ИИ управляет компьютером", "Проект «Бухгалтерия»",
            "Фокус может переключаться. Окна хода закроются вместе с ним.", StopVisible: true));
        StopItem(model).Should().Match<TrayMenuItem>(i => i.Enabled && i.IsDefault);
        model.Menu().Select(i => i.Text).Should().Contain("Сейчас работает: проект «Бухгалтерия»");
    }

    [Fact]
    public void Стоп_это_кадр_turn_stop_для_любого_хода_с_руками()
    {
        var model = Connected(Status(turns: Turn));

        model.StopRequest().Should().Be(new HandsPipeMessage(HandsPipeTypes.TurnStop),
            "TurnId не указан: «Стоп» гасит любой ход с руками, а не только показанный");
    }

    [Fact]
    public void После_Стоп_из_трея_плашка_три_секунды_говорит_Руки_выключены()
    {
        var model = Connected(Status(turns: Turn));

        model.Apply(new HandsPipeMessage(HandsPipeTypes.HandsEnded, TurnId: Turn.TurnId, Reason: HandsEndReason.StoppedFromTray));

        model.HandsActive.Should().BeFalse();
        model.ShowsStoppedNotice.Should().BeTrue();
        model.Plate.Should().Be(new PlateView(true, "Руки выключены. Ход прерван.", null, null, StopVisible: false));
        model.Icon.Should().Be(TrayIconKind.Idle);

        model.StoppedNoticeExpired();
        model.Plate.Visible.Should().BeFalse();
    }

    [Fact]
    public void Ход_кончился_сам_плашка_гаснет_сразу_а_новый_ход_снова_уведомляет()
    {
        var model = Connected(Status(turns: Turn));

        model.Apply(new HandsPipeMessage(HandsPipeTypes.HandsEnded, TurnId: Turn.TurnId));
        model.Plate.Visible.Should().BeFalse();

        model.Apply(new HandsPipeMessage(HandsPipeTypes.HandsActive, Turn: Turn with { TurnId = "turn-2", ProjectRoot = null }))
            .Should().Be(new TrayNotification("ИИ начал работать с компьютером",
                "Ход управляет окнами. Остановить — «Стоп» в углу экрана или в меню значка."));
    }

    [Fact]
    public void Обрыв_pipe_гасит_плашку_и_забывает_статус()
    {
        var model = Connected(Status(turns: Turn));
        var changes = 0;
        model.Changed += () => changes++;

        model.OnDisconnected();

        changes.Should().Be(1);
        model.Plate.Visible.Should().BeFalse();
        model.StopRequest().Should().BeNull();
        model.Icon.Should().Be(TrayIconKind.Offline);
    }

    [Fact]
    public void Ошибка_агента_показывается_уведомлением()
    {
        Connected().Apply(new HandsPipeMessage(HandsPipeTypes.Error, Error: "Сейчас ни один ход не управляет компьютером."))
            .Should().Be(new TrayNotification(TrayModel.AppTitle, "Сейчас ни один ход не управляет компьютером."));
    }

    [Fact]
    public void Выход_из_агента_подтверждается_с_упоминанием_хода()
    {
        Connected(Status(turns: Turn)).ExitConfirmation().Text.Should().StartWith(
            "Сейчас ИИ управляет компьютером в проекте «Бухгалтерия». Выход прервёт ход, руки выключатся.");
        Connected().ExitConfirmation().Text.Should().Contain("Агент запустится снова при следующем входе в Windows.");
    }

    [Theory]
    [InlineData(@"C:\Проекты\Бухгалтерия\", "Бухгалтерия")]
    [InlineData("/home/me/report", "report")]
    [InlineData(null, null)]
    [InlineData(@"C:\", "C:")]
    public void Имя_проекта_из_пути_хода(string? root, string? expected) =>
        TrayModel.ProjectName(root).Should().Be(expected);

    [Fact]
    public void Значки_трёх_состояний_различаются_формой()
    {
        var idle = TrayIconArt.Render(TrayIconKind.Idle, 16);
        var active = TrayIconArt.Render(TrayIconKind.HandsActive, 16);
        var offline = TrayIconArt.Render(TrayIconKind.Offline, 16);

        static int Opaque(uint[] px) => px.Count(p => p >> 24 > 128);
        Opaque(active).Should().BeGreaterThan(Opaque(idle), "экран залит");
        Opaque(offline).Should().BeGreaterThan(Opaque(idle), "поверх контура косая черта");
        idle.Should().Contain(p => p != 0);
    }
}

/// <summary>Сквозной pipe «агент ↔ трей»: настоящий <see cref="HandsTrayPipe"/> агента и клиент трея.</summary>
public class TrayPipeClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Модель трея в «потоке окна»: кадры применяются под замком, как их применял бы UI.</summary>
    private sealed class Ui
    {
        private readonly Lock _lock = new();
        public TrayModel Model { get; } = new();

        public void Run(Action action)
        {
            lock (_lock) action();
        }

        public async Task UntilAsync(Func<TrayModel, bool> condition)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (true)
            {
                lock (_lock) if (condition(Model)) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("модель трея не дошла до ожидаемого состояния");
                await Task.Delay(20);
            }
        }
    }

    private static string PipeName() => "ccs-tray-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Трей_видит_ход_с_руками_и_Стоп_гасит_его_без_сервера()
    {
        var registry = new HandsRegistry();
        var name = PipeName();
        var device = new HandsTrayDevice("https://home.example.ru/", "home-pc", "1.0.0", ["/work"], null);
        await using var agent = new HandsTrayPipe(name, registry, () => true, serverOnline: () => false, device: () => device);
        agent.Start();

        var ui = new Ui();
        await using var client = new TrayPipeClient(name,
            () => ui.Run(ui.Model.OnConnected),
            () => ui.Run(ui.Model.OnDisconnected),
            m => ui.Run(() => ui.Model.Apply(m)));
        client.Start();
        await ui.UntilAsync(m => m.Status is not null);
        ui.Model.Status!.Device.Should().BeEquivalentTo(device);

        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? turn = null;
        turn = registry.Attach("turn-1", "/work/report", reason =>
        {
            stopped.TrySetResult(reason);
            turn!.Dispose(); // как KillTree: ход погашен — руки отцепились
        });
        await ui.UntilAsync(m => m.HandsActive);
        ui.Model.Plate.StopVisible.Should().BeTrue();

        HandsPipeMessage request = null!;
        ui.Run(() => request = ui.Model.StopRequest()!);
        (await client.SendAsync(request)).Should().BeTrue();

        (await stopped.Task.WaitAsync(Wait)).Should().Be(HandsEndReason.StoppedFromTray, "«Стоп» дошёл до агента при недоступном сервере");
        await ui.UntilAsync(m => m.ShowsStoppedNotice);
        ui.Model.Plate.Title.Should().Be(TrayModel.StoppedNotice);
    }

    [Fact]
    public async Task Без_агента_Стоп_не_доставлен_а_агент_подхватывается_позже()
    {
        var name = PipeName();
        var ui = new Ui();
        await using var client = new TrayPipeClient(name,
            () => ui.Run(ui.Model.OnConnected),
            () => ui.Run(ui.Model.OnDisconnected),
            m => ui.Run(() => ui.Model.Apply(m)),
            retry: TimeSpan.FromMilliseconds(200));
        client.Start();

        (await client.SendAsync(new HandsPipeMessage(HandsPipeTypes.TurnStop))).Should().BeFalse();

        await using var agent = new HandsTrayPipe(name, new HandsRegistry(), () => true, () => true);
        agent.Start();
        await ui.UntilAsync(m => m.Connected && m.Status is not null);
        ui.Model.Icon.Should().Be(TrayIconKind.Idle);
    }
}
