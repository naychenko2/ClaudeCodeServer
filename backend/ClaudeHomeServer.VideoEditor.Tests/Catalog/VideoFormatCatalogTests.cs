using System.Text.Json;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Engines;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Catalog;

// Каталоги формата видео и совместимость старых данных (ADR-025): «Макс» серая у любой модели без родного размера и без
// PostStep, не серая у local H3; сцены, префы и нити без Format читаются как «Как у модели», Aspect зеркалится
public sealed class VideoFormatCatalogTests
{
    private static MediaFormatCaps Caps(string provider, string model) => provider switch
    {
        "local" => new LocalVideoEngine(null).Models.Single(m => m.Id == model).Caps.Format!,
        _ => FalVideoCatalog.Find(model)!.Info.Caps.Format!,
    };

    private static FormatTierOption Max(MediaFormatCaps caps) =>
        MediaFormatResolver.State(caps, null).Tiers.Single(t => t.Tier == FormatTier.Max);

    [Theory]
    [InlineData("fal", FalVideoCatalog.FluxDraft)]
    [InlineData("fal", FalVideoCatalog.KlingO1)]
    [InlineData("fal", FalVideoCatalog.VeoLite)]
    public void Max_is_grey_with_reason_when_model_has_no_native_size_and_no_post_step(string provider, string model)
    {
        var max = Max(Caps(provider, model));

        max.DisabledReason.Should().NotBeNullOrWhiteSpace();
        max.PostStep.Should().BeNull("автоматического апскейла нет нигде, кроме ступени «Макс» local H3");
    }

    [Fact]
    public void Max_is_not_grey_for_local_h3_and_carries_the_upscale_post_step()
    {
        var max = Max(Caps("local", LocalVideoEngine.MiniMaxH3));

        max.DisabledReason.Should().BeNull();
        max.PostStep.Should().Be(FormatPostSteps.Upscale);
        max.Label.Should().Be("1440p");
    }

    [Fact]
    public void Fal_h3_has_only_native_480p_and_768p_and_max_is_gray_with_a_reason()
    {
        var caps = Caps("fal", FalVideoCatalog.MiniMaxH3);

        caps.Tiers.Select(t => (t.Tier, t.WireValue)).Should().Equal((FormatTier.Standard, "480P"), (FormatTier.High, "768P"));
        var max = MediaFormatResolver.TierOptions(caps, null).Single(t => t.Tier == FormatTier.Max);
        max.DisabledReason.Should().Contain("увеличение, а не родной размер", "у fal 2K и 4K — апскейл 768P-основы, а у нас его нет");
        VideoFormatCatalog.DefaultTier("fal", FalVideoCatalog.MiniMaxH3).Should().Be(FormatTier.High, "прежнее умолчание — родные 768P");
    }

    [Fact]
    public void Only_local_h3_in_the_whole_catalog_has_a_post_step()
    {
        var steps = new[] { FalVideoCatalog.FluxDraft, FalVideoCatalog.VeoLite, FalVideoCatalog.MiniMaxH3, FalVideoCatalog.KlingO1 }
            .SelectMany(id => FalVideoCatalog.Find(id)!.Info.Caps.Format!.Tiers)
            .Concat(Caps("local", LocalVideoEngine.MiniMaxH3).Tiers)
            .Where(t => t.PostStep is not null).ToList();

        steps.Should().ContainSingle().Which.Label.Should().Be("1440p");
    }

    [Fact]
    public void Tier_options_of_local_h3_have_sizes_swapped_for_portrait()
    {
        var caps = Caps("local", LocalVideoEngine.MiniMaxH3);

        var landscape = MediaFormatResolver.TierOptions(caps, "16:9");
        var portrait = MediaFormatResolver.TierOptions(caps, "9:16");

        landscape.Select(t => t.Size).Should().Equal(new FormatSize(864, 480), new FormatSize(1344, 768), new FormatSize(2528, 1440));
        portrait.Select(t => t.Size).Should().Equal(new FormatSize(480, 864), new FormatSize(768, 1344), new FormatSize(1440, 2528));
    }

