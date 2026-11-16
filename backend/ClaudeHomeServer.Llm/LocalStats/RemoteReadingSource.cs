namespace ClaudeHomeServer.Services.Llm;

// Удалённый узел (агент узла фермы): GET {base}/v1/slice по образцу VllmMetricsSource —
// HTTP к недоверенному источнику. Тайм-аут 1,5 с (ADR-029 «Цикл»: меньше периода опроса 2 с),
// часовой — через TimeProvider (тесты гоняют тайм-аут на фейковых часах, без Task.Delay).
// Тело читается потоком не больше 256 КБ + 1 байт: лишний байт ловит переход потолка без
// буферизации всего ответа; дальше решает NodeSliceParser (потолки — его, здесь только чтение).
// Любой сбой (не-2xx, сеть, тайм-аут, кривое тело) — статус; исключение наружу не выходит
// (отмена ct вызывающего — исключение по контракту, как у VllmMetricsSource).
public sealed class RemoteReadingSource : INodeReadingSource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1_500);

    private readonly HttpClient _http;
    private readonly Uri _sliceUrl;
    private readonly TimeProvider _time;

    public RemoteReadingSource(HttpClient http, Uri baseUri, TimeProvider time)
    {
        _http = http;
        // Базовый URL без пути (HardwareNodesOptions.IsAgentUrl); дописываем /v1/slice.
        var path = baseUri.AbsolutePath is "" or "/" ? "/v1/slice" : baseUri.AbsolutePath.TrimEnd('/') + "/v1/slice";
        _sliceUrl = new UriBuilder(baseUri) { Path = path }.Uri;
        _time = time;
    }

    public async Task<NodeReading> ReadAsync(CancellationToken ct = default)
    {
        // Связанный CTS: отмена вызывающего или тайм-аут — один токен; ответ после
        // тайм-аута отбрасывается отменой, вызов не ждёт сеть. Таймер — через TimeProvider
        // (тесты гоняют срабатывание на фейковых часах, без реальных 1,5 с и Task.Delay).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = _time.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), cts, RequestTimeout, Timeout.InfiniteTimeSpan);

        byte[] body;
        try
        {
            using var resp = await _http.GetAsync(_sliceUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return NodeReading.Down(); // 503 «среза ещё нет», 5xx — узел down.
            body = await ReadBodyCappedAsync(resp.Content, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // отмена вызывающего — не сбой узла.
        }
        catch (TaskCanceledException)
        {
            return NodeReading.Down(); // тайм-аут 1,5 с (часовой или сетевой).
        }
        // Простой OperationCanceledException: так бросит отмену базовый Stream.ReadAsync и любые
        // обёртки чтения тела (GZip/декомпрессия) — токен вызывающего цел, а TaskCanceledException
        // ловит только свой подкласс. Ставится ПОСЛЕ catch (TaskCanceledException): без фильтра он
        // поглотил бы подкласс, следующий clause стал бы недостижимым (CS0160), и мутация
        // «убрать catch TaskCanceledException» перестала бы краснеть.
        catch (OperationCanceledException)
        {
            return NodeReading.Down(); // таймер тайм-аута сработал на чтении тела.
        }
        catch (HttpRequestException)
        {
            return NodeReading.Down(); // сеть/коннект.
        }
        catch (IOException)
        {
            return NodeReading.Down(); // обрыв потока тела.
        }
        // Список закрытый: отмена ct вызывающего — только фильтр-catch выше (тест
        // ReadAsync_CallerCancellation_Propagates), тайм-аут — TaskCanceledException/
        // OperationCanceledException (мутации c2), NodeSliceParser не бросает по контракту
        // (NodeSliceTests), ParseCsv/BuildFiles — чистые.

        var parsed = NodeSliceParser.Parse(body);
        switch (parsed.Status)
        {
            case NodeSliceParseStatus.Incompatible:
                return new NodeReading { Status = NodeReadingStatus.Incompatible };
            case NodeSliceParseStatus.Ok:
                var s = parsed.Slice!;
                // Порог свежести — тот же, что у локального status:"stale" (ADR-029, п.3 «Железо»).
                var status = s.AgeMs > HardwareSampler.StaleAfterMs ? NodeReadingStatus.Stale : NodeReadingStatus.Ok;
                return new NodeReading
                {
                    Status = status,
                    Files = BuildFiles(s),
                    Gpus = GpuSampler.ParseCsv(s.GpuCsv),
                    Gddr6 = s.Gddr6,
                    AgeMs = s.AgeMs,
                };
            default:
                return NodeReading.Down(); // Invalid: не-JSON/не объект/нет обязательных полей.
        }
    }

    // Файлы/ссылки/statvfs среза → та же форма, что у DiskFileSource (HwmonSliceParityTests).
    private static SliceFileSource BuildFiles(NodeSlice s)
    {
        var statvfs = new Dictionary<string, (long total, long free)>(StringComparer.Ordinal);
        foreach (var v in s.Statvfs)
            statvfs[v.Mount] = (v.Total, v.Free);
        return new SliceFileSource(s.Files, s.Links, statvfs);
    }

    // Потоковое чтение тела с потолком MaxBodyBytes+1: прочитано ровно столько, чтобы
    // различить «в потолке»/«за потолком»; весь ответ в память не буферизуем.
    private static async Task<byte[]> ReadBodyCappedAsync(HttpContent content, CancellationToken ct)
    {
        const int cap = NodeSliceLimits.MaxBodyBytes + 1;
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[cap];
        var total = 0;
        while (total < cap)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, cap - total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total == cap ? buffer : buffer[..total];
    }
}
