using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Contracts;

// Пример из ADR-022 ↔ контракт, туда и обратно: каждый пример раздела «Контракты» десериализуется в тип из
// заголовка и сериализуется обратно в тот же JSON (переименованное или потерянное поле даёт расхождение);
// обратно — у каждого типа из списка обязан быть пример. Вложенные типы покрыты примерами родителей
public sealed class VideoContractExamplesTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Имя в заголовке ADR → тип контракта. Типы без записи здесь вложенные и проверяются через родителей
    internal static readonly IReadOnlyDictionary<string, Type> Covered = new Dictionary<string, Type>
    {
        ["FrameRef (image)"] = typeof(FrameRef),
        ["FrameRef (file)"] = typeof(FrameRef),
        [nameof(VideoSceneSettingsDto)] = typeof(VideoSceneSettingsDto),
        [nameof(VideoClipVersionDto)] = typeof(VideoClipVersionDto),
        [nameof(VideoLaunchDto)] = typeof(VideoLaunchDto),
        [nameof(VideoSceneStaleDto)] = typeof(VideoSceneStaleDto),
        [nameof(VideoFocusDto)] = typeof(VideoFocusDto),
        [nameof(VideoPrefsDto)] = typeof(VideoPrefsDto),
        [nameof(VideoCatalogDto)] = typeof(VideoCatalogDto),
        [nameof(VideoSceneDto)] = typeof(VideoSceneDto),
        [nameof(VideoThreadsStateDto)] = typeof(VideoThreadsStateDto),
        [nameof(VideoStateDto)] = typeof(VideoStateDto),
        [nameof(VideoSceneCreateRequest)] = typeof(VideoSceneCreateRequest),
        [nameof(VideoSceneFocusRequest)] = typeof(VideoSceneFocusRequest),
        [nameof(VideoSceneSettingsRequest)] = typeof(VideoSceneSettingsRequest),
        [nameof(VideoSceneCurrentRequest)] = typeof(VideoSceneCurrentRequest),
        [nameof(VideoJobDto)] = typeof(VideoJobDto),
        [nameof(VideoQuoteRequest)] = typeof(VideoQuoteRequest),
        [nameof(VideoQuoteResponse)] = typeof(VideoQuoteResponse),
        [nameof(RetryQuote)] = typeof(RetryQuote),
        [nameof(VideoLaunchRequest)] = typeof(VideoLaunchRequest),
        [nameof(VideoLaunchResult)] = typeof(VideoLaunchResult),
        [nameof(SaveSceneRequest)] = typeof(SaveSceneRequest),
        [nameof(SaveSceneResult)] = typeof(SaveSceneResult),
        [nameof(FilmDocument)] = typeof(FilmDocument),
        [nameof(FilmSummaryDto)] = typeof(FilmSummaryDto),
        [nameof(FilmStateDto)] = typeof(FilmStateDto),
        [nameof(FilmPatch)] = typeof(FilmPatch),
        [nameof(FilmBuildStatusDto)] = typeof(FilmBuildStatusDto),
        [nameof(FilmMusicRequest)] = typeof(FilmMusicRequest),
        [nameof(FilmMusicDraftDto)] = typeof(FilmMusicDraftDto),
        [nameof(VideoThreadChangedMessage)] = typeof(VideoThreadChangedMessage),
        [nameof(VideoFilmChangedMessage)] = typeof(VideoFilmChangedMessage),
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Covered.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Пример_из_ADR_проходит_туда_и_обратно(string name)
    {
        var examples = ReadExamples();
        examples.Should().ContainKey(name, "у каждого контракта в ADR-022 обязан быть пример");

        var original = JsonNode.Parse(examples[name])!;
        var value = JsonSerializer.Deserialize(examples[name], Covered[name], Options);
        value.Should().NotBeNull();
        var back = JsonSerializer.SerializeToNode(value, Covered[name], Options);

        JsonNode.DeepEquals(original, back).Should().BeTrue(
            $"пример «{name}» и сериализация типа разошлись.\nПример: {original}\nТип:    {back}");
    }

    [Fact]
    public void В_ADR_нет_примеров_без_типа()
    {
        ReadExamples().Keys.Should().BeSubsetOf(Covered.Keys);
    }

    private static Dictionary<string, string> ReadExamples()
    {
        var text = File.ReadAllText(FindAdr());
        var section = text[text.IndexOf("## Контракты", StringComparison.Ordinal)..];
        var result = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(section, @"####\s+(?<name>[^\n]+)\n+```json\n(?<json>.*?)\n```", RegexOptions.Singleline))
            result[m.Groups["name"].Value.Trim()] = m.Groups["json"].Value;
        return result;
    }

    private static string FindAdr()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "adr", "ADR-022-video-editor.md");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("ADR-022-video-editor.md не найден выше каталога тестов");
    }
}
