using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва IAudioDsp (ADR-021, §2): ffmpeg и ffprobe процессом на хосте. Это первый и единственный
// вызов ffmpeg из бэкенда, поэтому командная строка собирается ТОЛЬКО из типизированных параметров:
// числа — в инвариантной культуре после проверки пределов, имена файлов — наши, во временной папке.
// Демультиплексор задаётся явно по сигнатуре (MediaProbe): иначе плейлист или concat-список в байтах
// заставил бы ffmpeg читать произвольные файлы хоста. Конфиг — секция AudioDsp (пути и таймаут),
// это настройка админа, а не ввод пользователя.
public sealed class FfmpegAudioDsp(IConfiguration config, ILogger<FfmpegAudioDsp> log) : IAudioDsp
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Частота, до которой понижаем звук ради пиков: на точность огибающей не влияет, а объём меньше
    private const int PeaksRate = 8_000;

    private readonly Lazy<bool> _available = new(() => Detect(config, log));

    private string Ffmpeg => config["AudioDsp:FfmpegPath"] is { Length: > 0 } p ? p : "ffmpeg";
    private string Ffprobe => config["AudioDsp:FfprobePath"] is { Length: > 0 } p ? p : "ffprobe";
    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(config.GetValue("AudioDsp:TimeoutSeconds", 120), 5, 3600));

    public bool Available => _available.Value;

    public async Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct)
    {
        if (!Available || Demuxer(audio) is not { } input) return null;
        using var work = WorkDir.Create();
        var path = await work.WriteAsync("in" + input.Extension, audio, ct);
        var run = await RunAsync(Ffprobe, ["-v", "error", "-f", input.Demuxer, "-i", path,
            "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels:format=duration",
            "-of", "json"], ct);
        if (run.Error is not null) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
            var stream = streams[0];
            var rate = int.Parse(Str(stream, "sample_rate") ?? "0", Inv);
            var channels = stream.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
            double? seconds = root.TryGetProperty("format", out var format)
                && double.TryParse(Str(format, "duration"), NumberStyles.Float, Inv, out var d) ? d : null;
            // Нет длительности в контейнере (потоковая запись) — та же оценка по заголовкам, что у local-media
            seconds ??= AudioProbe.Seconds(audio);
            return rate > 0 && channels > 0 && seconds is > 0
                ? new AudioDspInfo(seconds.Value, rate, channels, input.Extension)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            log.LogWarning(ex, "ffprobe вернул неразборчивый ответ");
            return null;
        }
    }

    public async Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct)
    {
        if (!Available) return AudioPeaks.Fail(Unavailable);
        if (points is < 1 or > AudioDspLimits.MaxPeaks)
            return AudioPeaks.Fail($"Число точек волны — от 1 до {AudioDspLimits.MaxPeaks}.");
        if (Demuxer(audio) is not { } input) return AudioPeaks.Fail(NotAudio);
        using var work = WorkDir.Create();
        var path = await work.WriteAsync("in" + input.Extension, audio, ct);
        // Сэмплы моно float32 прямо в stdout: отдельного файла не нужно
        var run = await RunAsync(Ffmpeg, [.. Head, "-f", input.Demuxer, "-i", path,
            "-ac", "1", "-ar", Int(PeaksRate), "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1"], ct);
        if (run.Error is not null) return AudioPeaks.Fail(run.Error);

        var samples = run.Stdout.Length / 4;
        if (samples == 0) return AudioPeaks.Fail("В файле нет звука.");
        var peaks = new float[points];
        var span = run.Stdout.AsSpan();
        for (var i = 0; i < points; i++)
        {
            var from = (int)((long)samples * i / points);
            var to = Math.Max(from + 1, (int)((long)samples * (i + 1) / points));
            var max = 0f;
            for (var s = from; s < to && s < samples; s++)
                max = Math.Max(max, Math.Abs(BitConverter.ToSingle(span.Slice(s * 4, 4))));
            peaks[i] = Math.Min(max, 1f);
        }
        return new AudioPeaks(peaks, samples / (double)PeaksRate, null);
    }

    public async Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct,
        AudioDspInfo? known = null)
    {
        if (!Available) return AudioDspOutput.Fail(Unavailable);
        if (!Finite(edit.StartSeconds) || !Finite(edit.EndSeconds) || !double.IsFinite(edit.FadeInSeconds)
            || !double.IsFinite(edit.FadeOutSeconds) || !double.IsFinite(edit.GainDb))
            return AudioDspOutput.Fail("Параметры правки должны быть числами.");
        if (GainError(edit.GainDb) is { } gainError) return AudioDspOutput.Fail(gainError);
        if (edit.FadeInSeconds < 0 || edit.FadeOutSeconds < 0) return AudioDspOutput.Fail("Длина фейда не может быть отрицательной.");
        if ((known ?? await ProbeAsync(audio, ct)) is not { } info) return AudioDspOutput.Fail(NotAudio);

        var start = edit.StartSeconds ?? 0;
        var end = Math.Min(edit.EndSeconds ?? info.Seconds, info.Seconds);
        if (start < 0 || start >= end) return AudioDspOutput.Fail("Начало куска должно быть раньше его конца и внутри записи.");
        var length = end - start;
        if (edit.FadeInSeconds + edit.FadeOutSeconds > length + 1e-6)
            return AudioDspOutput.Fail("Фейды вместе длиннее куска.");

        var filters = new List<string> { $"atrim=start={Num(start)}:end={Num(end)}", "asetpts=PTS-STARTPTS" };
        if (edit.FadeInSeconds > 0) filters.Add($"afade=t=in:st=0:d={Num(edit.FadeInSeconds)}");
        if (edit.FadeOutSeconds > 0) filters.Add($"afade=t=out:st={Num(length - edit.FadeOutSeconds)}:d={Num(edit.FadeOutSeconds)}");
        if (edit.GainDb != 0) filters.Add($"volume={Num(edit.GainDb)}dB");
        return await EncodeAsync(audio, ["-af", string.Join(',', filters)], edit.Format, ct);
    }

    public async Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs = AudioDspLimits.DefaultLufs,
        AudioFormat format = AudioFormat.Wav, CancellationToken ct = default, AudioDspInfo? known = null)
    {
        if (!Available) return AudioDspOutput.Fail(Unavailable);
        if (!double.IsFinite(targetLufs) || targetLufs is < AudioDspLimits.MinLufs or > AudioDspLimits.MaxLufs)
            return AudioDspOutput.Fail($"Целевая громкость — от {AudioDspLimits.MinLufs} до {AudioDspLimits.MaxLufs} LUFS.");
        if ((known ?? await ProbeAsync(audio, ct)) is not { } info) return AudioDspOutput.Fail(NotAudio);

        var measured = await MeasureLoudnessAsync(audio, $"I={Num(targetLufs)}:TP=-1:LRA=11", ct);
        if (measured.Error is not null) return AudioDspOutput.Fail(measured.Error);
        if (measured.Values is not { } m) return AudioDspOutput.Fail(Silence);
        // loudnorm внутри работает на 192 кГц — возвращаем частоту исходника
        return await EncodeAsync(audio, ["-af", LinearLoudnorm(targetLufs, m), "-ar", Int(info.SampleRate)], format, ct);
    }

    // Второй проход loudnorm по замеру первого — чистое усиление без сжатия динамики
    private static string LinearLoudnorm(double targetLufs, Loudness m)
    {
        // Линейный режим loudnorm молча меняет на динамический (сжатие), если разброс громкости
        // записи шире целевого LRA, — поэтому цель LRA не уже замеренного
        var lra = Math.Clamp(Math.Ceiling(Math.Max(11, m.Lra)), 1, 50);
        // Значения из ответа ffmpeg уже разобраны в числа — обратно в строку идут только они
        return $"loudnorm=I={Num(targetLufs)}:TP=-1:LRA={Num(lra)}:measured_I={Num(m.I)}:measured_TP={Num(m.Tp)}:measured_LRA={Num(m.Lra)}"
            + $":measured_thresh={Num(m.Thresh)}:offset={Num(m.Offset)}:linear=true";
    }

    // Интегральная громкость, LUFS; null — не измерили. Для проверки нормализации
    internal async Task<double?> MeasureLufsAsync(byte[] audio, CancellationToken ct) =>
        (await MeasureLoudnessAsync(audio, "I=-14:TP=-1:LRA=11", ct)).Values?.I;

    public async Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct)
    {
        if (!Available) return AudioDspOutput.Fail(Unavailable);
        if (stems.Count is 0 or > AudioDspLimits.MaxStems)
            return AudioDspOutput.Fail($"Стемов для сведения — от 1 до {AudioDspLimits.MaxStems}.");
        foreach (var stem in stems)
        {
            if (!double.IsFinite(stem.GainDb)) return AudioDspOutput.Fail("Громкость стема должна быть числом.");
            if (GainError(stem.GainDb) is { } gainError) return AudioDspOutput.Fail(gainError);
        }
        var audible = stems.Where(s => !s.Muted).ToList();
        if (audible.Count == 0) return AudioDspOutput.Fail("Все стемы выключены — сводить нечего.");

        using var work = WorkDir.Create();
        var args = new List<string>(Head);
        for (var i = 0; i < audible.Count; i++)
        {
            if (Demuxer(audible[i].Audio) is not { } input) return AudioDspOutput.Fail(NotAudio);
            var path = await work.WriteAsync($"in{i}{input.Extension}", audible[i].Audio, ct);
            args.AddRange(["-f", input.Demuxer, "-i", path]);
        }
        var graph = new StringBuilder();
        for (var i = 0; i < audible.Count; i++)
            graph.Append(Inv, $"[{i}:a]volume={Num(audible[i].GainDb)}dB[s{i}];");
        for (var i = 0; i < audible.Count; i++) graph.Append(Inv, $"[s{i}]");
        // normalize=0: без него amix делит каждый вход на число входов и сведение тише стемов
        graph.Append(Inv, $"amix=inputs={audible.Count}:duration=longest:normalize=0[out]");
        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[out]"]);
        return await EncodeToAsync(work, args, format, ct);
    }

    public async Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
        double? normalizeLufs, AudioFormat format, CancellationToken ct)
    {
        if (!Available) return AudioDspOutput.Fail(Unavailable);
        if (pieces.Count < AudioDspLimits.MinConcatPieces)
            return AudioDspOutput.Fail("Нужно хотя бы два куска — добавьте ещё один.");
        if (pieces.Count > AudioDspLimits.MaxConcatPieces)
            return AudioDspOutput.Fail($"Кусков для склейки — не больше {AudioDspLimits.MaxConcatPieces}.");
        if (joints.Count != pieces.Count - 1)
            return AudioDspOutput.Fail("Стыков должно быть на один меньше, чем кусков.");
        foreach (var joint in joints)
        {
            if (!Enum.IsDefined(joint.Kind)) return AudioDspOutput.Fail("Неизвестный вид стыка.");
            if (joint.Kind != AudioJointKind.Butt
                && (!double.IsFinite(joint.Seconds) || joint.Seconds <= 0 || joint.Seconds > AudioDspLimits.MaxJointSeconds))
                return AudioDspOutput.Fail($"Пауза и плавный переход — больше 0 и не длиннее {AudioDspLimits.MaxJointSeconds} с.");
        }
        if (normalizeLufs is { } lufs && (!double.IsFinite(lufs) || lufs is < AudioDspLimits.MinLufs or > AudioDspLimits.MaxLufs))
            return AudioDspOutput.Fail($"Целевая громкость — от {AudioDspLimits.MinLufs} до {AudioDspLimits.MaxLufs} LUFS.");

        var infos = new AudioDspInfo[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
            if (await ProbeAsync(pieces[i], ct) is { } info) infos[i] = info;
            else return AudioDspOutput.Fail($"Кусок {i + 1}: {NotAudio}");

        // Переход съедает хвост куска слева и начало куска справа: на кусок вместе с обоими его
        // переходами должно хватить длины, иначе acrossfade тихо укоротит результат
        double Fade(int joint) => joint >= 0 && joint < joints.Count && joints[joint].Kind == AudioJointKind.Crossfade
            ? joints[joint].Seconds : 0;
        for (var i = 0; i < pieces.Count; i++)
            if (Fade(i - 1) + Fade(i) >= infos[i].Seconds)
                return AudioDspOutput.Fail($"Кусок {i + 1} короче плавных переходов на его краях.");
        var total = infos.Sum(x => x.Seconds)
            + joints.Sum(j => j.Kind switch { AudioJointKind.Pause => j.Seconds, AudioJointKind.Crossfade => -j.Seconds, _ => 0 });
        if (total > AudioDspLimits.MaxConcatSeconds)
            return AudioDspOutput.Fail($"Склейка длиннее {AudioDspLimits.MaxConcatSeconds / 60:0} минут.");

        // Общий формат: наибольшая частота, стерео — если хоть один кусок не моно
        var rate = infos.Max(x => x.SampleRate);
        var layout = infos.Any(x => x.Channels > 1) ? "stereo" : "mono";

        using var work = WorkDir.Create();
        var args = new List<string>(Head);
        var graph = new StringBuilder();
        for (var i = 0; i < pieces.Count; i++)
        {
            var input = Demuxer(pieces[i])!;
            var path = await work.WriteAsync($"in{i}{input.Extension}", pieces[i], ct);
            args.AddRange(["-f", input.Demuxer, "-i", path]);

            graph.Append(Inv, $"[{i}:a]");
            if (normalizeLufs is { } target)
            {
                var measured = await MeasureLoudnessAsync(pieces[i], $"I={Num(target)}:TP=-1:LRA=11", ct);
                if (measured.Error is not null) return AudioDspOutput.Fail(measured.Error);
                // Тишину выравнивать не к чему — кусок идёт как есть
                if (measured.Values is { } m) graph.Append(LinearLoudnorm(target, m)).Append(',');
            }
            graph.Append(Inv, $"aresample={Int(rate)},aformat=sample_fmts=fltp:sample_rates={Int(rate)}:channel_layouts={layout}[p{i}];");
        }

        var current = "p0";
        for (var i = 0; i < joints.Count; i++)
        {
            var next = $"p{i + 1}";
            var joined = $"j{i}";
            switch (joints[i].Kind)
            {
                case AudioJointKind.Crossfade:
                    graph.Append(Inv, $"[{current}][{next}]acrossfade=d={Num(joints[i].Seconds)}:c1=tri:c2=tri[{joined}];");
                    break;
                case AudioJointKind.Pause:
                    graph.Append(Inv, $"[{current}]apad=pad_dur={Num(joints[i].Seconds)}[g{i}];");
                    graph.Append(Inv, $"[g{i}][{next}]concat=n=2:v=0:a=1[{joined}];");
                    break;
                default:
                    graph.Append(Inv, $"[{current}][{next}]concat=n=2:v=0:a=1[{joined}];");
                    break;
            }
            current = joined;
        }
        graph.Append(Inv, $"[{current}]anull[out]");
        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[out]"]);
        return await EncodeToAsync(work, args, format, ct);
    }

    public async Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct)
    {
        if (!Available) return AudioDspOutput.Fail(Unavailable);
        if (sampleRate is < AudioDspLimits.MinSampleRate or > AudioDspLimits.MaxSampleRate)
            return AudioDspOutput.Fail($"Частота — от {AudioDspLimits.MinSampleRate} до {AudioDspLimits.MaxSampleRate} Гц.");
        if (channels is < 1 or > AudioDspLimits.MaxChannels)
            return AudioDspOutput.Fail($"Каналов — от 1 до {AudioDspLimits.MaxChannels}.");
        var args = new List<string>();
        if (sampleRate is { } rate) args.AddRange(["-ar", Int(rate)]);
        if (channels is { } ch) args.AddRange(["-ac", Int(ch)]);
        return await EncodeAsync(audio, args, format, ct);
    }

    // --- общий конвейер ---

    private const string Unavailable = "Обработка звука недоступна: на сервере нет ffmpeg.";
    private const string NotAudio = "Не удалось разобрать звук: поддерживаются WAV, MP3, FLAC и OGG.";
    private const string Silence = "В записи тишина — нормализовать нечего.";

    private static readonly string[] Head = ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y"];

    private sealed record Input(string Demuxer, string Extension);

    // Только четыре контейнера и только по сигнатуре; всё прочее до ffmpeg не доходит
    private static Input? Demuxer(byte[] audio) => MediaProbe.DetectAudioExtension(audio) switch
    {
        ".wav" => new("wav", ".wav"),
        ".mp3" => new("mp3", ".mp3"),
        ".flac" => new("flac", ".flac"),
        ".ogg" => new("ogg", ".ogg"),
        _ => null,
    };

    private async Task<AudioDspOutput> EncodeAsync(byte[] audio, IReadOnlyList<string> filters, AudioFormat format, CancellationToken ct)
    {
        if (Demuxer(audio) is not { } input) return AudioDspOutput.Fail(NotAudio);
        using var work = WorkDir.Create();
        var path = await work.WriteAsync("in" + input.Extension, audio, ct);
        return await EncodeToAsync(work, [.. Head, "-f", input.Demuxer, "-i", path, .. filters], format, ct);
    }

    private async Task<AudioDspOutput> EncodeToAsync(WorkDir work, IReadOnlyList<string> args, AudioFormat format, CancellationToken ct)
    {
        var output = work.PathOf("out" + AudioFormats.Extension(format));
        string[] codec = format switch
        {
            AudioFormat.Mp3 => ["-c:a", "libmp3lame", "-q:a", "2", "-f", "mp3"],
            AudioFormat.Flac => ["-c:a", "flac", "-f", "flac"],
            AudioFormat.Ogg => ["-c:a", "libvorbis", "-q:a", "5", "-f", "ogg"],
            _ => ["-c:a", "pcm_s16le", "-f", "wav"],
        };
        var run = await RunAsync(Ffmpeg, [.. args, "-vn", .. codec, output], ct);
        if (run.Error is not null) return AudioDspOutput.Fail(run.Error);
        var bytes = await File.ReadAllBytesAsync(output, ct);
        return bytes.Length == 0 ? AudioDspOutput.Fail("ffmpeg вернул пустой файл.") : new AudioDspOutput(bytes, format, null);
    }

    private sealed record Loudness(double I, double Tp, double Lra, double Thresh, double Offset);

    // (null, null) — в записи тишина: громкость −inf, выравнивать нечего
    private async Task<(Loudness? Values, string? Error)> MeasureLoudnessAsync(byte[] audio, string target, CancellationToken ct)
    {
        if (Demuxer(audio) is not { } input) return (null, NotAudio);
        using var work = WorkDir.Create();
        var path = await work.WriteAsync("in" + input.Extension, audio, ct);
        // Отчёт loudnorm печатается на уровне info в stderr — поэтому не «-v error»
        var run = await RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-f", input.Demuxer, "-i", path,
            "-af", $"loudnorm={target}:print_format=json", "-f", "null", "-"], ct);
        if (run.Error is not null) return (null, run.Error);
        var json = run.Stderr;
        var open = json.LastIndexOf('{');
        var close = json.LastIndexOf('}');
        if (open < 0 || close < open) return (null, "ffmpeg не вернул замер громкости.");
        try
        {
            using var doc = JsonDocument.Parse(json[open..(close + 1)]);
            var r = doc.RootElement;
            double Get(string name) => double.Parse(Str(r, name) ?? "x", NumberStyles.Float, Inv);
            var values = new Loudness(Get("input_i"), Get("input_tp"), Get("input_lra"), Get("input_thresh"), Get("target_offset"));
            // У тишины громкость −inf: нормализовать нечего
            return double.IsFinite(values.I) && double.IsFinite(values.Tp) && double.IsFinite(values.Lra)
                && double.IsFinite(values.Thresh) && double.IsFinite(values.Offset)
                ? (values, null)
                : (null, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return (null, null);
        }
    }

    internal sealed record Run(byte[] Stdout, string Stderr, string? Error);

    // Потолки вывода процесса в памяти. От stderr храним только хвост: на битом входе ffmpeg пишет
    // предупреждение на каждый кадр, а нужен нам лишь конец (причина отказа и отчёт loudnorm).
    // stdout — это результат (JSON ffprobe или сэмплы пиков): час звука для пиков — 8 000 Гц × 4 байта
    // × 3 600 с ≈ 110 МБ, больше — отказ, а не рост памяти до таймаута
    internal int StderrCapBytes { get; init; } = 64 * 1024;
    internal long StdoutCapBytes { get; init; } = 128L * 1024 * 1024;

    // ArgumentList, не Arguments: каждый аргумент уходит отдельно, без разбора оболочкой
    internal async Task<Run> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            log.LogWarning(ex, "{Exe} не запустился", exe);
            return new Run([], "", Unavailable);
        }
        if (p is null) return new Run([], "", Unavailable);
        using (p)
        {
            var stdout = new MemoryStream();
            // Переполнение stdout гасит процесс сразу, а не ждёт таймаута
            var tooLarge = false;
            var outTask = ReadCappedAsync(p.StandardOutput.BaseStream, stdout, StdoutCapBytes,
                () => { tooLarge = true; timeout.Cancel(); }, timeout.Token);
            var errTask = ReadTailAsync(p.StandardError.BaseStream, StderrCapBytes, timeout.Token);
            try
            {
                await Task.WhenAll(outTask, errTask, p.WaitForExitAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                if (tooLarge)
                {
                    log.LogWarning("{Exe} выдал больше {Cap} байт результата", exe, StdoutCapBytes);
                    return new Run([], "", "Не удалось обработать звук: результат слишком большой.");
                }
                log.LogWarning("{Exe} не уложился в {Timeout}", exe, Timeout);
                return new Run([], "", $"Обработка звука не уложилась в {Timeout.TotalSeconds:0} с.");
            }
            var stderr = errTask.Result;
            if (p.ExitCode != 0)
            {
                log.LogWarning("{Exe} завершился с кодом {Code}: {Stderr}", exe, p.ExitCode, Tail(stderr));
                return new Run([], stderr, "Не удалось обработать звук: ffmpeg отказал. Возможно, файл повреждён.");
            }
            return new Run(stdout.ToArray(), stderr, null);
        }
    }

    // Весь поток в target, но не больше cap байт: на превышении зовёт overflow и бросает чтение
    private static async Task ReadCappedAsync(Stream source, MemoryStream target, long cap, Action overflow, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + read > cap)
            {
                overflow();
                ct.ThrowIfCancellationRequested();
                return;
            }
            target.Write(buffer, 0, read);
        }
    }

    // Только последние cap байт потока: начало отбрасываем по мере чтения, память не растёт
    private static async Task<string> ReadTailAsync(Stream source, int cap, CancellationToken ct)
    {
        var tail = new byte[cap * 2];
        var length = 0;
        var buffer = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (read >= cap)
            {
                // Порция сама не меньше потолка — прежнее целиком вытесняется
                Buffer.BlockCopy(buffer, read - cap, tail, 0, cap);
                length = cap;
                continue;
            }
            if (length + read > tail.Length)
            {
                // Сдвигаем к началу последние cap байт прочитанного — места хватит и на новую порцию
                Buffer.BlockCopy(tail, length - cap, tail, 0, cap);
                length = cap;
            }
            Buffer.BlockCopy(buffer, 0, tail, length, read);
            length += read;
        }
        var from = Math.Max(0, length - cap);
        return Encoding.UTF8.GetString(tail, from, length - from);
    }

    private static bool Detect(IConfiguration config, ILogger log)
    {
        string Path(string key, string fallback) => config[key] is { Length: > 0 } p ? p : fallback;
        var ok = Responds(Path("AudioDsp:FfmpegPath", "ffmpeg")) && Responds(Path("AudioDsp:FfprobePath", "ffprobe"));
        if (!ok) log.LogInformation("ffmpeg/ffprobe не найдены на хосте — обработка звука без ИИ недоступна");
        return ok;

        static bool Responds(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-version");
                using var p = Process.Start(psi);
                if (p is null) return false;
                _ = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(10_000))
                {
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return false;
                }
                return p.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static string? GainError(double db) => db is < AudioDspLimits.MinGainDb or > AudioDspLimits.MaxGainDb
        ? $"Усиление — от {AudioDspLimits.MinGainDb} до {AudioDspLimits.MaxGainDb} дБ."
        : null;

    private static bool Finite(double? v) => v is null || double.IsFinite(v.Value);
    private static string Num(double v) => v.ToString("0.######", Inv);
    private static string Int(int v) => v.ToString(Inv);
    private static string Tail(string s) => s.Length <= 2000 ? s : s[^2000..];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    // Своя временная папка на каждый вызов: имена файлов в ней задаём мы, удаляется целиком
    private sealed class WorkDir : IDisposable
    {
        private readonly string _root;
        private WorkDir(string root) => _root = root;

        public static WorkDir Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccs-audio-dsp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new WorkDir(root);
        }

        public string PathOf(string name) => System.IO.Path.Combine(_root, name);

        public async Task<string> WriteAsync(string name, byte[] bytes, CancellationToken ct)
        {
            var path = PathOf(name);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
