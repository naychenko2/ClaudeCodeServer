using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.VideoEditor.Tests.Engines;

// «Формат» у Higgsfield (ADR-025, Б2): пропорции — из aspect_ratios живого каталога, ступени — из enum resolution схемы,
// если он есть, иначе их нет и «Макс» серая. Размер задаёт формат: resolution из Params не принимается
public sealed class HiggsfieldVideoFormatTests
{
    private static string FixturePath(string name, [CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "..", "Fixtures", name);

    private static readonly IReadOnlyList<HiggsfieldVideoCatalog.Model> Models =
        HiggsfieldVideoCatalog.Parse(JsonNode.Parse(File.ReadAllText(FixturePath("higgsfield-models-explore-video.json"))));

    private static HiggsfieldVideoCatalog.Model Model(string id) => Models.Single(m => m.Info.Id == id);

    private static VideoRequest Request(HiggsfieldVideoCatalog.Model model, ResolvedFormat? format, JsonObject? @params = null) =>
        new(model.Info.Id, VideoEditScope.Of(new Project { Id = "p", RootPath = "/p" }), "закат",
            new VideoFrameBytes([1], "image/png"), null, model.Info.Caps.Durations[0], format?.Aspect, false, @params, Format: format);

    private static ResolvedFormat Resolve(HiggsfieldVideoCatalog.Model model, string aspect, FormatTier tier) =>
        MediaFormatResolver.Resolve(model.Info.Caps.Format,
            new MediaFormat(FormatModes.Aspect, aspect, tier, null, null, ContextActor.Human, DateTime.UtcNow));

    // Модель каталога с enum resolution из трёх и более значений; без required-параметров вне Reserved
    private static HiggsfieldVideoCatalog.Model? WithResolution() =>
        Models.FirstOrDefault(m => m.Params.TryGetValue("resolution", out var p) && p.Options.Count >= 2 && !p.Required
            && m.Info.Caps.FirstFrame);

    [Fact]
    public void Catalog_aspects_come_from_aspect_ratios_without_auto()
    {
        var model = Models.First(m => m.Info.Caps.Aspects.Contains("auto"));

        model.Info.Caps.Format!.Aspects.Should().NotContain("auto", "«auto» — режим модели, а не пропорция формата");
        model.Info.Caps.Format.Aspects.Should().Contain("16:9");
    }

    [Fact]
    public void Tiers_come_from_resolution_enum_of_the_schema_and_wire_value_is_the_enum_value()
    {
        var model = WithResolution();
        model.Should().NotBeNull("в живом каталоге есть модели с resolution");
        var options = model!.Params["resolution"].Options;

        var tiers = model.Info.Caps.Format!.Tiers;

        tiers.Should().NotBeEmpty();
        tiers.Select(t => t.WireValue).Should().OnlyContain(w => options.Contains(w!), "ступень — значение enum схемы, не выдумка");
        model.Info.Caps.Format.WireParam.Should().Be("resolution");
    }

    [Fact]
    public void Model_without_resolution_has_no_tiers_and_max_is_grey()
    {
        var model = Models.First(m => !m.Params.ContainsKey("resolution"));

        var state = MediaFormatResolver.State(model.Info.Caps.Format, null);

        model.Info.Caps.Format!.Tiers.Should().BeEmpty();
        state.Tiers.Should().OnlyContain(t => t.DisabledReason != null, "нет родного размера и нет PostStep — серая с причиной");
    }

    [Fact]
    public void Resolution_goes_to_arguments_from_tier_and_aspect_ratio_from_format()
    {
        var model = WithResolution()!;
        var tier = model.Info.Caps.Format!.Tiers[0];
        var aspect = model.Info.Caps.Format.Aspects.First();

        var (args, error) = HiggsfieldVideoEngine.Compose(model, Request(model, Resolve(model, aspect, tier.Tier)));

        error.Should().BeNull();
        args!["resolution"]!.ToString().Should().Be(tier.WireValue);
        if (model.Info.Caps.Aspects.Contains(aspect)) args["aspect_ratio"]!.ToString().Should().Be(aspect);
    }

    [Fact]
    public void Resolution_in_params_is_ignored_by_driver_and_marked_format_controlled()
    {
        var model = WithResolution()!;
        var options = model.Params["resolution"].Options;
        var asked = options[^1];

        var (args, _) = HiggsfieldVideoEngine.Compose(model, Request(model, null, new JsonObject { ["resolution"] = asked }));

        args!.ContainsKey("resolution").Should().BeFalse("размер задаёт только формат");
        HiggsfieldVideoCatalog.FormatControlled.Should().Contain("resolution");
    }

    [Fact]
    public void Tier_value_not_in_schema_enum_is_not_sent()
    {
        var model = WithResolution()!;
        var foreign = new ResolvedFormat(FormatModes.Aspect, "16:9", FormatTier.Max, null, null, "8k", null, false, false, null);

        var (args, _) = HiggsfieldVideoEngine.Compose(model, Request(model, foreign));

        args!.ContainsKey("resolution").Should().BeFalse();
    }
}
