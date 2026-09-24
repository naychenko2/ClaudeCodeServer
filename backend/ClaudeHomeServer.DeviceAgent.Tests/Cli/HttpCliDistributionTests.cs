using System.Net;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Cli;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

public sealed class HttpCliDistributionTests
{
    // Урезанная копия настоящего manifest.json выпуска 2.1.273.
    private const string ManifestJson = """
        {
          "version": "2.1.273",
          "manifestSignatureEnforcement": "flag",
          "platforms": {
            "linux-x64": { "binary": "claude", "checksum": "6c752e2cc7c110c9df15f26d8d134d438c5ae95dbd610efc1a308bf7f9c5f6c1", "size": 228663608 },
            "win32-x64": { "binary": "claude.exe", "checksum": "19654006672b6da7c945115eea99ca10051796016df563a65b3f0c7d72720ef0", "size": 231776416 }
          }
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Манифест_и_бинарь_берутся_по_раскладке_официального_установщика()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("manifest.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson, Encoding.UTF8) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var dist = new HttpCliDistribution(new HttpClient(handler));

        var manifest = await dist.GetManifestAsync("2.1.273", default);
        await using (var bin = await dist.OpenBinaryAsync("2.1.273", "win32-x64", "claude.exe", default))
        {
            var buffer = new MemoryStream();
            await bin.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(1, 2, 3);
        }

        manifest.Version.Should().Be("2.1.273");
        manifest.Platforms["win32-x64"].Should().Be(new CliPlatformBuild("claude.exe",
            "19654006672b6da7c945115eea99ca10051796016df563a65b3f0c7d72720ef0", 231776416));
        handler.Requests.Select(u => u.AbsoluteUri).Should().Equal(
            "https://downloads.claude.ai/claude-code-releases/2.1.273/manifest.json",
            "https://downloads.claude.ai/claude-code-releases/2.1.273/win32-x64/claude.exe");
        dist.Name.Should().Be("downloads.claude.ai");
    }

    [Fact]
    public async Task Нет_выпуска_это_404_с_кодом()
    {
        var dist = new HttpCliDistribution(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        var error = await FluentActions.Awaiting(() => dist.GetManifestAsync("9.9.9", default))
            .Should().ThrowAsync<HttpRequestException>();
        error.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Мусор_вместо_манифеста_это_отказ_целостности()
    {
        var dist = new HttpCliDistribution(new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>blocked</html>") })));

        await FluentActions.Awaiting(() => dist.GetManifestAsync("2.1.273", default))
            .Should().ThrowAsync<CliIntegrityException>();
    }

    [SkippableFact]
    public void Платформа_Linux_определяется_ключом_манифеста()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "ветка Linux");
        var p = CliPlatform.Detect();
        p.Key.Should().MatchRegex("^linux-(x64|arm64)(-musl)?$");
        p.IsWindows.Should().BeFalse();
    }

    [SkippableFact]
    public void Платформа_Windows_определяется_ключом_манифеста()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ветка Windows — гоняет Windows-CI");
        var p = CliPlatform.Detect();
        p.Key.Should().MatchRegex("^win32-(x64|arm64)$");
        p.IsWindows.Should().BeTrue();
    }
}