    [Fact]
    public void Switching_model_back_restores_the_wish_because_only_the_wish_is_stored()
    {
        var wish = new MediaFormat(FormatModes.Aspect, "1:1", FormatTier.Max, null, null, ContextActor.Human, DateTime.UtcNow);

        var onH3 = MediaFormatResolver.Resolve(Caps("local", LocalVideoEngine.MiniMaxH3), wish);
        var onFlux = MediaFormatResolver.Resolve(Caps("fal", FalVideoCatalog.FluxDraft), wish);

        onH3.Aspect.Should().NotBe("1:1");
        onH3.Substituted.Should().BeTrue();
        onFlux.Aspect.Should().Be("1:1", "A → B → A: желание не менялось");
    }

    // ── Совместимость старых данных ─────────────────────────────────────────────

    [Fact]
    public void Old_scene_json_without_format_fields_reads_and_means_as_the_model_does()
    {
        const string old = """
            {"sceneId":"s","name":"Сцена 1","folder":"","settings":{"text":"т","provider":"fal","model":"m","durationSec":5,"aspect":"16:9","sound":false,"count":1},
             "versions":[{"versionId":"v","number":1,"jobId":"j","variant":1,"provider":"fal","model":"m","durationSec":5,"sizeBytes":1,"hasSound":false,"initiator":"human",
             "inputs":{"text":"т"},"createdAt":"2026-10-01T10:00:00Z"}],"currentVersionId":"v","launches":[],"savedFiles":[],"createdAt":"2026-10-01T10:00:00Z"}
            """;

        var scene = JsonSerializer.Deserialize<VideoSceneDto>(old, VideoThreadStore.Json)!;

        scene.Settings.Format.Should().BeNull();
        scene.Settings.Aspect.Should().Be("16:9");
        scene.LockedBy.Should().BeNull();
        scene.Versions[0].Width.Should().BeNull();
        scene.Versions[0].UpscaleError.Should().BeNull();
        VideoStale.Mismatch(scene).Should().BeNull();
    }

