using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

// Среда «устройство пользователя» (ADR-016 §3, задача 2.3): процесс исполняется на машине
// владельца через канал устройства (шов IDeviceExecChannel, реализует Desktop). По образцу
// docker-раннера: на сервере живёт процесс-ретранслятор stdio (exec-relay.mjs) — для
// ClaudeSession это обычный Process, его stdin/stdout/stderr и код выхода — это stdio CLI
// на устройстве. Ретранслятор ходит только на loopback-порт этого раннера, раннер — в канал.
//
// Граница учётных данных: spec для устройства собирается по allow-list. Env — только
// поведенческие ключи из AllowedEnvKeys (ключи провайдеров, токены подписки и прочее
// из BuildCliEnv/BuildOAuthCliEnv не едут никогда: маршрут и авторизацию ставит шлюз через
// сайдкар агента). MCP-конфиг переписывается: остаются только http-серверы нашего бэкенда,
// адрес — на сайдкар, заголовков нет вовсе; stdio и сторонние серверы отбрасываются.
//
// Маршрут и токен хода выдаёт шлюз (шов IDeviceTurnGateway) ДО открытия канала: отказ —
// ход не стартует на устройстве вовсе. Токен едет только полем Gateway кадра spawn, мимо
// spec, и живёт столько же, сколько процесс CLI: отзыв — по его выходу или kill (конец
// исполнения) и по сбою запуска, но не по концу хода — процесс обслуживает много ходов.
public sealed class RemoteProcessRunner : IProcessLauncher
{
    /// <summary>Ключи env, которые допускается передать CLI на устройстве. Остальное отбрасывается.</summary>
    public static readonly IReadOnlySet<string> AllowedEnvKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // BareMode: отключение автозагрузки CLAUDE.md — поведение, а не маршрут
        "CLAUDE_CODE_DISABLE_CLAUDE_MDS",
    };

    // Флаги CLI, значение которых — путь к файлу на сервере: файл едет в spawn и
    // материализуется на устройстве
    internal static readonly IReadOnlySet<string> FileArgFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--mcp-config",
        "--system-prompt-file",
        "--append-system-prompt-file",
    };

    // Серверы нашего MCP-over-HTTP: только их адреса переводятся на сайдкар
    private static readonly IReadOnlySet<string> BackendMcpServers = new HashSet<string>(StringComparer.Ordinal)
    {
        McpEndpoints.TasksName, McpEndpoints.NotesName, McpEndpoints.MemoryName,
        McpEndpoints.PersonasName, McpEndpoints.NotificationsName, McpEndpoints.WatchName,
        McpEndpoints.WebSearchName, McpEndpoints.CodeGraphName, McpEndpoints.DifyName,
        McpEndpoints.HiggsfieldName, McpEndpoints.WidgetsName, McpEndpoints.WorkspaceName,
    };

    // Живые исполнения по «владелец/ход»: Kill обязан найти ход, даже если его зовут
    // с другого экземпляра раннера того же владельца
    private static readonly ConcurrentDictionary<string, RemoteExec> Execs = new();

    private readonly IDeviceExecChannel _channel;
    private readonly IDeviceTurnGateway _gateway;
    private readonly string _ownerId;
    private readonly string _deviceId;
    private readonly string _relayScript;
    private readonly string _nodePath;

    public RemoteProcessRunner(IDeviceExecChannel channel, IDeviceTurnGateway gateway, string ownerId, string deviceId,
        string? relayScriptPath = null, string? nodePath = null)
    {
        _channel = channel;
        _gateway = gateway;
        _ownerId = ownerId;
        _deviceId = deviceId;
        _relayScript = relayScriptPath ?? Path.Combine(AppContext.BaseDirectory, "exec-relay.mjs");
        _nodePath = nodePath ?? ResolveNode();
    }

    public string DeviceId => _deviceId;

    // Процесс исполняется не на хосте и окружение сервера не наследует — как у песочницы
    public bool IsSandboxed => true;
    public bool TargetIsWindows =>
        _channel.GetStatus(_ownerId, _deviceId)?.Platform?.StartsWith("win", StringComparison.OrdinalIgnoreCase) == true;
    // Путь проекта локального проекта — уже путь устройства
    public IPathMapper Paths => IdentityPathMapper.Instance;
    // Агент запускает свою управляемую копию CLI по этому имени
    public string ClaudeCliCommand => DeviceExecCli.Name;
    public string? McpApiUrlOverride => null;

    public string HostTempDir
    {
        get
        {
            // Серверный temp: отсюда раннер читает файлы spec и везёт их содержимое на устройство
            var dir = Path.Combine(Path.GetTempPath(), "ccs-remote", _ownerId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public Process Start(ProcessSpec spec)
    {
        if (spec.RawArguments is not null)
            throw new NotSupportedException("RawArguments — только local-раннер (cmd /s /c); на устройство едет только Args");

        var turnId = spec.TurnId ?? NewTurnId();
        var spawn = BuildSpawn(spec);
        // Отказ шлюза — DeviceExecRefusedException с его текстом, до канала и ретранслятора
        var gateway = StartGatewayTurn(spec);
        IDeviceExecStream? stream = null;
        try
        {
            var control = DeviceExecJson.Serialize(
                new DeviceExecControl(DeviceExecControlOps.Spawn, turnId, spawn, gateway));
            if (control.Length > DeviceExecProtocol.MaxPayloadBytes)
                throw new InvalidOperationException(
                    $"Запуск на устройстве не помещается в кадр канала: {control.Length} байт при потолке {DeviceExecProtocol.MaxPayloadBytes}");

            // Отказ неготового устройства — DeviceExecRefusedException с причиной, до старта ретранслятора
            stream = _channel.OpenAsync(_ownerId, _deviceId).GetAwaiter().GetResult();
            stream.SendAsync(DeviceExecFrameChannel.Control, control).AsTask().GetAwaiter().GetResult();
            // Отказ агента по кадру spawn (папка вне разрешённых корней, нет копии CLI) — тоже
            // DeviceExecRefusedException с его причиной: иначе человек увидит «процесс упал»
            stream = AwaitAgentVerdictAsync(stream).GetAwaiter().GetResult();
            var exec = RemoteExec.Launch(ExecKey(turnId), turnId, stream, spec, _nodePath, _relayScript);
            Execs[exec.Key] = exec;
            exec.Run(() =>
            {
                Execs.TryRemove(new KeyValuePair<string, RemoteExec>(exec.Key, exec));
                _gateway.EndTurn(gateway.TurnId);
            });
            if (spec.Track) ProcessRegistry.Register(exec.Relay);
            return exec.Relay;
        }
        catch
        {
            _gateway.EndTurn(gateway.TurnId);
            if (stream is not null) _ = stream.DisposeAsync().AsTask();
            throw;
        }
    }

    private static readonly TimeSpan VerdictTimeout = TimeSpan.FromSeconds(15);

    // Первый кадр агента после spawn — Info (ход запущен) либо stderr с Exit и Error (отказ).
    // Прочитанное до вердикта не теряется: его первым отдаёт обёртка потока. Нет ответа за
    // потолок — решает дальше ретранслятор, как до этой проверки.
    private static async Task<IDeviceExecStream> AwaitAgentVerdictAsync(IDeviceExecStream stream)
    {
        var head = new List<DeviceExecFrame>();
        using var cts = new CancellationTokenSource(VerdictTimeout);
        try
        {
            await foreach (var frame in stream.ReadAllAsync(cts.Token))
            {
                head.Add(frame);
                if (frame.Channel == DeviceExecFrameChannel.Stderr) continue;
                if (frame.Channel == DeviceExecFrameChannel.Exit && ExitError(frame) is { } error)
                    throw new DeviceExecRefusedException(DeviceExecRefusal.AgentRefused, error);
                break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        return head.Count == 0 ? stream : new PrefetchedStream(stream, head);
    }

    private static string? ExitError(DeviceExecFrame frame)
    {
        try
        {
            var exit = DeviceExecJson.Deserialize<DeviceExecExit>(frame.Payload.Span);
            return string.IsNullOrWhiteSpace(exit?.Error) ? null : exit.Error;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private sealed class PrefetchedStream(IDeviceExecStream inner, IReadOnlyList<DeviceExecFrame> head) : IDeviceExecStream
    {
        private IReadOnlyList<DeviceExecFrame>? _head = head;

        public string ExecId => inner.ExecId;

        public ValueTask SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
            inner.SendAsync(channel, payload, ct);

        public async IAsyncEnumerable<DeviceExecFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var frame in Interlocked.Exchange(ref _head, null) ?? []) yield return frame;
            await foreach (var frame in inner.ReadAllAsync(ct)) yield return frame;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Маршрут и токен хода — до запуска на устройстве. Модель — подсказка из --model: провайдера
    // и итоговую модель решает шлюз
    private DeviceExecGateway StartGatewayTurn(ProcessSpec spec)
    {
        if (string.IsNullOrEmpty(spec.SessionId))
            throw new DeviceExecRefusedException(DeviceExecRefusal.GatewayRefused,
                "Ход на устройстве без чата: токен шлюза не к чему привязать.");
        var start = _gateway.StartTurn(_ownerId, spec.SessionId, _deviceId, ModelOf(spec.Args));
        return start.Gateway
            ?? throw new DeviceExecRefusedException(DeviceExecRefusal.GatewayRefused,
                start.FailureText ?? "Шлюз не выдал ходу маршрут.");
    }

    internal static string? ModelOf(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--model" && i + 1 < args.Count) return args[i + 1];
            if (args[i].StartsWith("--model=", StringComparison.Ordinal)) return args[i]["--model=".Length..];
        }
        return null;
    }

    public void Kill(Process process, string? turnId = null)
    {
        // Сначала группа процессов хода на устройстве: убийство ретранслятора её не трогает
        RemoteExec? exec = null;
        if (turnId is not null) Execs.TryGetValue(ExecKey(turnId), out exec);
        exec ??= Execs.Values.FirstOrDefault(e => ReferenceEquals(e.Relay, process));
        exec?.KillRemote();

        // Затем ретранслятор (освобождает пайпы)
        try { process.Kill(entireProcessTree: true); }
        catch { /* процесс уже завершился */ }
    }

    // Командная строка собирается на устройстве: claude + args, пути файлов spec там той же
    // природы, что и серверные. Обвязка ретранслятора на сервере в неё не входит.
    public int EstimateCommandLineLength(ProcessSpec spec)
    {
        if (spec.RawArguments is not null)
            throw new NotSupportedException("RawArguments — только local-раннер (cmd /s /c); на устройство едет только Args");
        var total = CmdlineEstimate.ArgCost(spec.FileName);
        foreach (var a in spec.Args) total += CmdlineEstimate.ArgCost(a);
        return total;
    }

    internal static string NewTurnId() => Guid.NewGuid().ToString("N")[..12];

    private string ExecKey(string turnId) => _ownerId + "/" + turnId;

    // ---------- сборка spawn по allow-list ----------

    /// <summary>
    /// Что поедет на устройство. Единственная точка сборки: env — только
    /// <see cref="AllowedEnvKeys"/>, файлы spec — содержимым (MCP-конфиг — переписанным).
    /// </summary>
    internal static DeviceExecSpawn BuildSpawn(ProcessSpec spec)
    {
        var files = new List<DeviceExecFile>();
        var args = new List<string>(spec.Args.Count);
        for (var i = 0; i < spec.Args.Count; i++)
        {
            var a = spec.Args[i];
            // Рантайм-настройки несут env и помощников ключа (apiKeyHelper) — на устройство
            // их не везём; раннер объявлен песочным, и ClaudeRuntimeSettings их не собирает
            if (a == "--settings" || a.StartsWith("--settings=", StringComparison.Ordinal))
                throw new NotSupportedException("--settings на устройство не передаётся");

            args.Add(a);
            if (!FileArgFlags.Contains(a) || i + 1 >= spec.Args.Count) continue;

            var path = spec.Args[++i];
            var id = "f" + (files.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            files.Add(ReadSpecFile(a, path, id));
            args.Add(DeviceExecPlaceholders.File(id));
        }

        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>())
            if (AllowedEnvKeys.Contains(k)) env[k] = v;

        return new DeviceExecSpawn(spec.FileName, args, spec.WorkingDirectory, env, files, spec.RedirectStdin);
    }

    private static DeviceExecFile ReadSpecFile(string flag, string path, string id)
    {
        // Значение флага — не файл (например, JSON строкой): не знаем, что внутри, — не везём
        if (!File.Exists(path))
            throw new InvalidOperationException($"Файл для {flag} не найден на сервере: {path}");
        var content = File.ReadAllText(path);
        if (flag == "--mcp-config") content = SanitizeMcpConfig(content);
        return new DeviceExecFile(id, Path.GetFileName(path), content);
    }

    /// <summary>
    /// MCP-конфиг для устройства: только http-серверы нашего бэкенда, адрес
    /// <c>{сайдкар}/mcp/{имя}/{хвост}</c>, никаких заголовков и env. Авторизацию и
    /// <c>X-Caller-Session-Id</c> ставит шлюз по привязке токена хода.
    /// </summary>
    internal static string SanitizeMcpConfig(string json)
    {
        var servers = new JsonObject();
        if (JsonNode.Parse(json) is JsonObject { } root && root["mcpServers"] is JsonObject src)
        {
            foreach (var (name, node) in src)
            {
                if (node is not JsonObject server) continue;
                var type = (server["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
                var url = (server["url"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u : null;
                if (type is not ("http" or "sse") || url is null) continue;
                if (TryRewriteBackendUrl(url) is not { } rewritten) continue;
                servers[name] = new JsonObject { ["type"] = type, ["url"] = rewritten };
            }
        }
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    private static string? TryRewriteBackendUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var path = uri.AbsolutePath;
        var idx = path.IndexOf("/mcp/", StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = path[(idx + "/mcp/".Length)..];
        var server = tail.Split('/')[0];
        if (!BackendMcpServers.Contains(server)) return null;
        // Строка запроса отбрасывается: у наших эндпоинтов её нет, а токен в ней был бы утечкой
        return $"{DeviceExecPlaceholders.Sidecar}/{DeviceSidecarRoutes.Mcp}/{tail}";
    }

    private static string ResolveNode()
    {
        if (OperatingSystem.IsWindows()) return ExecutableResolver.ResolveExecutable("node");
        return ExecutableResolver.FindInPath("node", Environment.GetEnvironmentVariable("PATH"), null) ?? "node";
    }

    // ---------- одно исполнение: ретранслятор ↔ loopback ↔ канал устройства ----------

    private sealed class RemoteExec
    {
        private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan KillSendTimeout = TimeSpan.FromSeconds(5);
        private const int MaxKeyFrameBytes = 256;

        private readonly IDeviceExecStream _stream;
        private readonly TcpListener _listener;
        private readonly byte[] _relayKey;
        private int _killSent;
        private volatile bool _exited;

        public string Key { get; }
        public string TurnId { get; }
        public Process Relay { get; }

        private RemoteExec(string key, string turnId, IDeviceExecStream stream, TcpListener listener, byte[] relayKey, Process relay)
        {
            Key = key;
            TurnId = turnId;
            _stream = stream;
            _listener = listener;
            _relayKey = relayKey;
            Relay = relay;
        }

        public static RemoteExec Launch(string key, string turnId, IDeviceExecStream stream, ProcessSpec spec,
            string nodePath, string relayScript)
        {
            if (!File.Exists(relayScript))
                throw new InvalidOperationException($"Не найден ретранслятор удалённого исполнения: {relayScript}");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                // Ключ подключения: порт loopback виден любому процессу машины, ретранслятор
                // обязан доказать, что он наш. Через env, а не argv: argv видят все пользователи
                var relayKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

                var psi = new ProcessStartInfo
                {
                    FileName = nodePath,
                    UseShellExecute = false,
                    RedirectStandardInput = spec.RedirectStdin,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (spec.StdioEncoding is { } enc)
                {
                    psi.StandardOutputEncoding = enc;
                    psi.StandardErrorEncoding = enc;
                    if (spec.RedirectStdin) psi.StandardInputEncoding = enc;
                }
                psi.ArgumentList.Add(relayScript);
                psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // Окружение ретранслятора — с нуля: окружение бэкенда (токены, ключи) ему не нужно
                psi.Environment.Clear();
                foreach (var name in new[] { "PATH", "SystemRoot", "SYSTEMROOT", "windir" })
                    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) psi.Environment[name] = v;
                psi.Environment["CCS_EXEC_RELAY_KEY"] = relayKey;

                var relay = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
                if (!relay.Start())
                    throw new InvalidOperationException("Не удалось запустить ретранслятор удалённого исполнения");
                return new RemoteExec(key, turnId, stream, listener, Encoding.ASCII.GetBytes(relayKey), relay);
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }

        public void Run(Action onDone) => _ = Task.Run(async () =>
        {
            try { await RunAsync(); }
            catch { /* сбой моста: ретранслятор всё равно добивается ниже */ }
            finally
            {
                onDone();
                try { _listener.Stop(); } catch { }
                try { await _stream.DisposeAsync(); } catch { }
            }
        });

        // Убить ход на устройстве (однократно). Синхронно с потолком: Kill зовут и из
        // синхронных веток ClaudeSession
        public void KillRemote()
        {
            if (Interlocked.Exchange(ref _killSent, 1) != 0 || _exited) return;
            try
            {
                var payload = DeviceExecJson.Serialize(new DeviceExecControl(DeviceExecControlOps.Kill, TurnId));
                _stream.SendAsync(DeviceExecFrameChannel.Control, payload).AsTask().Wait(KillSendTimeout);
            }
            catch { /* канал уже закрыт */ }
        }

        private async Task RunAsync()
        {
            using var client = await AcceptRelayAsync();
            if (client is null)
            {
                KillRemote();
                try { Relay.Kill(entireProcessTree: true); } catch { }
                return;
            }

            var net = client.GetStream();
            using var cts = new CancellationTokenSource();
            var down = PumpDownAsync(client, net, cts.Token);
            var up = PumpUpAsync(net, cts.Token);
            var first = await Task.WhenAny(down, up);

            if (first == up && !_exited)
            {
                // Ретранслятор ушёл без кода выхода с устройства (убит или упал) —
                // ход на устройстве сиротой не оставляем
                KillRemote();
                cts.Cancel();
            }
            try { await Task.WhenAll(down, up).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { /* отмена/обрыв — штатный конец моста */ }
        }

        private async Task<TcpClient?> AcceptRelayAsync()
        {
            using var cts = new CancellationTokenSource(AcceptTimeout);
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }

                try
                {
                    var frame = await ReadFrameAsync(client.GetStream(), MaxKeyFrameBytes, cts.Token);
                    if (frame is { Channel: DeviceExecFrameChannel.Control } f
                        && CryptographicOperations.FixedTimeEquals(f.Payload.Span, _relayKey))
                        return client;
                }
                catch (Exception) when (!cts.IsCancellationRequested) { }
                catch (OperationCanceledException) { }
                client.Dispose();
            }
            return null;
        }

        // Канал устройства → ретранслятор: stdout, stderr, код выхода
        private async Task PumpDownAsync(TcpClient client, NetworkStream net, CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _stream.ReadAllAsync(ct))
                {
                    switch (frame.Channel)
                    {
                        case DeviceExecFrameChannel.Stdout:
                        case DeviceExecFrameChannel.Stderr:
                            await net.WriteAsync(DeviceExecFrames.Encode(frame.Channel, 0, frame.Payload.Span), ct);
                            break;
                        case DeviceExecFrameChannel.Exit:
                            _exited = true;
                            await net.WriteAsync(DeviceExecFrames.Encode(frame.Channel, 0, frame.Payload.Span), ct);
                            return;
                    }
                }
                // Поток закрылся без кода выхода — ретранслятор выйдет с 1
                var note = Encoding.UTF8.GetBytes("[remote] канал исполнения закрыт до завершения процесса на устройстве\n");
                await net.WriteAsync(DeviceExecFrames.Encode(DeviceExecFrameChannel.Stderr, 0, note), ct);
            }
            finally
            {
                // Конец вывода: ретранслятор допишет своё и выйдет с полученным кодом
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }

        // Ретранслятор → канал устройства: stdin и его конец
        private async Task PumpUpAsync(NetworkStream net, CancellationToken ct)
        {
            while (true)
            {
                var frame = await ReadFrameAsync(net, DeviceExecProtocol.MaxPayloadBytes, ct);
                if (frame is not { } f) return;
                if (f.Channel is DeviceExecFrameChannel.Stdin or DeviceExecFrameChannel.StdinEof)
                    await _stream.SendAsync(f.Channel, f.Payload, ct);
            }
        }

        // Ровно один кадр из TCP; null — соединение закрыто на границе кадра
        private static async Task<DeviceExecFrame?> ReadFrameAsync(Stream net, int maxPayload, CancellationToken ct)
        {
            var header = new byte[DeviceExecProtocol.HeaderBytes];
            var read = await net.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (read == 0) return null;
            if (read < header.Length) throw new EndOfStreamException("Обрыв кадра ретранслятора");

            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(9, 4));
            if (length > maxPayload) throw new InvalidDataException("Кадр ретранслятора длиннее потолка");
            var message = new byte[header.Length + (int)length];
            header.CopyTo(message, 0);
            await net.ReadExactlyAsync(message.AsMemory(header.Length), ct);
            if (!DeviceExecFrames.TryDecode(message, out var frame))
                throw new InvalidDataException("Битый кадр ретранслятора");
            return frame;
        }
    }
}
