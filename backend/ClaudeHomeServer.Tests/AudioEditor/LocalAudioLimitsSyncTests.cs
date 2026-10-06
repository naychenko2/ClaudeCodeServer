using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.Images.LocalMedia;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Сторож синхронизации: каталог «Звука» (AudioCatalog, local) держит копию пределов local-media
// (LocalMediaService.Audio, ComfyWorkflows, LocalMediaOptions) — модуль не ссылается на вертикаль Images.
// Разъехались — форма и котировка обещают одно, а запуск отказывает другим (или пропускает лишнее)
public class LocalAudioLimitsSyncTests
{
    // Все схемы «Дополнительно» локальных моделей по всем их операциям
    private static IEnumerable<(string Model, AudioOp Op, AudioParamField Field)> PassedFields() =>
        from model in AudioCatalog.Local
        from op in model.Bindings.Keys
        let schema = AudioCatalog.LocalSchema(model.Info.Id, op)!
        from field in schema.Fields
        where field.Passed
        select (model.Info.Id, op, field);

    private static IReadOnlyList<string> Strings(IReadOnlyList<JsonNode>? values) =>
        [.. (values ?? []).Select(v => v.GetValue<string>())];

    [Fact]
    public void Числовые_параметры_совпадают_с_границами_local_media()
    {
        var numeric = PassedFields().Where(f => f.Field.Min is not null || f.Field.Max is not null).ToList();
        numeric.Should().NotBeEmpty();

        foreach (var (model, op, field) in numeric)
        {
            LocalMediaService.AudioArgRanges.Should().ContainKey(field.Key,
                $"у {model}/{op} параметр «{field.Key}» с границами — его сверка обязана быть в local-media");
            var range = LocalMediaService.AudioArgRanges[field.Key];
            field.Min.Should().Be(range.Min, $"{model}/{op}: нижняя граница «{field.Key}»");
            field.Max.Should().Be(range.Max, $"{model}/{op}: верхняя граница «{field.Key}»");
            field.Default!.GetValue<double>().Should().Be(range.Default, $"{model}/{op}: умолчание «{field.Key}»");
        }
    }

    [Fact]
    public void Длины_строк_и_списки_значений_совпадают_с_local_media()
    {
        var lengths = new Dictionary<string, int>
        {
            ["voice"] = LocalMediaService.MaxVoiceDescriptionLength,
            ["reference_text"] = LocalMediaService.MaxReferenceTextLength,
            ["abc"] = LocalMediaService.MaxAbcLength,
        };
        var choices = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["speaker"] = LocalMediaService.QwenSpeakers,
            ["mode"] = LocalMediaService.SeedVcModes,
            ["format"] = LocalMediaService.StemFormats,
            ["model"] = LocalMediaService.UpsampleModels,
            ["key"] = ComfyWorkflows.AceKeys,
            ["track"] = LocalMediaService.AceTracks,
        };

        foreach (var (model, op, field) in PassedFields())
        {
            if (field.MaxLength is { } max)
            {
                lengths.Should().ContainKey(field.Key, $"у {model}/{op} строка «{field.Key}» с пределом длины");
                max.Should().Be(lengths[field.Key], $"{model}/{op}: длина «{field.Key}»");
            }
            if (field.Enum is { Count: > 0 })
            {
                choices.Should().ContainKey(field.Key, $"у {model}/{op} список значений «{field.Key}»");
                Strings(field.Enum).Should().BeEquivalentTo(choices[field.Key], $"{model}/{op}: значения «{field.Key}»");
            }
            if (field.Items?.Enum is { Count: > 0 } items)
                Strings(items).Should().BeEquivalentTo(LocalMediaService.AceTracks, $"{model}/{op}: элементы «{field.Key}»");
        }
    }

    [Fact]
    public void Пределы_моделей_совпадают_с_local_media()
    {
        var speech = new[] { AudioCatalog.QwenTts, AudioCatalog.MossTts, AudioCatalog.Chatterbox };
        var songs = new[] { AudioCatalog.AceStep, AudioCatalog.Yue2, AudioCatalog.MiniMaxMusic };
        var inputMax = new LocalMediaOptions().MaxAudioInputSeconds;

        foreach (var model in AudioCatalog.Local)
        {
            var id = model.Info.Id;
            var caps = model.Info.Caps;
            if (speech.Contains(id))
            {
                caps.MaxTextChars.Should().Be(LocalMediaService.MaxSpeechTextLength, $"{id}: длина текста речи");
                caps.InputMaxSec.Should().Be(LocalMediaService.MaxReferenceSeconds, $"{id}: длина образца");
            }
            else if (songs.Contains(id))
            {
                caps.MaxTextChars.Should().Be(ComfyWorkflows.MaxLyricsLength, $"{id}: длина слов");
                caps.MinDurationSec.Should().Be(ComfyWorkflows.MinMusicSeconds, $"{id}: кратчайшая песня");
                caps.MaxDurationSec.Should().Be(ComfyWorkflows.MaxMusicSeconds, $"{id}: длиннейшая песня");
            }
            else if (id == AudioCatalog.AudioSr)
                caps.InputMaxSec.Should().Be(LocalMediaService.UpsampleMaxSeconds, $"{id}: вход расширения частот");
            else if (caps.InputMaxSec is not null)
                caps.InputMaxSec.Should().Be(inputMax, $"{id}: общий потолок входного звука");
        }

        // Входной звук у ACE-Step — общий потолок, у кавера YuE2 — длина песни
        Caps(AudioCatalog.AceStep).InputMaxSec.Should().Be(inputMax);
        Caps(AudioCatalog.Yue2).InputMaxSec.Should().Be(ComfyWorkflows.MaxMusicSeconds);

        Caps(AudioCatalog.QwenTts).Languages.Should().BeEquivalentTo(LocalMediaService.QwenLanguages.Keys);
        Caps(AudioCatalog.MossTts).Languages.Should().BeEquivalentTo(LocalMediaService.MossLanguages);
        Caps(AudioCatalog.Chatterbox).Languages.Should().BeEquivalentTo(LocalMediaService.ChatterboxLanguages);
    }

    private static AudioCaps Caps(string id) => AudioCatalog.FindLocal(id)!.Info.Caps;
}
