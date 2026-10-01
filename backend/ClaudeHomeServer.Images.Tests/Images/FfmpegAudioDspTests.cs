using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов обработки звука без ИИ на настоящем ffmpeg: каждая операция на сгенерированном синусе даёт
// ожидаемую длину и уровень. Нет ffmpeg на машине разработчика — тест явно пропускается с причиной;
// на CI (CI=true) ffmpeg обязан быть, там пропуск превращается в падение.
public class FfmpegAudioDspTests
{
    private static readonly Lazy<FfmpegAudioDsp> Shared = new(() => Build());

    private static FfmpegAudioDsp Build(string? ffmpeg = null, string? ffprobe = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AudioDsp:FfmpegPath"] = ffmpeg,
            ["AudioDsp:FfprobePath"] = ffprobe,
        }).Build(), NullLogger<FfmpegAudioDsp>.Instance);

    private static FfmpegAudioDsp Dsp()
    {
        var dsp = Shared.Value;
        if (!dsp.Available && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("На CI ffmpeg и ffprobe обязаны быть установлены");
        Skip.IfNot(dsp.Available, "ffmpeg/ffprobe не найдены на этой машине — проверка на настоящем ffmpeg пропущена");
        return dsp;
    }

    // WAV 16 бит: синус 440 Гц амплитудой amplitude (0..1); silenceSeconds тишины перед ним
    private static byte[] Sine(double seconds, double amplitude = 0.25, int rate = 44100, int channels = 1, double silenceSeconds = 0) =>
        Wave(seconds, rate, channels, t => t < silenceSeconds ? 0 : amplitude);

    // Синус 440 Гц с амплитудой, заданной от времени
    private static byte[] Wave(double seconds, int rate, int channels, Func<double, double> amplitudeAt)
    {
        var frames = (int)(seconds * rate);
        var data = frames * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        for (var i = 0; i < frames; i++)
        {
            var v = amplitudeAt(i / (double)rate) * Math.Sin(2 * Math.PI * 440 * i / rate);
            for (var c = 0; c < channels; c++) w.Write((short)Math.Round(v * short.MaxValue));
        }
        return ms.ToArray();
    }

    private static async Task<float> PeakAsync(FfmpegAudioDsp dsp, byte[] audio)
    {
        var peaks = await dsp.PeaksAsync(audio, 1, default);
        peaks.Error.Should().BeNull();
        return peaks.Peaks![0];
    }

    private static async Task<AudioDspInfo> ProbeOk(FfmpegAudioDsp dsp, AudioDspOutput output)
    {
        output.Error.Should().BeNull();
        output.Audio.Should().NotBeNull();
        var info = await dsp.ProbeAsync(output.Audio!, default);
        info.Should().NotBeNull();
        return info!;
    }

    [SkippableFact]
    public async Task Probe_длина_частота_каналы_формат()
    {
        var dsp = Dsp();
        var info = await dsp.ProbeAsync(Sine(2.0, rate: 22050, channels: 2), default);

        info.Should().NotBeNull();
        info!.Seconds.Should().BeApproximately(2.0, 0.01);
        info.SampleRate.Should().Be(22050);
        info.Channels.Should().Be(2);
        info.Format.Should().Be(".wav");
    }

    [SkippableFact]
    public async Task Peaks_повторяют_огибающую_тишина_потом_синус()
    {
        var dsp = Dsp();
        var peaks = await dsp.PeaksAsync(Sine(2.0, amplitude: 0.5, silenceSeconds: 1.0), 10, default);

        peaks.Error.Should().BeNull();
        peaks.Peaks.Should().HaveCount(10);
        peaks.Seconds.Should().BeApproximately(2.0, 0.01);
        peaks.Peaks!.Take(4).Should().AllSatisfy(p => p.Should().BeLessThan(0.01f));
        peaks.Peaks!.Skip(6).Should().AllSatisfy(p => p.Should().BeApproximately(0.5f, 0.03f));
    }

    [SkippableFact]
    public async Task Обрезка_даёт_длину_интервала()
    {
        var dsp = Dsp();
        var info = await ProbeOk(dsp, await dsp.TrimFadeGainAsync(Sine(3.0), new AudioEdit(0.5, 2.0), default));

        info.Seconds.Should().BeApproximately(1.5, 0.01);
    }

    [SkippableFact]
    public async Task Конец_за_пределом_записи_режется_по_её_концу()
    {
        var dsp = Dsp();
        var info = await ProbeOk(dsp, await dsp.TrimFadeGainAsync(Sine(2.0), new AudioEdit(StartSeconds: 1.0, EndSeconds: 99), default));

        info.Seconds.Should().BeApproximately(1.0, 0.01);
    }

    [SkippableFact]
    public async Task Усиление_6дБ_удваивает_уровень()
    {
        var dsp = Dsp();
        var output = await dsp.TrimFadeGainAsync(Sine(1.0, amplitude: 0.25), new AudioEdit(GainDb: 6.0206), default);
        output.Error.Should().BeNull();

        (await PeakAsync(dsp, output.Audio!)).Should().BeApproximately(0.5f, 0.02f);
    }

    [SkippableFact]
    public async Task Фейды_гасят_края_и_не_трогают_середину()
    {
        var dsp = Dsp();
        var output = await dsp.TrimFadeGainAsync(Sine(2.0, amplitude: 0.5), new AudioEdit(FadeInSeconds: 0.5, FadeOutSeconds: 0.5), default);
        output.Error.Should().BeNull();
        var peaks = (await dsp.PeaksAsync(output.Audio!, 20, default)).Peaks!;

        peaks[0].Should().BeLessThan(0.15f);
        peaks[^1].Should().BeLessThan(0.15f);
        peaks[10].Should().BeApproximately(0.5f, 0.03f);
    }

    [SkippableFact]
    public async Task Обрезка_отвергает_пустой_интервал_и_длинные_фейды()
    {
        var dsp = Dsp();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(0.8, 0.2), default)).Error.Should().NotBeNull();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(StartSeconds: 5), default)).Error.Should().NotBeNull();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(FadeInSeconds: 0.6, FadeOutSeconds: 0.6), default)).Error.Should().NotBeNull();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(GainDb: double.NaN), default)).Error.Should().NotBeNull();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(GainDb: 100), default)).Error.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Нормализация_приводит_к_минус_14_LUFS()
    {
        var dsp = Dsp();
        var source = Sine(5.0, amplitude: 0.05);
        (await dsp.MeasureLufsAsync(source, default)).Should().BeLessThan(-25);

        var normalized = await dsp.NormalizeAsync(source, ct: default);
        var info = await ProbeOk(dsp, normalized);

        (await dsp.MeasureLufsAsync(normalized.Audio!, default)).Should().BeApproximately(-14, 0.5);
        info.SampleRate.Should().Be(44100);
        info.Seconds.Should().BeApproximately(5.0, 0.05);
    }

    // Второй проход — линейный по замеру первого: чистое усиление, перепад тихой и громкой частей
    // сохраняется. Динамический режим loudnorm сжал бы его к цели
    [SkippableFact]
    public async Task Нормализация_не_сжимает_динамику()
    {
        var dsp = Dsp();
        var source = Wave(8.0, 44100, 1, t => t < 4 ? 0.02 : 0.08);

        var normalized = await dsp.NormalizeAsync(source, ct: default);
        normalized.Error.Should().BeNull();
        var peaks = (await dsp.PeaksAsync(normalized.Audio!, 8, default)).Peaks!;

        (peaks[6] / peaks[1]).Should().BeApproximately(4f, 0.2f);
    }

    [SkippableFact]
    public async Task Нормализация_тишины_честный_отказ()
    {
        var dsp = Dsp();
        (await dsp.NormalizeAsync(Sine(2.0, amplitude: 0), ct: default)).Error.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Сведение_складывает_стемы_по_длине_самого_длинного()
    {
        var dsp = Dsp();
        var output = await dsp.MixAsync([new AudioStem(Sine(2.0)), new AudioStem(Sine(3.0))], AudioFormat.Wav, default);
        var info = await ProbeOk(dsp, output);

        info.Seconds.Should().BeApproximately(3.0, 0.02);
        // Синусы в фазе: 0,25 + 0,25 — без деления на число входов
        (await PeakAsync(dsp, output.Audio!)).Should().BeApproximately(0.5f, 0.02f);
    }

    [SkippableFact]
    public async Task Сведение_учитывает_mute_и_громкость_стема()
    {
        var dsp = Dsp();
        var muted = await dsp.MixAsync([new AudioStem(Sine(2.0)), new AudioStem(Sine(3.0), Muted: true)], AudioFormat.Wav, default);
        (await ProbeOk(dsp, muted)).Seconds.Should().BeApproximately(2.0, 0.02);
        (await PeakAsync(dsp, muted.Audio!)).Should().BeApproximately(0.25f, 0.02f);

        var quieter = await dsp.MixAsync([new AudioStem(Sine(2.0), GainDb: -6.0206), new AudioStem(Sine(2.0))], AudioFormat.Wav, default);
        (await PeakAsync(dsp, quieter.Audio!)).Should().BeApproximately(0.375f, 0.02f);

        (await dsp.MixAsync([new AudioStem(Sine(1.0), Muted: true)], AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.MixAsync([], AudioFormat.Wav, default)).Error.Should().NotBeNull();
    }

    // Три синуса по 1, 1,5 и 2 с: встык — сумма, паузы добавляют, плавные переходы вычитают
    [SkippableTheory]
    [InlineData(AudioJointKind.Butt, 0.0, 4.5)]
    [InlineData(AudioJointKind.Pause, 0.5, 5.5)]
    [InlineData(AudioJointKind.Crossfade, 0.4, 3.7)]
    public async Task Склейка_трёх_кусков_даёт_ожидаемую_длину(AudioJointKind kind, double seconds, double expected)
    {
        var dsp = Dsp();
        var joint = new AudioJoint(kind, seconds);
        var output = await dsp.ConcatAsync([Sine(1.0), Sine(1.5), Sine(2.0)], [joint, joint], null, AudioFormat.Wav, default);
        var info = await ProbeOk(dsp, output);

        info.Seconds.Should().BeApproximately(expected, 0.05);
    }

    [SkippableFact]
    public async Task Склейка_пауза_ложится_тишиной_между_кусками()
    {
        var dsp = Dsp();
        var output = await dsp.ConcatAsync([Sine(1.0, amplitude: 0.5), Sine(1.0, amplitude: 0.5)], [AudioJoint.Pause(1.0)],
            null, AudioFormat.Wav, default);
        output.Error.Should().BeNull();
        var peaks = (await dsp.PeaksAsync(output.Audio!, 30, default)).Peaks!;

        peaks[5].Should().BeApproximately(0.5f, 0.03f);
        peaks[15].Should().BeLessThan(0.01f);
        peaks[25].Should().BeApproximately(0.5f, 0.03f);
    }

    [SkippableFact]
    public async Task Склейка_разные_стыки_на_каждом_месте()
    {
        var dsp = Dsp();
        var output = await dsp.ConcatAsync([Sine(1.0), Sine(1.0), Sine(1.0), Sine(1.0)],
            [AudioJoint.Butt, AudioJoint.Pause(0.5), AudioJoint.Crossfade(0.3)], null, AudioFormat.Wav, default);

        (await ProbeOk(dsp, output)).Seconds.Should().BeApproximately(4.2, 0.05);
    }

    [SkippableFact]
    public async Task Склейка_сводит_разные_частоты_и_каналы()
    {
        var dsp = Dsp();
        var output = await dsp.ConcatAsync([Sine(1.0, rate: 22050), Sine(1.0, rate: 44100, channels: 2), Sine(1.0, rate: 16000)],
            [AudioJoint.Butt, AudioJoint.Butt], null, AudioFormat.Wav, default);
        var info = await ProbeOk(dsp, output);

        info.SampleRate.Should().Be(44100);
        info.Channels.Should().Be(2);
        info.Seconds.Should().BeApproximately(3.0, 0.05);
    }

    // Тихий и громкий кусок: без выравнивания перепад восьмикратный, с выравниванием — уровни равны
    [SkippableFact]
    public async Task Склейка_выравнивает_громкость_кусков()
    {
        var dsp = Dsp();
        byte[][] pieces = [Sine(3.0, amplitude: 0.04), Sine(3.0, amplitude: 0.32)];

        var raw = await dsp.ConcatAsync(pieces, [AudioJoint.Butt], null, AudioFormat.Wav, default);
        var rawPeaks = (await dsp.PeaksAsync(raw.Audio!, 6, default)).Peaks!;
        (rawPeaks[4] / rawPeaks[1]).Should().BeApproximately(8f, 0.4f);

        var leveled = await dsp.ConcatAsync(pieces, [AudioJoint.Butt], AudioDspLimits.ConcatLufs, AudioFormat.Wav, default);
        leveled.Error.Should().BeNull();
        var peaks = (await dsp.PeaksAsync(leveled.Audio!, 6, default)).Peaks!;
        (peaks[4] / peaks[1]).Should().BeApproximately(1f, 0.1f);
        (await dsp.MeasureLufsAsync(leveled.Audio!, default)).Should().BeApproximately(AudioDspLimits.ConcatLufs, 1);
    }

    [SkippableFact]
    public async Task Склейка_тихий_кусок_не_мешает_выравниванию()
    {
        var dsp = Dsp();
        var output = await dsp.ConcatAsync([Sine(1.0, amplitude: 0), Sine(1.0)], [AudioJoint.Butt],
            AudioDspLimits.ConcatLufs, AudioFormat.Wav, default);

        (await ProbeOk(dsp, output)).Seconds.Should().BeApproximately(2.0, 0.05);
    }

    [SkippableFact]
    public async Task Склейка_отвергает_неверные_куски_и_стыки()
    {
        var dsp = Dsp();
        (await dsp.ConcatAsync([Sine(1.0)], [], null, AudioFormat.Wav, default)).Error.Should().Contain("два куска");
        (await dsp.ConcatAsync([], [], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        var many = Enumerable.Range(0, AudioDspLimits.MaxConcatPieces + 1).Select(_ => Sine(0.1)).ToList();
        (await dsp.ConcatAsync(many, [.. Enumerable.Repeat(AudioJoint.Butt, many.Count - 1)], null, AudioFormat.Wav, default))
            .Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [AudioJoint.Pause(0)], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [AudioJoint.Pause(9)], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [AudioJoint.Crossfade(double.NaN)], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        // Переход длиннее куска
        (await dsp.ConcatAsync([Sine(0.5), Sine(2.0)], [AudioJoint.Crossfade(1.0)], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [AudioJoint.Butt], 100, AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), "не звук"u8.ToArray()], [AudioJoint.Butt], null, AudioFormat.Wav, default))
            .Error.Should().Contain("Кусок 2");
    }

    [SkippableTheory]
    [InlineData(AudioFormat.Mp3, ".mp3")]
    [InlineData(AudioFormat.Flac, ".flac")]
    [InlineData(AudioFormat.Ogg, ".ogg")]
    [InlineData(AudioFormat.Wav, ".wav")]
    public async Task Конвертация_меняет_формат_частоту_и_каналы(AudioFormat format, string extension)
    {
        var dsp = Dsp();
        var output = await dsp.ConvertAsync(Sine(2.0, channels: 2), format, 22050, 1, default);
        var info = await ProbeOk(dsp, output);

        output.Extension.Should().Be(extension);
        info.Format.Should().Be(extension);
        info.SampleRate.Should().Be(22050);
        info.Channels.Should().Be(1);
        info.Seconds.Should().BeApproximately(2.0, 0.1);
    }

    [SkippableFact]
    public async Task Не_звук_до_ffmpeg_не_доходит()
    {
        var dsp = Dsp();
        // Плейлист со ссылкой на файл хоста: без явного демультиплексора ffmpeg прочитал бы этот файл
        // и вернул его звук как результат
        // (ffmpeg 8 сам отбивает сегмент .wav, а .mp3 пропускает)
        var host = Path.Combine(Path.GetTempPath(), $"ccs-dsp-host-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(host, (await dsp.ConvertAsync(Sine(1.0), AudioFormat.Mp3, null, null, default)).Audio!);
        var playlist = System.Text.Encoding.UTF8.GetBytes(
            $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.0,\n{host}\n#EXT-X-ENDLIST\n");
        try
        {
            (await dsp.ProbeAsync(playlist, default)).Should().BeNull();
            (await dsp.ConvertAsync(playlist, AudioFormat.Wav, null, null, default)).Error.Should().NotBeNull();
            (await dsp.PeaksAsync(playlist, 10, default)).Error.Should().NotBeNull();
        }
        finally { File.Delete(host); }
        (await dsp.ConvertAsync(Sine(1.0), AudioFormat.Wav, 1, null, default)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Без_ffmpeg_недоступен_и_отвечает_отказом_без_исключений()
    {
        var missing = Path.Combine(Path.GetTempPath(), "нет-такого-ffmpeg-" + Guid.NewGuid().ToString("N"));
        var dsp = Build(missing, missing);

        dsp.Available.Should().BeFalse();
        (await dsp.ProbeAsync(Sine(1.0), default)).Should().BeNull();
        (await dsp.PeaksAsync(Sine(1.0), 10, default)).Error.Should().NotBeNull();
        (await dsp.TrimFadeGainAsync(Sine(1.0), new AudioEdit(0, 0.5), default)).Error.Should().NotBeNull();
        (await dsp.NormalizeAsync(Sine(1.0), ct: default)).Error.Should().NotBeNull();
        (await dsp.MixAsync([new AudioStem(Sine(1.0))], AudioFormat.Wav, default)).Error.Should().NotBeNull();
        (await dsp.ConvertAsync(Sine(1.0), AudioFormat.Mp3, null, null, default)).Error.Should().NotBeNull();
        (await dsp.ConcatAsync([Sine(1.0), Sine(1.0)], [AudioJoint.Butt], null, AudioFormat.Wav, default)).Error.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Известная_длительность_не_зовёт_ffprobe_повторно()
    {
        Dsp();
        Skip.If(OperatingSystem.IsWindows(), "заглушка процесса — sh-скрипт");
        // ffprobe-заглушка отказывает на любом разборе: без known обрезка и нормализация упали бы
        // на нём, с known идут сразу в настоящий ffmpeg
        var probe = Stub("exit 1");
        try
        {
            var dsp = Build(ffprobe: probe);
            var known = new AudioDspInfo(2.0, 44100, 1, ".wav");
            (await dsp.TrimFadeGainAsync(Sine(2.0), new AudioEdit(0.5, 1.5), default)).Error.Should().NotBeNull();

            var trimmed = await dsp.TrimFadeGainAsync(Sine(2.0), new AudioEdit(0.5, 1.5), default, known);
            var normalized = await dsp.NormalizeAsync(Sine(2.0), ct: default, known: known);

            (await ProbeOk(Shared.Value, trimmed)).Seconds.Should().BeApproximately(1.0, 0.01);
            (await ProbeOk(Shared.Value, normalized)).Seconds.Should().BeApproximately(2.0, 0.05);
        }
        finally { File.Delete(probe); }
    }

    // Процесс-заглушка вместо ffmpeg: на -version отвечает успехом, иначе печатает много мусора
    // в stderr (или stdout) и завершается так, как велено
    private static string Stub(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccs-ffmpeg-stub-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\nif [ \"$1\" = \"-version\" ]; then exit 0; fi\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [SkippableFact]
    public async Task Большой_stderr_хранится_только_хвостом()
    {
        Skip.If(OperatingSystem.IsWindows(), "заглушка процесса — sh-скрипт");
        // 8 МБ мусора, затем причина отказа последней строкой
        var stub = Stub("head -c 8000000 /dev/zero | tr '\\0' 'x' >&2; echo ПРИЧИНА-ОТКАЗА >&2; exit 1");
        try
        {
            var dsp = Build(stub, stub);
            dsp.Available.Should().BeTrue();

            var run = await dsp.RunAsync(stub, ["-i", "x"], default);

            run.Error.Should().NotBeNull();
            run.Stderr.Length.Should().BeLessThanOrEqualTo(dsp.StderrCapBytes);
            run.Stderr.TrimEnd().Should().EndWith("ПРИЧИНА-ОТКАЗА");
        }
        finally { File.Delete(stub); }
    }

    [SkippableFact]
    public async Task Stdout_сверх_потолка_отказ_без_ожидания_таймаута()
    {
        Skip.If(OperatingSystem.IsWindows(), "заглушка процесса — sh-скрипт");
        // Бесконечный поток в stdout: без потолка память росла бы до таймаута
        var stub = Stub("cat /dev/zero");
        try
        {
            var dsp = new FfmpegAudioDsp(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AudioDsp:FfmpegPath"] = stub,
                ["AudioDsp:FfprobePath"] = stub,
                ["AudioDsp:TimeoutSeconds"] = "60",
            }).Build(), NullLogger<FfmpegAudioDsp>.Instance) { StdoutCapBytes = 1024 * 1024 };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = await dsp.RunAsync(stub, ["-i", "x"], default);

            run.Error.Should().Contain("слишком большой");
            run.Stdout.Should().BeEmpty();
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        }
        finally { File.Delete(stub); }
    }

    [Fact]
    public void Images_регистрирует_шов_IAudioDsp()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddLocalMedia(config);

        services.Should().Contain(d => d.ServiceType == typeof(IAudioDsp) && d.ImplementationType == typeof(FfmpegAudioDsp)
            && d.Lifetime == ServiceLifetime.Singleton);
    }
}
