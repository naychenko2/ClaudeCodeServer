using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор CHANGELOG claude-code: диапазон версий, скрытие чужих продуктов, строгое
// распознавание новых моделей, потолки. Фикстура — реальные секции 2.1.281–2.1.285.
public class ClaudeCliChangelogTests
{
    private static readonly string Fixture = File.ReadAllText(
        Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer.Tests", "Fixtures", "claude-cli-changelog.md"));

    // Корень репозитория от каталога сборки (как в PromptBenchTests): .git — папка или файл worktree
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    [Fact]
    public void Range_ExcludesCurrent_NewestFirst()
    {
        var (digest, hasLatest) = ClaudeCliChangelog.Parse(Fixture, "2.1.281", "2.1.285");

        hasLatest.Should().BeTrue();
        digest.Versions.Select(v => v.Version).Should().Equal("2.1.285", "2.1.284", "2.1.283", "2.1.282");
        digest.Truncated.Should().BeFalse();
        digest.ItemCount.Should().BeGreaterThan(100);
    }

    [Fact]
    public void FindsSonnet55_Once_AsFamilyDefault()
    {
        var (digest, _) = ClaudeCliChangelog.Parse(Fixture, "2.1.281", "2.1.285");

        digest.NewModels.Should().ContainSingle().Which.Should().Be(
            new ClaudeCliChangelog.NewModel("Sonnet 5.5", "claude-sonnet-5-5", "2.1.284", true));
        // Модельный пункт остаётся и в общем списке
        digest.Versions.Single(v => v.Version == "2.1.284").Items
            .Should().Contain(i => i.StartsWith("Added Claude Sonnet 5.5"));
    }

    [Fact]
    public void MissingLatestSection_HasLatestFalse_RestParsed()
    {
        var (digest, hasLatest) = ClaudeCliChangelog.Parse(Fixture, "2.1.283", "2.1.286");

        hasLatest.Should().BeFalse();
        digest.Versions.Select(v => v.Version).Should().Equal("2.1.285", "2.1.284");
    }

    [Theory]
    [InlineData("Added Claude Sonnet 5.5 (`claude-sonnet-5-5`), now the default Sonnet model on the Anthropic API — 1M context", "Sonnet 5.5", "claude-sonnet-5-5")]
    [InlineData("Added Claude Opus 5.5 (`claude-opus-5-5`), now the default Opus model — 1M context", "Opus 5.5", "claude-opus-5-5")]
    [InlineData("Added Claude Fable 5.1 (`claude-fable-5-1`), now the default Fable model — 1M context", "Fable 5.1", "claude-fable-5-1")]
    [InlineData("Added Claude Opus 5 (`claude-opus-5`), now the default Opus model — 1M context", "Opus 5", "claude-opus-5")]
    public void StrictModelFormula_Matches(string item, string name, string id)
    {
        var (digest, _) = ClaudeCliChangelog.Parse(Section("2.1.300", item), "2.1.299", "2.1.300");
        digest.NewModels.Should().ContainSingle().Which.Should().Be(new ClaudeCliChangelog.NewModel(name, id, "2.1.300", true));
    }

    [Theory]
    [InlineData("Added Claude Opus 4.8 support and 4.7 → 4.8 migration guidance to the `/claude-api` skill")]
    [InlineData("Added support for Claude Sonnet 4.6")]
    [InlineData("Added Opus 4.5! https://www.anthropic.com/news/claude-opus-4-5")]
    [InlineData("Added `xhigh` effort level for Opus 4.7, sitting between `high` and `max`")]
    [InlineData("Fixed Haiku 4.5 being picked for titles")]
    public void LooseMentions_NotModels(string item)
    {
        var (digest, _) = ClaudeCliChangelog.Parse(Section("2.1.300", item), "2.1.299", "2.1.300");
        digest.NewModels.Should().BeEmpty();
        digest.ItemCount.Should().Be(1);
    }

    [Fact]
    public void HidesOnlyPrefixedOtherProducts_CountsPerVersion()
    {
        var md = Section("2.1.301", "[VSCode] Fixed the diff view", "[SDK] Added setModel", "Fixed [VSCode] mention in the middle")
            + Section("2.1.300", "[Claude Tag] Added threads", "[Windows] Fixed paths", "[IDE] Fixed tab", "Fixed the IDE tag parsing");

        var (digest, _) = ClaudeCliChangelog.Parse(md, "2.1.299", "2.1.301");

        digest.Versions[0].Items.Should().Equal("[SDK] Added setModel", "Fixed [VSCode] mention in the middle");
        digest.Versions[0].Hidden.Should().Be(1);
        digest.Versions[1].Items.Should().Equal("[Windows] Fixed paths", "Fixed the IDE tag parsing");
        digest.Versions[1].Hidden.Should().Be(2);
        digest.HiddenCount.Should().Be(3);
    }

    [Fact]
    public void Since_NarrowsVersionsModelsAndHidden()
    {
        var md = Section("2.1.302", "Added Claude Sonnet 5.6 (`claude-sonnet-5-6`), now the default Sonnet model", "[VSCode] x")
            + Section("2.1.301", "Added Claude Sonnet 5.5 (`claude-sonnet-5-5`), now the default Sonnet model", "[VSCode] y", "[VSCode] z");
        var (digest, _) = ClaudeCliChangelog.Parse(md, "2.1.300", "2.1.302");

        // Две версии семейства: обе в списке, «по умолчанию» — только новая
        digest.NewModels.Select(m => (m.Name, m.IsFamilyDefault)).Should().Equal(("Sonnet 5.6", true), ("Sonnet 5.5", false));

        var narrowed = ClaudeCliChangelog.Since(digest, "2.1.301")!;
        narrowed.Versions.Select(v => v.Version).Should().Equal("2.1.302");
        narrowed.NewModels.Should().ContainSingle().Which.Name.Should().Be("Sonnet 5.6");
        narrowed.HiddenCount.Should().Be(1);

        ClaudeCliChangelog.Since(digest, "2.1.302").Should().BeNull();
    }

    [Fact]
    public void FamilyDefault_PerFamily()
    {
        var md = Section("2.1.301",
            "Added Claude Opus 6 (`claude-opus-6`), now the default Opus model",
            "Added Claude Haiku 5 (`claude-haiku-5`), now the default Haiku model",
            "Added Claude Opus 5.9 (`claude-opus-5-9`), now the default Opus model");
        var (digest, _) = ClaudeCliChangelog.Parse(md, "2.1.300", "2.1.301");

        digest.NewModels.Where(m => m.IsFamilyDefault).Select(m => m.Name).Should().BeEquivalentTo("Opus 6", "Haiku 5");
    }

    [Fact]
    public void Caps_CutWholeOldVersions()
    {
        // 20 версий по 30 пунктов: потолок 400 пунктов срабатывает раньше 15 версий
        var md = string.Concat(Enumerable.Range(1, 20).Reverse()
            .Select(i => Section($"3.0.{i}", Enumerable.Range(0, 30).Select(j => $"Fixed thing {i}-{j}").ToArray())));
        var (digest, _) = ClaudeCliChangelog.Parse(md, "3.0.0", "3.0.20");

        digest.Truncated.Should().BeTrue();
        digest.ItemCount.Should().BeLessThanOrEqualTo(ClaudeCliChangelog.MaxItems);
        digest.Versions.Should().OnlyContain(v => v.Items.Count == 30);
        digest.Versions[0].Version.Should().Be("3.0.20");

        var many = string.Concat(Enumerable.Range(1, 20).Reverse().Select(i => Section($"3.0.{i}", "Fixed one")));
        ClaudeCliChangelog.Parse(many, "3.0.0", "3.0.20").Digest.Versions.Should().HaveCount(ClaudeCliChangelog.MaxVersions);
    }

    [Fact]
    public void LongItem_Trimmed()
    {
        var (digest, _) = ClaudeCliChangelog.Parse(Section("2.1.300", "Fixed " + new string('x', 1000)), "2.1.299", "2.1.300");
        digest.Versions[0].Items[0].Should().HaveLength(ClaudeCliChangelog.MaxItemLength + 1).And.EndWith("…");
    }

    [Fact]
    public void ContinuationLine_GluedToItem()
    {
        var md = "## 2.1.300\n\n- Fixed a long\n  wrapped item\n- Second\n";
        var (digest, _) = ClaudeCliChangelog.Parse(md, "2.1.299", "2.1.300");
        digest.Versions[0].Items.Should().Equal("Fixed a long wrapped item", "Second");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>404: Not Found</html>")]
    [InlineData("## not-a-version\n- Added Claude Sonnet 9 (`claude-sonnet-9`)")]
    public void Garbage_EmptyDigestNoThrow(string? md)
    {
        var (digest, hasLatest) = ClaudeCliChangelog.Parse(md, "2.1.281", "2.1.285");
        hasLatest.Should().BeFalse();
        digest.Versions.Should().BeEmpty();
        digest.NewModels.Should().BeEmpty();
    }

    private static string Section(string version, params string[] items) =>
        $"## {version}\n\n" + string.Concat(items.Select(i => $"- {i}\n")) + "\n";
}
