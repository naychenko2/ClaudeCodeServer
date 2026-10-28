using System.Diagnostics;
using ClaudeHomeServer.Services.MediaHistory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Миниатюры истории (ADR-030 §5): MediaHistoryThumbService.
// - отмена: закрытая вкладка (отменённый CancellationToken) не роняет 500, а возвращает null
//   (фронт рисует плитку). Мутация «убрать catch OperationCanceledException» — краснеет;
// - дефект 8fccc75f: генерация настоящим ffmpeg (временный файл — с расширением, понятным
//   ffmpeg: «.webp.tmp» он не знает, exit 234) и single-flight на одну запись;
// - R-F3-a: падение лидера single-flight исключением — ждущие получают ответ (страховка
//   finally), запись уходит из _inFlight. Падение моделируется швом GenerateOverride:
//   у настоящей генерации путь с исключением наружу не достижим (все отказы перехвачены).
public class MediaHistoryThumbServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "thumb_" + Guid.NewGuid().ToString("N"));

    // Каталог миниатюр — как его считает сам сервис: рядом с DataPath
    private string ThumbsDir => Path.Combine(_tempDir, "data", "local-media-history", "thumbs");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    private MediaHistoryThumbService NewService(ILogger<MediaHistoryThumbService>? log = null)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        return new MediaHistoryThumbService(config, log ?? NullLogger<MediaHistoryThumbService>.Instance);
    }

    // Наличие ffmpeg: как в FfmpegVideoDspTests — на CI он обязан быть, вне CI — явный пропуск
    private static bool HasFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private static void RequireFfmpeg()
    {
        var has = HasFfmpeg();
        if (!has && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("На CI ffmpeg обязан быть установлен");
        Skip.IfNot(has, "ffmpeg не найден на этой машине — проверка на настоящем ffmpeg пропущена");
    }

    // Исходник-картинка через lavfi (тот же приём, что в FfmpegVideoDspTests): красный 64×64
    private string SourcePng()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "src.png");
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1", "-frames:v", "1", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "исходник для теста должен собраться: " + err);
        return path;
    }

    // Счётчик строк лога: сигнал «сколько раз ffmpeg реально завершился»
    private sealed class CountingLog : ILogger<MediaHistoryThumbService>
    {
        private int _ffmpegFailures;
        public int FfmpegFailures => _ffmpegFailures;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // учитываем только строки о фактическом запуске/исходе ffmpeg
            if (formatter(state, exception).Contains("ffmpeg", StringComparison.Ordinal))
                Interlocked.Increment(ref _ffmpegFailures);
        }
    }

    // Шов подмены генерации (R-F3-a): тест держит лидера внутри генерации и роняет его
    // исключением — только так проверяется страховка finally в сервисе (у настоящей генерации
    // путь с исключением наружу не достижим). Первый вызов: (по HoldFirstCall — стоит до
    // Release) → падает. Второй: пишет миниатюру (стаб файла, не настоящий ffmpeg) и успешен.
    // Считает, сколько раз генерацию звали: это признак того, что вызывающий лидировал.
    private sealed class CrashingGeneration
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // true: первый вызов стоит до Release — нужно, чтобы присоединить ждущего до падения
        public bool HoldFirstCall { get; set; }

        public async Task<bool> Invoke(string source, string thumb, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                if (HoldFirstCall)
                {
                    Started.SetResult(); // лидер внутри генерации: запись уже в _inFlight
                    await Release.Task;
                }
                throw new InvalidOperationException("имитация падения лидера: генерация бросает наружу");
            }
            await File.WriteAllTextAsync(thumb, "RIFF"); // стаб миниатюры: важен путь, не содержимое
            return true;
        }
    }

    // Отмена (закрытая вкладка) — null, не 500
    [Fact]
    public async Task Отмена_Null()
    {
        var svc = NewService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = await svc.GetThumbAsync("rec-1", 0, "/tmp/source.png", "image/png", cts.Token);
        res.Should().BeNull();
    }

    // Дефект 8fccc75f (c1): миниатюра создаётся тем же запуском ffmpeg, что в проде
    // (scale=256:-1 → webp). Webp лежит в thumbs под итоговым именем {id}-{n}.webp, и в каталоге
    // не остаётся временного файла. Мутация «писать в .webp.tmp» (старый код): ffmpeg падает с
    // 234 «Unable to choose an output format» → GetThumbAsync возвращает null — тест краснеет
    [Fact]
    public async Task Миниатюра_НастоящийFfmpeg_ФинальныйWebp_БезВременногоФайла()
    {
        RequireFfmpeg();
        var src = SourcePng();
        var svc = NewService();

        var thumb = await svc.GetThumbAsync("rec-1", 0, src, "image/png", CancellationToken.None);

        var path = thumb.Should().NotBeNull("миниатюра должна сгенерироваться настоящим ffmpeg").And.Subject;
        path.Should().Be(Path.Combine(ThumbsDir, "rec-1-0.webp"), "итоговое имя в каталоге thumbs");
        var bytes = File.ReadAllBytes(path);
        bytes.Length.Should().BeGreaterThan(0);
        // подпись webp: RIFF....WEBP
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WEBP");
        // временного файла ({id}-{n}.tmp.webp) не осталось: в каталоге ровно один файл
        Directory.GetFiles(ThumbsDir).Should().Equal(path);
    }

    // Дефект 8fccc75f п.3 (c4): два одновременных запроса одной записи — один запуск ffmpeg.
    // Лидер и ждущий стартуют синхронно (первая точка ожидания лидера — WaitForExitAsync
    // реального процесса), ждущий присоединяется к его итогу до конца первого ffmpeg.
    // Исходник несуществующий → лидер краснеет ровно одной строкой лога о ffmpeg.
    // Мутация «убрать single-flight (_inFlight)»: оба запроса запускают ffmpeg — 2 строки, краснеет
    [Fact]
    public async Task ДваОдновременныхЗапросаОднойЗаписи_ОдинЗапускFfmpeg()
    {
        RequireFfmpeg();
        var log = new CountingLog();
        var svc = NewService(log);
        var missing = Path.Combine(_tempDir, "no-such-source.png");

        var t1 = svc.GetThumbAsync("rec-2", 0, missing, "image/png", CancellationToken.None);
        var t2 = svc.GetThumbAsync("rec-2", 0, missing, "image/png", CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);

        results.Should().AllBeEquivalentTo((string?)null, "исходника нет — миниатюры нет ни у кого");
        log.FfmpegFailures.Should().Be(1,
            "одна запись — один ffmpeg: второй запрос ждёт итог первого, а не запускает свой");
    }

    // R-F3-a (c1): лидер single-flight падает исключением — ждущий, присоединившийся заранее,
    // завершается null (не 500 и не виснет). Порядок жёсткий, без пауз: тело GetThumbAsync лидера
    // доходит до первого await (подменённой генерации) синхронно, поэтому к сигналу Started
    // запись уже в _inFlight; тело GetThumbAsync ждущего доходит до await pending.Task синхронно
    // — он присоединён к итогу лидера до его падения. Отмена запроса ждущего — только страховка
    // от вечного зависания: по правильному коду она не срабатывает.
    // Мутация «убрать tcs.TrySetResult(false) в finally (:102)»: ждущий виснет до страховочной
    // отмены → IsCancellationRequested=true → тест краснеет.
    [Fact]
    public async Task ЛидерПадаетИсключением_ЖдущийЗавершаетсяNull()
    {
        var gen = new CrashingGeneration { HoldFirstCall = true };
        var svc = NewService();
        svc.GenerateOverride = gen.Invoke;
        var missing = Path.Combine(_tempDir, "no-such-source.png");

        var leader = svc.GetThumbAsync("rec-3", 0, missing, "image/png", CancellationToken.None);
        await gen.Started.Task;

        using var waitGuard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiter = svc.GetThumbAsync("rec-3", 0, missing, "image/png", waitGuard.Token);

        gen.Release.SetResult(); // лидер идёт дальше и падает

        var res = await waiter;
        waitGuard.IsCancellationRequested.Should().BeFalse(
            "ответ даёт страховка finally в сервисе, а не отмена запроса: ждущий не виснет");
        res.Should().BeNull("лидер упал — ждущий получает отказ (плитку), не 500");

        await Assert.ThrowsAsync<InvalidOperationException>(() => leader);
    }

    // R-F3-a (c2): после падения лидера запись ушла из _inFlight — следующий запрос той же
    // записи снова лидирует: генерация звалась заново и дошла до результата.
    // Мутация «убрать _inFlight.TryRemove в finally (:101)»: следующий запрос присоединяется к
    // мёртвому tcs (его итог false) → null и ни одного нового вызова генерации → тест краснеет.
    [Fact]
    public async Task ПослеПаденияЛидера_СледующийЗапросТойЖеЗаписиСноваЛидер()
    {
        var gen = new CrashingGeneration();
        var svc = NewService();
        svc.GenerateOverride = gen.Invoke;
        var missing = Path.Combine(_tempDir, "no-such-source.png");
        var thumb = Path.Combine(ThumbsDir, "rec-4-0.webp");

        var crashed = svc.GetThumbAsync("rec-4", 0, missing, "image/png", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashed);
        gen.Calls.Should().Be(1);

        var res = await svc.GetThumbAsync("rec-4", 0, missing, "image/png", CancellationToken.None);

        res.Should().Be(thumb, "запись снова лидировала: её генерация дошла до результата");
        File.Exists(thumb).Should().BeTrue("результат — реальный файл миниатюры");
        gen.Calls.Should().Be(2, "следующий запрос той же записи запустил генерацию сам, а не ждал чужой итог");
    }
}
