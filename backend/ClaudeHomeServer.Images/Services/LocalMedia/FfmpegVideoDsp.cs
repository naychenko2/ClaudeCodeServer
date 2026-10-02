using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва IVideoDsp (ADR-022 §4): ffmpeg и ffprobe процессом на хосте. Командная строка собирается ТОЛЬКО
// из типизированных параметров: числа — в инвариантной культуре после проверки пределов, пути — полные хостовые,
// уже проверенные вызывающим (границы проекта, символические ссылки). Входы открываются явным `-i` и только как
// файлы (`-protocol_whitelist file`): имя вроде «concat:…» или «http://…» ffmpeg файлом не прочтёт.
// Конфиг — секция AudioDsp (пути к ffmpeg/ffprobe общие с обработкой звука: обнаружение — один и тот же Detect).
//
// Сборка фильма — ТЯЖЁЛЫЙ запуск: ProcessSpec.Heavy, слот единого BuildConcurrencyGate берётся ДО старта процесса,
// запуск идёт через ILauncherFactory.Local — при включённой изоляции процесс уходит в scope ccs-agents.slice под
// тот же MemoryMax, что у ходов агентов. Второго семафора здесь нет и быть не должно: потолок общий.
public sealed class FfmpegVideoDsp(
    IConfiguration config,
    ILogger<FfmpegVideoDsp> log,
    ILauncherFactory launchers,
    BuildConcurrencyGate? gate = null) : IVideoDsp
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const int AudioRate = 48_000;
    private const int MinSide = 64;
    private const int MaxSide = 4096;
    private const int MaxFilmstripFrames = 60;

    // Короткие вызовы (разбор, кадры) — потолок времени; сборка берёт Timeout из плана
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] Head = ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y"];

    private readonly Lazy<bool> _available = new(() => FfmpegAudioDsp.Detect(config, log));

    private string Ffmpeg => config["AudioDsp:FfmpegPath"] is { Length: > 0 } p ? p : "ffmpeg";
    private string Ffprobe => config["AudioDsp:FfprobePath"] is { Length: > 0 } p ? p : "ffprobe";
    private BuildConcurrencyGate Gate => gate ?? BuildConcurrencyGate.Instance;

    public bool Available => _available.Value;

    private const string Unavailable = "Обработка видео недоступна: на сервере нет ffmpeg.";

    // ── Разбор, полоса кадров, последний кадр ─────────────────────────────────────

    public async Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct)
    {
        if (!Available || !Rooted(path)) return null;
        var run = await RunShortAsync(Ffprobe, ["-v", "error", "-protocol_whitelist", "file",
            "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration", "-of", "json", "-i", path], ct);
        if (run.Error is not null) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams)) return null;
            JsonElement? video = null;
            var hasAudio = false;
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type == "video" && video is null) video = s;
                else if (type == "audio") hasAudio = true;
            }
            if (video is not { } v) return null;
            var width = int.Parse(Str(v, "width") ?? "0", Inv);
            var height = int.Parse(Str(v, "height") ?? "0", Inv);
            var seconds = root.TryGetProperty("format", out var format)
                && double.TryParse(Str(format, "duration"), NumberStyles.Float, Inv, out var d) ? d : 0;
            return width > 0 && height > 0 && seconds > 0
                ? new VideoDspInfo(seconds, width, height, Fps(Str(v, "avg_frame_rate")), hasAudio)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            log.LogWarning(ex, "ffprobe вернул неразборчивый ответ");
            return null;
        }
    }

    public async Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct)
    {
        if (!Available) return VideoDspResult.Fail(Unavailable);
        if (!Rooted(path) || !Rooted(outPath)) return VideoDspResult.Fail("Пути к файлам должны быть полными.");
        if (frames is < 1 or > MaxFilmstripFrames) return VideoDspResult.Fail($"Кадров в полосе — от 1 до {MaxFilmstripFrames}.");
        if (height is < 16 or > 1080) return VideoDspResult.Fail("Высота кадра полосы — от 16 до 1080.");
        if (await ProbeAsync(path, ct) is not { } info) return VideoDspResult.Fail("Файл не разобран как видео.");

        var fps = frames / info.Seconds;
        var run = await RunShortAsync(Ffmpeg, [.. Head, "-protocol_whitelist", "file", "-i", path,
            "-vf", $"fps={Num(fps)},scale=-2:{Int(height)},tile={Int(frames)}x1", "-frames:v", "1", "-q:v", "4",
            "-f", "image2", "-update", "1", outPath], ct);
        return Finish(run, outPath, "Не удалось снять полосу кадров.");
    }

    public async Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct)
    {
        if (!Available) return VideoDspResult.Fail(Unavailable);
        if (!Rooted(path) || !Rooted(outPath)) return VideoDspResult.Fail("Пути к файлам должны быть полными.");
        // -sseof близко к концу, дальше каждый кадр перезаписывает один файл (update): остаётся последний
        var run = await RunShortAsync(Ffmpeg, [.. Head, "-protocol_whitelist", "file", "-sseof", "-1", "-i", path,
            "-f", "image2", "-update", "1", outPath], ct);
        return Finish(run, outPath, "Не удалось снять последний кадр.");
    }

    // ── Сборка фильма ─────────────────────────────────────────────────────────────

    public async Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress,
        CancellationToken ct)
    {
        if (!Available) return VideoDspResult.Fail(Unavailable);
        if (Validate(plan, outPath) is { } invalid) return VideoDspResult.Fail(invalid);

        var args = BuildArgs(plan, outPath, out var totalSeconds);
        var spec = BuildSpec(Ffmpeg, args, Path.GetDirectoryName(outPath));

        // Слот — ДО старта процесса и на всё время его жизни; освобождается в finally на любом исходе
        if (Gate.Available == 0) progress?.Report(new VideoAssembleProgress(VideoAssembleProgress.Waiting, 0));
        using var slot = await Gate.AcquireAsync(spec, ct);
        progress?.Report(new VideoAssembleProgress(VideoAssembleProgress.Running, 0));

        var launcher = launchers.Local;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(plan.Timeout);
        Process process;
        try { process = launcher.Start(spec); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            log.LogWarning(ex, "ffmpeg не запустился");
            return VideoDspResult.Fail(Unavailable);
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            var pump = PumpProgressAsync(process.StandardOutput, totalSeconds, progress, timeout.Token);
            try
            {
                await Task.WhenAll(pump, process.WaitForExitAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                try { launcher.Kill(process); } catch (InvalidOperationException) { }
                DeleteQuietly(outPath);
                ct.ThrowIfCancellationRequested();
                log.LogWarning("Сборка фильма не уложилась в {Timeout}", plan.Timeout);
                return VideoDspResult.Fail($"Сборка фильма не уложилась в {plan.Timeout.TotalMinutes:0} мин.");
            }
            var tail = Tail(await stderr);
            if (process.ExitCode != 0)
            {
                DeleteQuietly(outPath);
                log.LogWarning("ffmpeg завершился с кодом {Code}: {Stderr}", process.ExitCode, tail);
                return VideoDspResult.Fail("Не удалось собрать фильм: ffmpeg отказал. Возможно, клип повреждён." +
                    (tail.Length > 0 ? " " + tail : ""));
            }
        }
        if (!File.Exists(outPath)) return VideoDspResult.Fail("Сборка не оставила файла.");
        progress?.Report(new VideoAssembleProgress(VideoAssembleProgress.Running, 1));
        return VideoDspResult.Success;
    }

    // Спека сборки. Heavy ставится ЯВНО здесь: по ней BuildConcurrencyGate решает, занимать ли слот. Без метки
    // ffmpeg прошёл бы мимо потолка, а перекодирование фильма держит гигабайты и все ядра
    internal static ProcessSpec BuildSpec(string ffmpeg, IReadOnlyList<string> args, string? workingDirectory) => new()
    {
        FileName = ffmpeg,
        Args = args,
        WorkingDirectory = workingDirectory,
        RedirectStdin = false,
        StdioEncoding = new UTF8Encoding(false),
        // Короткоживущая утилита с собственным Kill по таймауту: реестр сирот ей не нужен
        Track = false,
        Heavy = true,
    };

    internal static string? Validate(FilmPlan plan, string outPath)
    {
        if (!Rooted(outPath)) return "Путь результата должен быть полным.";
        if (plan.Clips.Count == 0) return "В фильме нет клипов.";
        if (plan.Cuts.Count != plan.Clips.Count - 1) return "Склеек должно быть на одну меньше, чем клипов.";
        if (plan.Width is < MinSide or > MaxSide || plan.Height is < MinSide or > MaxSide
            || plan.Width % 2 != 0 || plan.Height % 2 != 0)
            return $"Размер кадра — чётные числа от {MinSide} до {MaxSide}.";
        if (plan.Fps is < 1 or > 60) return "Частота кадров — от 1 до 60.";
        if (plan.Timeout <= TimeSpan.Zero) return "Не задан предел времени сборки.";
        foreach (var clip in plan.Clips)
        {
            if (!Rooted(clip.Path)) return "Путь клипа должен быть полным.";
            if (!double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.EndSeconds)
                || clip.StartSeconds < 0 || clip.EndSeconds - clip.StartSeconds < MinClipSeconds)
                return "Обрезка клипа задана неверно.";
        }
        foreach (var cut in plan.Cuts)
            if (cut.Type is not ("butt" or "dissolve" or "fade") || !double.IsFinite(cut.Seconds) || cut.Seconds < 0)
                return "Склейка задана неверно.";
        if (plan.Music is { } m)
        {
            if (!Rooted(m.Path)) return "Путь музыки должен быть полным.";
            if (m.VolumePercent is < 0 or > 100 || !double.IsFinite(m.FadeOutSeconds) || m.FadeOutSeconds < 0)
                return "Громкость или затухание музыки заданы неверно.";
        }
        return null;
    }

    // Короче — ffmpeg отдаёт пустое видео, а xfade не может ни от чего считать смещение
    private const double MinClipSeconds = 0.2;

    // Аргументы ffmpeg для плана; totalSeconds — длина фильма по плану (знаменатель прогресса). Чистая
    // функция от плана: фильтр-граф проверяется без запуска процесса
    internal static IReadOnlyList<string> BuildArgs(FilmPlan plan, string outPath, out double totalSeconds)
    {
        var graph = BuildGraph(plan, out totalSeconds, out var videoLabel, out var audioLabel);
        List<string> args = [.. Head, "-protocol_whitelist", "file"];
        foreach (var clip in plan.Clips) { args.Add("-i"); args.Add(clip.Path); }
        if (plan.Music is { } music) { args.Add("-i"); args.Add(music.Path); }
        args.AddRange(["-filter_complex", graph, "-map", $"[{videoLabel}]", "-map", $"[{audioLabel}]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", Int(AudioRate), "-movflags", "+faststart",
            "-progress", "pipe:1", "-f", "mp4", outPath]);
        return args;
    }

    internal static string BuildGraph(FilmPlan plan, out double totalSeconds, out string videoLabel, out string audioLabel)
    {
        var parts = new List<string>();
        var size = $"{Int(plan.Width)}:{Int(plan.Height)}";

        // Каждый клип: кусок → общий размер с полями → общая частота; звук — кусок или тишина нужной длины
        var durations = new double[plan.Clips.Count];
        for (var i = 0; i < plan.Clips.Count; i++)
        {
            var c = plan.Clips[i];
            var d = c.EndSeconds - c.StartSeconds;
            durations[i] = d;
            parts.Add($"[{i}:v]trim=start={Num(c.StartSeconds)}:end={Num(c.EndSeconds)},setpts=PTS-STARTPTS," +
                      $"scale={size}:force_original_aspect_ratio=decrease,pad={size}:(ow-iw)/2:(oh-ih)/2:color=black," +
                      $"setsar=1,fps={Int(plan.Fps)},format=yuv420p,settb=AVTB[v{i}]");
            var audioFormat = $"aresample={Int(AudioRate)},aformat=sample_fmts=fltp:channel_layouts=stereo";
            parts.Add(c.HasAudio
                ? $"[{i}:a]atrim=start={Num(c.StartSeconds)}:end={Num(c.EndSeconds)},asetpts=PTS-STARTPTS,{audioFormat}," +
                  $"apad=whole_dur={Num(d)},atrim=duration={Num(d)},asetpts=PTS-STARTPTS[a{i}]"
                : $"anullsrc=r={Int(AudioRate)}:cl=stereo:d={Num(d)},{audioFormat},asetpts=PTS-STARTPTS[a{i}]");
        }

        // Склейка по порядку: acc — накопленные видео и звук, length — их длина
        var vAcc = "v0";
        var aAcc = "a0";
        var length = durations[0];
        for (var i = 1; i < plan.Clips.Count; i++)
        {
            var cut = plan.Cuts[i - 1];
            var d = durations[i];
            var vOut = $"vj{i}";
            var aOut = $"aj{i}";
            // Наплыв и затемнение не длиннее половины меньшего из соседей: иначе кадр или звук кончится раньше
            var t = Math.Min(cut.Seconds, Math.Min(length, d) / 2);
            switch (cut.Type)
            {
                case "dissolve" when t >= 0.05:
                    parts.Add($"[{vAcc}][v{i}]xfade=transition=fade:duration={Num(t)}:offset={Num(length - t)}[{vOut}]");
                    parts.Add($"[{aAcc}][a{i}]acrossfade=d={Num(t)}:c1=tri:c2=tri[{aOut}]");
                    length += d - t;
                    break;
                case "fade" when t >= 0.05:
                    // Затемнение через чёрное: конец прежнего гаснет, начало следующего проявляется, длина складывается
                    var h = t / 2;
                    parts.Add($"[{vAcc}]fade=t=out:st={Num(length - h)}:d={Num(h)}[{vOut}o]");
                    parts.Add($"[v{i}]fade=t=in:st=0:d={Num(h)}[{vOut}i]");
                    parts.Add($"[{vOut}o][{vOut}i]concat=n=2:v=1:a=0[{vOut}]");
                    parts.Add($"[{aAcc}]afade=t=out:st={Num(length - h)}:d={Num(h)}[{aOut}o]");
                    parts.Add($"[a{i}]afade=t=in:st=0:d={Num(h)}[{aOut}i]");
                    parts.Add($"[{aOut}o][{aOut}i]concat=n=2:v=0:a=1[{aOut}]");
                    length += d;
                    break;
                default:
                    parts.Add($"[{vAcc}][v{i}]concat=n=2:v=1:a=0[{vOut}]");
                    parts.Add($"[{aAcc}][a{i}]concat=n=2:v=0:a=1[{aOut}]");
                    length += d;
                    break;
            }
            vAcc = vOut;
            aAcc = aOut;
        }

        totalSeconds = length;
        videoLabel = vAcc;
        audioLabel = aAcc;
        if (plan.Music is { } music)
        {
            // Музыка подрезается под фильм, глушится и затухает в конце; сводим без нормализации, чтобы
            // не просадить звук клипов вдвое
            var index = plan.Clips.Count;
            var fade = Math.Min(music.FadeOutSeconds, length);
            var filters = $"[{index}:a]aresample={Int(AudioRate)},aformat=sample_fmts=fltp:channel_layouts=stereo," +
                          $"volume={Num(music.VolumePercent / 100.0)},atrim=duration={Num(length)},asetpts=PTS-STARTPTS" +
                          (fade > 0 ? $",afade=t=out:st={Num(length - fade)}:d={Num(fade)}" : "") + "[mus]";
            parts.Add(filters);
            parts.Add($"[{aAcc}][mus]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[amix]");
            audioLabel = "amix";
        }
        return string.Join(';', parts);
    }

    // out_time_us=… в stdout `-progress`: доля фильма, уже записанная в выход
    private static async Task PumpProgressAsync(StreamReader stdout, double total, IProgress<VideoAssembleProgress>? progress,
        CancellationToken ct)
    {
        while (await stdout.ReadLineAsync(ct) is { } line)
        {
            if (progress is null || total <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, Inv, out var micros) && micros >= 0)
                progress.Report(new VideoAssembleProgress(VideoAssembleProgress.Running,
                    Math.Clamp(micros / 1_000_000.0 / total, 0, 0.99)));
        }
    }

    // ── Короткие вызовы ───────────────────────────────────────────────────────────

    private sealed record Run(string Stdout, string Stderr, string? Error);

    private async Task<Run> RunShortAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
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
        timeout.CancelAfter(ShortTimeout);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            log.LogWarning(ex, "{Exe} не запустился", exe);
            return new Run("", "", Unavailable);
        }
        if (p is null) return new Run("", "", Unavailable);
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await Task.WhenAll(stdout, stderr, p.WaitForExitAsync(timeout.Token)); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new Run("", "", "Обработка видео не уложилась в отведённое время.");
            }
            if (p.ExitCode != 0)
            {
                log.LogWarning("{Exe} завершился с кодом {Code}: {Stderr}", exe, p.ExitCode, Tail(stderr.Result));
                return new Run("", stderr.Result, "ffmpeg отказал. Возможно, файл повреждён.");
            }
            return new Run(stdout.Result, stderr.Result, null);
        }
    }

    private static VideoDspResult Finish(Run run, string outPath, string failure)
    {
        if (run.Error is not null)
        {
            DeleteQuietly(outPath);
            return VideoDspResult.Fail($"{failure} {run.Error}");
        }
        return File.Exists(outPath) ? VideoDspResult.Success : VideoDspResult.Fail(failure);
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Rooted(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    private static double Fps(string? rate)
    {
        if (rate is null) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, Inv, out var d) && d > 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, Inv, out var plain) ? plain : 0;
    }

    private static string Num(double v) => v.ToString("0.######", Inv);
    private static string Int(int v) => v.ToString(Inv);
    private static string Tail(string s) => s.Length <= 1500 ? s.Trim() : s[^1500..].Trim();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;
}
