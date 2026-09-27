using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Execution;

// Реализация ILauncherFactory (контракт в Core, реализация держит IUserStore/SandboxManager
// и потому живёт в Main). Канал устройств (шов IDeviceExecChannel, реализует Desktop)
// необязателен: без него проект на устройстве получает раннер, который честно отказывает при старте.
// Шлюз хода (шов IDeviceTurnGateway, реализует Llm) так же необязателен: без него раннер
// устройства откажет при старте текстом шлюза, до открытия канала.
// Канал и шлюз резолвятся лениво, при первом проекте на устройстве: прямая зависимость в конструкторе
// замыкала граф синглтонов (канал → маршрутизатор Desktop → … → SessionManager → фабрика),
// и хост зависал на старте.
public sealed class LauncherFactory(IUserStore users, SandboxManager sandbox,
    Func<IDeviceExecChannel?>? deviceExec = null, Func<IDeviceTurnGateway?>? deviceGateway = null) : ILauncherFactory
{
    private readonly ConcurrentDictionary<string, DockerProcessRunner> _sandboxed = new();
    private readonly ConcurrentDictionary<(string Owner, string Device), RemoteProcessRunner> _remote = new();

    public IProcessLauncher Local => LocalProcessRunner.Instance;

    public IProcessLauncher ForOwner(string? ownerId) => OwnerEnvironment(ownerId);

    public IProcessLauncher ForProject(Project project)
    {
        if (!ProjectCapabilities.IsDeviceBound(project)) return OwnerEnvironment(project.OwnerId);
        // Проект на устройстве без владельца или без канала — не повод исполнять его на сервере:
        // раннер устройства откажет при старте (устройство не найдено), ход закончится ошибкой
        var channel = project.OwnerId is null ? UnavailableChannel.Instance : deviceExec?.Invoke() ?? UnavailableChannel.Instance;
        var gateway = deviceGateway?.Invoke() ?? UnavailableGateway.Instance;
        return _remote.GetOrAdd((project.OwnerId ?? "", project.DeviceId!),
            key => new RemoteProcessRunner(channel, gateway, key.Owner, key.Device));
    }

    private IProcessLauncher OwnerEnvironment(string? ownerId)
    {
        if (ownerId is null) return Local;
        var env = users.GetById(ownerId)?.ExecutionEnvironment;
        // Fail closed: если песочница не настроена, container-пользователь получит
        // понятную ошибку из SandboxManager.EnsureRunningAsync, а не тихий запуск на хосте
        return env == ExecutionEnvironments.Container
            ? _sandboxed.GetOrAdd(ownerId, id => new DockerProcessRunner(sandbox, id))
            : Local;
    }

    // Канал устройств недоступен (вертикаль Desktop не зарегистрирована или у проекта нет владельца)
    private sealed class UnavailableChannel : IDeviceExecChannel
    {
        public static readonly UnavailableChannel Instance = new();

        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => null;

        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new DeviceExecRefusedException(DeviceExecRefusal.UnknownDevice,
                ProjectCapabilities.DeviceMissingReason + " — сообщение не взято в работу.");
    }

    // Шлюз хода недоступен (вертикаль Llm не зарегистрирована): маршрута и токена ходу не выдать
    private sealed class UnavailableGateway : IDeviceTurnGateway
    {
        public static readonly UnavailableGateway Instance = new();

        public DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model) =>
            new(null, "Шлюз моделей недоступен на сервере — ход на устройстве не запущен.");

        public void EndTurn(string gatewayTurnId) { }
    }
}
