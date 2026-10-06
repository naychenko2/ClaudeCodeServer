using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Media;

// Пример из ADR-025-contracts.md ↔ контракт Core, туда и обратно: каждый пример раздела «Контракты»
// десериализуется в тип из заголовка и сериализуется обратно в тот же JSON (переименованное или
// потерянное поле даёт расхождение); у каждого примера обязан быть тип. Образец —
// VideoContractExamplesTests.
public sealed class MediaFormatContractExamplesTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static readonly IReadOnlyDictionary<string, Type> Covered = new Dictionary<string, Type>
    {
        ["MediaFormat"] = typeof(MediaFormat),
        ["MediaFormat (exact)"] = typeof(MediaFormat),
        ["FormatTierSpec"] = typeof(FormatTierSpec),
        ["ExactSizeSpec"] = typeof(ExactSizeSpec),
        ["MediaFormatCaps"] = typeof(MediaFormatCaps),
        ["ResolvedFormat"] = typeof(ResolvedFormat),
        ["FormatTierOption"] = typeof(FormatTierOption),
        ["FormatTierOption (серая)"] = typeof(FormatTierOption),
        ["MediaFormatState"] = typeof(MediaFormatState),
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
        examples.Should().ContainKey(name, "у каждого контракта в ADR-025-contracts.md обязан быть пример");

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

    // Пример ответа ручки — не выдумка: действующий формат и серые ступени в нём такие, какие считает резолвер
    [Fact]
    public void Пример_состояния_совпадает_с_расчётом_резолвера()
    {
        var example = JsonSerializer.Deserialize<MediaFormatState>(ReadExamples()["MediaFormatState"], Options)!;

        var computed = MediaFormatResolver.State(example.Caps, example.Wish);

        computed.Resolved.Should().BeEquivalentTo(example.Resolved);
        computed.ExactUnavailableReason.Should().Be(example.ExactUnavailableReason);
        computed.Tiers.Select(t => (t.Tier, t.DisabledReason))
            .Should().BeEquivalentTo(example.Tiers.Select(t => (t.Tier, t.DisabledReason)));
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
            var path = Path.Combine(dir.FullName, "docs", "adr", "ADR-025-contracts.md");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("ADR-025-contracts.md не найден выше каталога тестов");
    }
}
