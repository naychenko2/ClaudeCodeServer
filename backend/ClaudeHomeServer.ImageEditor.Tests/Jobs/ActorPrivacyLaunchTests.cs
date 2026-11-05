using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.ImageEditor.Tests.Jobs;

// F2-P2d: чужесть private судится по вызывающему (actorId), а не по арендатору проекта (ownerId).
// Проект один, владелец — арендатор; участники x и y. В прежнем коде актором шёл арендатор: свой
// private участника x отказывался, а private владельца читался в любом чате.
public class ActorPrivacyLaunchTests : IDisposable
{
    private const string Tenant = "owner";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_actor_priv_" + Guid.NewGuid().ToString("N"));
    private readonly ImageEditScope _scope;

    public ActorPrivacyLaunchTests()
    {
        foreach (var who in new[] { "owner", "x", "y" })
        {
            var dir = Path.Combine(_root, ".cc-attachments", "private", who, "g");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "a.jpg"), CharacterStoreTests.Jpeg(1));
        }
        _scope = ImageEditScope.Of(new Project { Id = "p1", OwnerId = Tenant, RootPath = _root });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Priv(string who) => $".cc-attachments/private/{who}/g/a.jpg";

    private ImageEditLaunchAssembler Assembler()
    {
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Tenant, "p1", "q1")).Returns(new ImageEditQuoteInfo(ImageEditOp.Edit, null));
        return new ImageEditLaunchAssembler([HiggsfieldImageEditorTests.Create().Editor], jobs.Object, new SkiaImageRaster());
    }

    private async Task<bool> ReferenceReads(string? actor, string path)
    {
        var req = new ImageEditLaunchRequest("q1", "", null, null, null, null, null, [],
            [(path, ReferenceRole.Object)], null);
        var result = await Assembler().AssembleAsync(Tenant, actor, _scope, req, default);
        return result.Value is not null;
    }

    private async Task<bool> SourceReads(string? actor, string path)
    {
        var req = new ImageEditLaunchRequest("q1", "", null, path, null, null, null, [], [], null);
        var result = await Assembler().AssembleAsync(Tenant, actor, _scope, req, default);
        return result.Value is not null;
    }

    [Theory]
    [InlineData("x", "x", true)]       // свой private участника читается, хотя арендатор — владелец
    [InlineData("x", "owner", false)]  // private владельца участнику отказан
    [InlineData("x", "y", false)]      // private другого участника отказан
    [InlineData("owner", "x", false)]  // владельцу чужой private участника отказан
    [InlineData("owner", "owner", true)]
    [InlineData(null, "x", false)]     // актор неизвестен — fail-closed
    public async Task Образец_и_исходник_судятся_по_вызывающему(string? actor, string author, bool allowed)
    {
        (await ReferenceReads(actor, Priv(author))).Should().Be(allowed, "образец путём");
        (await SourceReads(actor, Priv(author))).Should().Be(allowed, "исходник путём");
    }

    [Fact]
    public async Task Отказ_чужого_private_неотличим_от_несуществующего_пути()
    {
        var req = (string path) => new ImageEditLaunchRequest("q1", "", null, null, null, null, null, [],
            [(path, ReferenceRole.Object)], null);
        var foreign = await Assembler().AssembleAsync(Tenant, "x", _scope, req(Priv("owner")), default);
        var missing = await Assembler().AssembleAsync(Tenant, "x", _scope, req(".cc-attachments/private/owner/g/none.jpg"), default);
        foreign.Error.Should().Be(missing.Error);
        foreign.ErrorCode.Should().Be(missing.ErrorCode);
    }
}