    [Fact]
    public void Format_in_threads_json_is_written_as_camel_case_strings_not_numbers()
    {
        var settings = new VideoSceneSettingsDto(null, null, "т", null, null, null, "9:16", null, null,
            new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.High, null, null, ContextActor.Agent, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc)),
            new VideoFrameFitDto("left"));

        var json = JsonSerializer.Serialize(settings, VideoThreadStore.Json);

        json.Should().Contain("\"tier\":\"high\"").And.Contain("\"by\":\"agent\"").And.Contain("\"anchor\":\"left\"");
        JsonSerializer.Deserialize<VideoSceneSettingsDto>(json, VideoThreadStore.Json).Should().Be(settings);
    }

    [Fact]
    public void Normalize_old_client_that_edits_only_aspect_replaces_the_wish_instead_of_losing_the_edit()
    {
        var human = new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null, ContextActor.Human, DateTime.UtcNow);
        var current = new VideoSceneSettingsDto(null, null, "т", null, null, null, "16:9", null, null, human);
        var oldClient = current with { Aspect = "9:16" };

        var saved = VideoSceneFormat.Normalize(oldClient, current, ContextActor.Human, DateTime.UtcNow);

        saved.Format!.Aspect.Should().Be("9:16");
        saved.Format.Tier.Should().Be(FormatTier.High, "ступень не теряется");
        saved.Aspect.Should().Be("9:16");
    }

    [Fact]
    public void Normalize_stamps_author_only_on_a_changed_wish_and_keeps_untouched_human_one()
    {
        var human = new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null, ContextActor.Human, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var current = new VideoSceneSettingsDto(null, null, "т", null, null, null, "16:9", null, null, human);
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

        var same = VideoSceneFormat.Normalize(current with { Text = "другой" }, current, ContextActor.Agent, now);
        var changed = VideoSceneFormat.Normalize(current with { Format = human with { Aspect = "9:16" } }, current, ContextActor.Agent, now);

        same.Format.Should().Be(human, "правка текста агентом не отнимает формат у человека");
        changed.Format!.By.Should().Be(ContextActor.Agent);
        changed.Format.At.Should().Be(now);
        changed.Aspect.Should().Be("9:16");
    }

    [Fact]
    public void Format_validation_for_video_rejects_exact_source_and_bad_aspect()
    {
        VideoSceneFormat.Validate(null).Should().BeNull();
        VideoSceneFormat.Validate(new MediaFormat(FormatModes.Exact, null, null, 1920, 1080, ContextActor.Human, DateTime.UtcNow)).Should().NotBeNull();
        VideoSceneFormat.Validate(new MediaFormat(FormatModes.Source, null, null, null, null, ContextActor.Human, DateTime.UtcNow)).Should().NotBeNull();
        VideoSceneFormat.Validate(new MediaFormat(FormatModes.Aspect, "широко", null, null, null, ContextActor.Human, DateTime.UtcNow)).Should().NotBeNull();
        VideoSceneFormat.Validate(new MediaFormat(FormatModes.Aspect, null, FormatTier.High, null, null, ContextActor.Human, DateTime.UtcNow)).Should().NotBeNull();
        VideoSceneFormat.Validate(new MediaFormat(FormatModes.Aspect, "16:9", FormatTier.High, null, null, ContextActor.Human, DateTime.UtcNow)).Should().BeNull();
        VideoSceneFormat.ValidateFit(new VideoFrameFitDto("diagonal")).Should().NotBeNull();
    }

    [Fact]
    public void Prefs_format_seeds_new_scene_mirrors_aspect_and_old_aspect_only_prefs_still_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var prefs = new VideoPrefsService(new VideoPrefsStore(root));
            var scope = new VideoEditScope("p", null);
            var wish = new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.Max, null, null, ContextActor.Human, DateTime.UtcNow);

            var saved = prefs.Save("u", scope, new VideoPrefsDto("local", null, 5, null, null, 1, wish));
            var seeded = prefs.ForNewScene("u", scope);

            saved.Aspect.Should().Be("9:16", "Aspect — зеркало Format.Aspect");
            seeded.Format.Should().NotBeNull();
            seeded.Format!.Aspect.Should().Be("9:16");
            seeded.Aspect.Should().Be("9:16");

            // Старый фронт пишет только Aspect при сохранённом Format: правка не теряется, ступень остаётся
            var oldClient = prefs.Save("u", scope, saved with { Format = null, Aspect = "16:9" });
            oldClient.Format!.Aspect.Should().Be("16:9");
            oldClient.Format.Tier.Should().Be(FormatTier.Max);

            // Файл без Format (до «Формата») читается как есть
            prefs.Save("u2", scope, new VideoPrefsDto(null, null, null, "4:3", null, null)).Format.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prefs_reject_invalid_format()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var act = () => new VideoPrefsStore(root).Save("u", "p", new VideoPrefsDto(null, null, null, null, null, null,
                new MediaFormat(FormatModes.Exact, null, null, 10, 10, ContextActor.Human, DateTime.UtcNow)));

            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Lock_conflict_only_for_other_aspect_and_free_for_same_as_model_or_force()
    {
        var locked = new VideoFormatLockDto("video/x/x.film", "16:9");
        var scene = new VideoSceneDto("s", "С", "", new VideoSceneSettingsDto(null, null, "т", null, null, null, "16:9", null, null),
            [], null, [], null, [], null, DateTime.UtcNow, LockedBy: locked);
        VideoSceneSettingsDto Set(string? aspect) => scene.Settings with { Aspect = aspect, Format = null };

        VideoSceneFormat.LockedConflict(scene, Set("9:16"), force: false).Should().Be(locked);
        VideoSceneFormat.LockedConflict(scene, Set("9:16"), force: true).Should().BeNull();
        VideoSceneFormat.LockedConflict(scene, Set("16:9"), force: false).Should().BeNull();
        VideoSceneFormat.LockedConflict(scene, Set(null), force: false).Should().BeNull("«Как у модели» пропорции не меняет");
        VideoSceneFormat.LockedConflict(scene with { LockedBy = null }, Set("9:16"), force: false).Should().BeNull();
    }

    // Желание, унаследованное из префов (автор и время чужие), не перештамповывается автором очередной правки:
    // сравниваются режим, пропорции, ступень и размер, но не At и By
    [Fact]
    public void Normalize_does_not_restamp_the_same_wish_with_other_author_and_time()
    {
        var inherited = new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.High, null, null, ContextActor.Human, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var settings = new VideoSceneSettingsDto(null, null, "", null, null, null, "9:16", null, null, inherited);
        var current = settings with { Format = inherited with { By = ContextActor.Agent, At = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) } };

        var normalized = VideoSceneFormat.Normalize(settings, current, ContextActor.Agent, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));

        normalized.Format.Should().Be(inherited, "то же желание: автор и время остаются прежними");

        var changed = VideoSceneFormat.Normalize(settings with { Format = inherited with { Tier = FormatTier.Max } }, current,
            ContextActor.Agent, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
        changed.Format!.By.Should().Be(ContextActor.Agent, "другая ступень — это уже правка автора");
    }
}
