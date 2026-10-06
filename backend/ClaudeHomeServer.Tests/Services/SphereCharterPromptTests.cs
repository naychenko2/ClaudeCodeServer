using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// B6: устав сферы — секция sphere-charter только при включённых сферах и проекте в сфере с непустым уставом
public class SphereCharterPromptTests
{
    private sealed class Dir : ISphereDirectory
    {
        public bool IsEnabled { get; set; } = true;
        public string? Charter { get; set; } = "Всегда пиши тесты.";
        public string? SphereOf(string ownerId, string projectId) => IsEnabled && projectId == "p-in" ? "s1" : null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => IsEnabled;
        public string? SphereName(string ownerId, string sphereId) => "Работа";
        public string? CharterOf(string ownerId, string sphereId) => IsEnabled ? Charter : null;
    }

    private static PromptSessionContext Ctx(string? projectId) =>
        new(new Session { ProjectId = projectId }, "owner", null, null);

    [Fact]
    public async Task ПроектВСфереСУставом_СекцияСУставом()
    {
        var sut = new SphereCharterContributor(new Dir());

        var result = await sut.BuildAsync(Ctx("p-in"), "текст хода");

        var section = result!.Sections.Should().ContainSingle().Subject;
        section.Key.Should().Be("sphere-charter");
        section.Text.Should().Contain("«Работа»").And.Contain("Всегда пиши тесты.");
        section.InTurnTail.Should().BeFalse("устав стабилен — хвостом хода не едет");
    }

    [Theory]
    [InlineData("flag-off")]
    [InlineData("empty-charter")]
    [InlineData("outside-sphere")]
    public async Task НетСферыИлиУстава_СекцииНет(string case_)
    {
        var dir = new Dir();
        var project = "p-in";
        if (case_ == "flag-off") dir.IsEnabled = false;
        if (case_ == "empty-charter") dir.Charter = null;
        if (case_ == "outside-sphere") project = "p-out";

        var result = await new SphereCharterContributor(dir).BuildAsync(Ctx(project), null);

        result.Should().BeNull();
    }

    [Fact]
    public void ЧатВнеПроекта_КонтрибьюторВыключен()
    {
        new SphereCharterContributor(new Dir()).IsEnabled(Ctx(null)).Should().BeFalse();
    }
}
