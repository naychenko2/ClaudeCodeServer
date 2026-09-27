using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Pipe «агент ↔ трей» (<see cref="HandsPipe"/>): трей узнаёт, что ИИ управляет компьютером, и
/// жмёт «Стоп». Без localhost-API и без сервера: «Стоп» работает, даже когда связи нет.
/// Доступ — только текущему пользователю ОС: на Windows явный ACL из одного правила, на Unix —
/// <see cref="PipeOptions.CurrentUserOnly"/> (права сокета и сверка учётки собеседника).
/// </summary>
internal sealed class HandsTrayPipe : IAsyncDisposable
{
    private const int MaxInstances = 4;

    private readonly string _name;
    private readonly HandsRegistry _registry;
    private readonly Func<bool> _installed;
    private readonly Func<bool> _serverOnline;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<Client> _clients = [];
    // Ходы с руками, о которых треям уже сказано: из разницы рождаются hands-active/hands-ended
    private HashSet<string> _announced = new(StringComparer.Ordinal);
    private Task? _accept;

    public HandsTrayPipe(string name, HandsRegistry registry, Func<bool> installed, Func<bool> serverOnline, ILogger? log = null)
    {
        _name = name;
        _registry = registry;
        _installed = installed;
        _serverOnline = serverOnline;
        _log = log ?? NullLogger.Instance;
        _registry.Changed += OnRegistryChanged;
    }

    /// <summary>Имя pipe текущего пользователя ОС: на Windows — по SID, на Unix — по имени пользователя.</summary>
    public static string NameForCurrentUser() =>
        HandsPipe.Name(OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : Environment.UserName);

    public void Start()
    {
        // Первый экземпляр — только свой: занятое кем-то заранее имя трей не должен увидеть как агента
        var first = Create(firstInstance: true);
        _accept = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    /// <summary>Снимок для трея.</summary>
    public HandsTrayStatus Status() => new(_installed(), _registry.Active, _serverOnline());

    /// <summary>Разослать снимок всем подключённым треям (перемена связи с сервером, установка компонента).</summary>
    public void Broadcast() => _ = BroadcastAsync(new HandsPipeMessage(HandsPipeTypes.Status, Status: Status()));

    /// <summary>Сервер-сторона pipe; на Windows — с ACL из одного правила для текущего пользователя.</summary>
    internal NamedPipeServerStream Create(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (OperatingSystem.IsWindows())
            return NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                options, 0, 0, CurrentUserOnlySecurity());
        return new NamedPipeServerStream(_name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            options | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// ACL pipe: одно разрешающее правило — текущему пользователю, наследование от родителя
    /// отключено. Ни «Все», ни «Прошедшие проверку», ни другие пользователи машины pipe не откроют.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CurrentUserOnlySecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            catch (IOException e)
            {
                _log.LogWarning("Pipe трея: подключение не принято: {Error}", e.Message);
                await server.DisposeAsync();
                server = Create(firstInstance: false);
                continue;
            }

            var client = new Client(server);
            lock (_lock) _clients.Add(client);
            _ = Task.Run(() => ServeAsync(client, ct));

            try
            {
                server = Create(firstInstance: false);
            }
            catch (IOException e)
            {
                // Все экземпляры заняты: ждём, пока кто-то отключится
                _log.LogWarning("Pipe трея: новый экземпляр не создан: {Error}", e.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return; }
                server = Create(firstInstance: false);
            }
        }
    }

    private async Task ServeAsync(Client client, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineAsync(client.Stream, ct);
                if (line is null) break;

                HandsPipeMessage? request;
                try { request = JsonSerializer.Deserialize<HandsPipeMessage>(line, HandsPipe.Json); }
                catch (JsonException)
                {
                    await client.SendAsync(new HandsPipeMessage(HandsPipeTypes.Error, Error: "кадр не разобран"), ct);
                    continue;
                }
                if (request is null) continue;

                var reply = Handle(request);
                if (reply is not null) await client.SendAsync(reply, ct);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Трей закрылся — забываем его
        }
        finally
        {
            lock (_lock) _clients.Remove(client);
            await client.DisposeAsync();
        }
    }

    /// <summary>Ответ на запрос трея; null — незнакомый тип, пропускается молча.</summary>
    internal HandsPipeMessage? Handle(HandsPipeMessage request)
    {
        switch (request.Type)
        {
            case HandsPipeTypes.Hello when request.Version != HandsPipe.Version:
                return new(HandsPipeTypes.Error, Error: $"трей говорит на версии pipe {request.Version}, агент — на {HandsPipe.Version}");
            case HandsPipeTypes.Hello:
            case HandsPipeTypes.GetStatus:
                return new(HandsPipeTypes.Status, Status: Status());
            case HandsPipeTypes.TurnStop:
                var stopped = _registry.Stop(request.TurnId, HandsEndReason.StoppedFromTray);
                if (stopped == 0) return new(HandsPipeTypes.Error, Error: "Сейчас ни один ход не управляет компьютером.");
                _log.LogWarning("«Стоп» из трея: погашено ходов с руками — {Count}", stopped);
                return new(HandsPipeTypes.Status, Status: Status());
            default:
                return null;
        }
    }

    private void OnRegistryChanged()
    {
        var active = _registry.Active;
        var now = active.Select(t => t.TurnId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> before;
        lock (_lock)
        {
            before = _announced;
            _announced = now;
        }

        var messages = new List<HandsPipeMessage>();
        foreach (var turn in active.Where(t => !before.Contains(t.TurnId)))
            messages.Add(new(HandsPipeTypes.HandsActive, Turn: turn));
        foreach (var gone in before.Where(id => !now.Contains(id)))
            messages.Add(new(HandsPipeTypes.HandsEnded, TurnId: gone, Reason: _registry.StopReasonOf(gone)));
        messages.Add(new(HandsPipeTypes.Status, Status: Status()));
        _ = BroadcastAsync([.. messages]);
    }

    private async Task BroadcastAsync(params HandsPipeMessage[] messages)
    {
        Client[] clients;
        lock (_lock) clients = _clients.ToArray();
        foreach (var client in clients)
        {
            foreach (var message in messages)
            {
                try { await client.SendAsync(message, _stop.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { break; }
            }
        }
    }

    /// <summary>Строка до <c>\n</c>, не длиннее потолка кадра; null — трей закрыл pipe.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray());
            if (buffer.Length >= HandsPipe.MaxMessageBytes) throw new IOException("кадр pipe длиннее потолка");
            buffer.WriteByte(one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _registry.Changed -= OnRegistryChanged;
        await _stop.CancelAsync();
        if (_accept is not null)
        {
            try { await _accept.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        }
        Client[] clients;
        lock (_lock) clients = _clients.ToArray();
        foreach (var c in clients) await c.DisposeAsync();
        _stop.Dispose();
    }

    private sealed class Client(NamedPipeServerStream stream) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public NamedPipeServerStream Stream { get; } = stream;

        public async Task SendAsync(HandsPipeMessage message, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, HandsPipe.Json) + "\n");
            await _write.WaitAsync(ct);
            try
            {
                await Stream.WriteAsync(bytes, ct);
                await Stream.FlushAsync(ct);
            }
            finally
            {
                _write.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); }
            catch (IOException) { }
        }
    }
}
