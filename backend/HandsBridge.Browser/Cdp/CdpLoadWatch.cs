using System.Diagnostics;
using System.Threading.Channels;

namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>Докуда дошла загрузка документа.</summary>
public enum CdpLoadState
{
    /// <summary>DOMContentLoaded не пришёл за отведённое время.</summary>
    NotLoaded,

    /// <summary>DOM готов, но затишья (load или почти пустая сеть) за короткое окно не дождались.</summary>
    DomReady,

    /// <summary>DOM готов и страница затихла: load, почти пустая сеть или конец загрузки фрейма.</summary>
    Settled,
}

/// <summary>
/// Наблюдение за загрузкой фрейма по событиям вкладки. Ставится ДО команды, которая вызывает
/// переход (<c>Page.navigate</c>, клик, Enter): события копятся в очереди с момента создания,
/// поэтому быстрый DOMContentLoaded, пришедший раньше ответа на команду, не теряется.
/// <para>
/// Ждём не <c>load</c> (реклама и счётчики держат его секундами), а DOMContentLoaded документа
/// плюс короткое затишье: <c>load</c> или <c>networkAlmostIdle</c> того же документа, что раньше.
/// </para>
/// Обрыв соединения завершает ожидание <see cref="CdpDisconnectedException"/>, а не таймаутом.
/// </summary>
public sealed class CdpLoadWatch : IDisposable
{
    private static readonly string[] Methods =
    [
        "Page.lifecycleEvent", "Page.frameRequestedNavigation", "Page.frameStartedLoading",
        "Page.frameStoppedLoading", "Page.navigatedWithinDocument",
    ];

    private readonly CdpConnection _connection;
    private readonly Channel<CdpEvent> _events = Channel.CreateUnbounded<CdpEvent>();
    private readonly List<IDisposable> _subscriptions = [];

    internal CdpLoadWatch(CdpConnection connection, string sessionId)
    {
        _connection = connection;
        foreach (var method in Methods)
            _subscriptions.Add(connection.Subscribe(method, sessionId, e => _events.Writer.TryWrite(e)));
    }

    /// <summary>
    /// Начался ли переход фрейма за окно <paramref name="window"/>: запрос перехода страницей,
    /// начало загрузки или новый документ. Переход внутри документа (якорь, <c>pushState</c>)
    /// загрузки не даёт — false.
    /// </summary>
    public async Task<bool> WaitForNavigationStartAsync(string frameId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var deadline = started + Ticks(window);
            while (await NextAsync(deadline, cancellationToken) is { } e)
            {
                if (FrameOf(e) != frameId)
                    continue;
                switch (e.Method)
                {
                    case "Page.frameRequestedNavigation" or "Page.frameStartedLoading":
                        return true;
                    case "Page.lifecycleEvent" when CdpBrowser.Str(e.Params, "name") == "init":
                        return true;
                    case "Page.navigatedWithinDocument":
                        return false;
                }
            }
            return false;
        }
        finally
        {
            CdpMeter.AddWait(Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Дождаться DOMContentLoaded документа не дольше <paramref name="loadTimeout"/>, затем
    /// затишья не дольше <paramref name="settle"/> (потолок <paramref name="loadTimeout"/> при
    /// этом не растёт).
    /// </summary>
    /// <param name="loaderId">
    /// Загрузчик документа из ответа <c>Page.navigate</c>. null — переход начала страница (клик,
    /// Enter) и загрузчик неизвестен: берётся первый DOMContentLoaded фрейма, а конец загрузки
    /// без него значит, что нового документа не будет (переход отменён, ответ 204).
    /// </param>
    public async Task<CdpLoadState> WaitForLoadAsync(string frameId, string? loaderId, TimeSpan loadTimeout, TimeSpan settle,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var ceiling = started + Ticks(loadTimeout);
        var deadline = ceiling;
        var loader = loaderId;
        long? domReadyAt = null;
        // Чем кончилось ожидание — для этапов hands.log
        string? outcome = null;
        try
        {
            while (await NextAsync(deadline, cancellationToken) is { } e)
            {
                if (FrameOf(e) != frameId)
                    continue;

                switch (e.Method)
                {
                    case "Page.lifecycleEvent":
                    {
                        var name = CdpBrowser.Str(e.Params, "name");
                        var eventLoader = CdpBrowser.Str(e.Params, "loaderId");
                        if (loader is not null && eventLoader != loader)
                            continue;
                        if (name == "load" || (name == "networkAlmostIdle" && domReadyAt is not null))
                        {
                            outcome = name;
                            return CdpLoadState.Settled;
                        }
                        if (name == "DOMContentLoaded" && domReadyAt is null)
                        {
                            domReadyAt = Stopwatch.GetTimestamp();
                            loader ??= eventLoader;
                            deadline = Math.Min(ceiling, domReadyAt.Value + Ticks(settle));
                        }
                        break;
                    }
                    // С известным загрузчиком конец загрузки до DOMContentLoaded мог прийти от
                    // прежнего документа — верим ему только после готовности своего
                    case "Page.frameStoppedLoading" when domReadyAt is not null:
                        outcome = "конец загрузки";
                        return CdpLoadState.Settled;
                    case "Page.frameStoppedLoading" when loaderId is null:
                        outcome = "переход отменён";
                        return CdpLoadState.NotLoaded;
                    case "Page.navigatedWithinDocument" when loaderId is null:
                        outcome = "внутри документа";
                        return CdpLoadState.Settled;
                }
            }
            // Короткое окно затишья и потолок загрузки — разные беды: реклама держит load или
            // вся загрузка упёрлась в потолок
            outcome = deadline < ceiling ? "затишье истекло" : "потолок загрузки";
            return domReadyAt is not null ? CdpLoadState.DomReady : CdpLoadState.NotLoaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "отмена";
            throw;
        }
        catch (CdpDisconnectedException)
        {
            outcome = "обрыв соединения";
            throw;
        }
        finally
        {
            var now = Stopwatch.GetTimestamp();
            CdpMeter.AddWait(Stopwatch.GetElapsedTime(started, now));
            // load до DOMContentLoaded не приходит, поэтому без готовности DOM затишья нет вовсе
            if (domReadyAt is { } ready)
            {
                CdpMeter.AddStage("DOMContentLoaded", Stopwatch.GetElapsedTime(started, ready));
                CdpMeter.AddStage("затишье", Stopwatch.GetElapsedTime(ready, now), outcome);
            }
            else
            {
                CdpMeter.AddStage("DOMContentLoaded", Stopwatch.GetElapsedTime(started, now),
                    outcome == "потолок загрузки" ? "не пришёл" : outcome);
            }
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _events.Writer.TryComplete();
    }

    /// <summary>Следующее событие до срока; null — срок вышел.</summary>
    private async Task<CdpEvent?> NextAsync(long deadline, CancellationToken cancellationToken)
    {
        var left = deadline - Stopwatch.GetTimestamp();
        if (_events.Reader.TryRead(out var ready))
            return ready;
        if (left <= 0)
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromTicks(left * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        var read = _events.Reader.ReadAsync(cts.Token).AsTask();
        try
        {
            if (await Task.WhenAny(read, _connection.Closed) != read)
                throw new CdpDisconnectedException(await _connection.Closed);
            return await read;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private static string FrameOf(CdpEvent e) => CdpBrowser.Str(e.Params, "frameId");

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
