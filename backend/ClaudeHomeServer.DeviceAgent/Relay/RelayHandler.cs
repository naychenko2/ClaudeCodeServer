using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Relay;

/// <summary>Ответ операции: заголовок и тело — готовые байты либо открытый поток файла.</summary>
internal sealed record RelayReply(RelayResponseHead Head, ReadOnlyMemory<byte> Body, Stream? Content = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Ретранслятор чтения на устройстве (ADR-016 §5, задача 5.1): исполняет запрос сервера,
/// пришедший по каналу исполнения с назначением <see cref="DeviceExecPurposes.Relay"/>.
///
/// Только композиция поверх того же <see cref="AgentProjectFiles"/> и <see cref="GitService"/>,
/// что у localhost-API: корни машины, реальный путь, сверка дескриптора и потолки — их
/// проверки, своего чтения файлов здесь нет (сторож <c>CompositionOnlyGuardTests</c>).
/// Серверу агент не доверяет (G7): корень проекта из запроса проходит ту же политику, что
/// корень из билета браузера.
///
/// Записи нет по построению: таблица операций — ровно <see cref="RelayOperations.All"/>, и
/// ни одна из них не зовёт пишущих методов (G6); незнакомая операция — отказ.
/// </summary>
internal sealed class RelayHandler
{
    private delegate Task<RelayReply> Operation(RelayRequest request, Project project, CancellationToken ct);

    private const int ChunkBytes = 256 * 1024;

    private readonly AgentProjectFiles _files;
    private readonly GitService _git;
    private readonly ILogger _log;
    private readonly IReadOnlyDictionary<string, Operation> _operations;

    public RelayHandler(AgentProjectFiles files, GitService git, ILogger? log = null)
    {
        _files = files;
        _git = git;
        _log = log ?? NullLogger.Instance;
        _operations = new Dictionary<string, Operation>(StringComparer.Ordinal)
        {
            [RelayOperations.List] = ListAsync,
            [RelayOperations.Read] = ReadAsync,
            [RelayOperations.Stat] = StatAsync,
            [RelayOperations.Search] = SearchAsync,
            [RelayOperations.GitStatus] = GitStatusAsync,
            [RelayOperations.GitDiff] = GitDiffAsync,
            [RelayOperations.GitLog] = GitLogAsync,
            [RelayOperations.GitShow] = GitShowAsync,
        };
    }

    /// <summary>Операции, которые агент исполняет: сторож G6 сверяет их с протоколом.</summary>
    internal IReadOnlyCollection<string> Operations => (IReadOnlyCollection<string>)_operations.Keys;

    /// <summary>Обслуживает один запрос: кадр запроса → заголовок, тело, конец.</summary>
    public async Task RunAsync(ExecLink link, CancellationToken ct)
    {
        await using var owned = link;
        // Сервер закрыл канал (клиент ушёл) — отдачу файла прекращаем
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = link.Finished.ContinueWith(_ =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);

        try
        {
            var request = await ReadRequestAsync(link, cts.Token);
            await using var reply = request is null
                ? Error(StatusCodes.BadRequest, "Нет запроса ретранслятора")
                : await ExecuteAsync(request, cts.Token);
            await SendAsync(link, reply, cts.Token);
            await link.DrainAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            _log.LogWarning(e, "Запрос ретранслятора {ExecId} не обслужен", link.ExecId);
        }
    }

    /// <summary>Исполняет запрос со всеми проверками агента; исключения становятся кодами отказа.</summary>
    public async Task<RelayReply> ExecuteAsync(RelayRequest request, CancellationToken ct)
    {
        if (!_operations.TryGetValue(request.Operation ?? "", out var operation))
            return Error(StatusCodes.BadRequest, "Операция не поддерживается ретранслятором");
        var project = new Project { Id = request.ProjectId, RootPath = request.RootPath };
        try
        {
            return await operation(request, project, ct);
        }
        // Коды — как у localhost-API агента (LocalApi.MapErrors) и серверных контроллеров
        catch (AgentFileTooLargeException e) { return Error(StatusCodes.TooLarge, e.Message); }
        catch (UnauthorizedAccessException e) { return Error(StatusCodes.Forbidden, e.Message); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Error(StatusCodes.NotFound, "Не найдено"); }
        catch (GitCommandException e) { return Error(StatusCodes.Conflict, e.Message); }
        catch (IOException e) { return Error(StatusCodes.Conflict, e.Message); }
    }

    // ---------- операции ----------

    private async Task<RelayReply> ListAsync(RelayRequest r, Project project, CancellationToken ct) =>
        Json(r.Variant == RelayVariants.Tree
            ? await _files.TreeAsync(project, r.Path ?? "", r.ShowHidden, ct)
            : await _files.ListAsync(project, r.Path ?? "", r.ShowHidden, ct));

    private async Task<RelayReply> ReadAsync(RelayRequest r, Project project, CancellationToken ct)
    {
        var path = r.Path ?? "";
        if (r.Variant == RelayVariants.Stream)
        {
            // Поток: в память не читается, потолок — у политики агента (EnsureStreamable)
            var file = await _files.OpenReadAsync(project, path, ct);
            return new RelayReply(new RelayResponseHead(StatusCodes.Ok, FileContentReader.StreamMime(path), file.Length),
                ReadOnlyMemory<byte>.Empty, file.Content);
        }
        if (Directory.Exists(_files.Check(project, path).Full)) return Error(StatusCodes.NotFound, "Не найдено");
        return Json(await _files.GetContentAsync(project, path, ct));
    }

    // Свойства одного файла — строка листинга его папки: своего stat у агента нет
    private async Task<RelayReply> StatAsync(RelayRequest r, Project project, CancellationToken ct)
    {
        _files.Check(project, r.Path ?? "");
        var segments = (r.Path ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return Json(new FileEntry("", "", IsDirectory: true, Size: null, default, IsModified: false));

        var parent = string.Join('/', segments[..^1]);
        var entries = await _files.ListAsync(project, parent, showHidden: true, ct);
        return entries.FirstOrDefault(e => AgentPathPolicy.PathComparer.Equals(e.Name, segments[^1])) is { } entry
            ? Json(entry)
            : Error(StatusCodes.NotFound, "Не найдено");
    }

    // Поиск — на агенте, выдача под политикой корней (AgentProjectFiles.SearchAsync)
    private async Task<RelayReply> SearchAsync(RelayRequest r, Project project, CancellationToken ct) =>
        Json(await _files.SearchAsync(project, r.Query ?? "", ct));

    private async Task<RelayReply> GitStatusAsync(RelayRequest r, Project project, CancellationToken ct) =>
        Json(await _git.StatusAsync(null, RootOf(project), ct));

    private async Task<RelayReply> GitDiffAsync(RelayRequest r, Project project, CancellationToken ct)
    {
        if (r.Variant == RelayVariants.File)
            return Json(new DiffResponse(await _files.GetDiffAsync(project, r.Path ?? "", ct)));
        var root = RootOf(project);
        return Checked(project, r.Path) is not { } path
            ? BadPath()
            : Json(new DiffResponse(await _git.DiffFileAsync(null, root, path, r.Staged, ct)));
    }

    private async Task<RelayReply> GitLogAsync(RelayRequest r, Project project, CancellationToken ct) =>
        Json(await _git.LogAsync(null, RootOf(project), Math.Clamp(r.Limit ?? 100, 1, 1000), r.Branch, ct));

    private async Task<RelayReply> GitShowAsync(RelayRequest r, Project project, CancellationToken ct)
    {
        var root = RootOf(project);
        var sha = r.Sha ?? "";
        if (r.Variant is null)
            return await _git.CommitDetailAsync(null, root, sha, ct) is { } detail
                ? Json(detail)
                : Error(StatusCodes.NotFound, "Коммит не найден");

        if (Checked(project, r.Path) is not { } path) return BadPath();
        return r.Variant switch
        {
            RelayVariants.CommitDiff => Json(new DiffResponse(await _git.CommitFileDiffAsync(null, root, sha, path, ct))),
            RelayVariants.CommitFile => Json(new CommitFileResponse(await _git.FileAtCommitAsync(null, root, sha, path, ct))),
            _ => Error(StatusCodes.BadRequest, "Операция не поддерживается ретранслятором"),
        };
    }

    // Реальный корень проекта под корнями машины — или отказ политики (403, как у localhost-API)
    private string RootOf(Project project) => _files.Check(project, "").Root;

    // Путь внутри репозитория проходит ту же политику, что файлы: git сам ссылки не раскрывает.
    // Отказ по пути у git-маршрутов сервера — 400 «Недопустимый путь», а не 403 файлов
    private string? Checked(Project project, string? path)
    {
        try
        {
            _files.Check(project, path ?? "");
            return path ?? "";
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static RelayReply BadPath() => Error(StatusCodes.BadRequest, "Недопустимый путь");

    // ---------- ответ ----------

    private static RelayReply Json<T>(T value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, RelayProtocol.Json);
        if (body.LongLength > RelayProtocol.MaxJsonBytes)
            return Error(StatusCodes.TooLarge, $"Ответ больше потолка ретранслятора {RelayProtocol.MaxJsonBytes} байт");
        return new RelayReply(new RelayResponseHead(StatusCodes.Ok, JsonType, body.LongLength), body);
    }

    private static RelayReply Error(int status, string message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = message }, RelayProtocol.Json);
        return new RelayReply(new RelayResponseHead(status, JsonType, body.LongLength), body);
    }

    private const string JsonType = "application/json; charset=utf-8";

    private static async Task<RelayRequest?> ReadRequestAsync(ExecLink link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RelayProtocol.RequestTimeout);
        try
        {
            await foreach (var frame in link.ReadAllAsync(timeout.Token))
            {
                if (frame.Channel != DeviceExecFrameChannel.Control) return null;
                try { return JsonSerializer.Deserialize<RelayRequest>(frame.Payload.Span, RelayProtocol.Json); }
                catch (JsonException) { return null; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return null;
    }

    private static async Task SendAsync(ExecLink link, RelayReply reply, CancellationToken ct)
    {
        await link.SendAsync(DeviceExecFrameChannel.Info, JsonSerializer.SerializeToUtf8Bytes(reply.Head, RelayProtocol.Json), ct);
        if (!reply.Body.IsEmpty) await link.SendChunkedAsync(DeviceExecFrameChannel.Stdout, reply.Body, ct);
        if (reply.Content is { } content)
        {
            // Большой файл — кадрами по мере чтения; окно неподтверждённого держит память агента
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
                await link.SendAsync(DeviceExecFrameChannel.Stdout, buffer.AsMemory(0, read), ct);
        }
        await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(0)), ct);
    }

    private static class StatusCodes
    {
        public const int Ok = 200;
        public const int BadRequest = 400;
        public const int Forbidden = 403;
        public const int NotFound = 404;
        public const int Conflict = 409;
        public const int TooLarge = 413;
    }
}
