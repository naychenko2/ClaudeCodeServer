using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// «Авто» с флагом владельца local-media-default сначала пробует локальные модели (ADR-021 §2)
public sealed class AudioAutoLocalTests : IDisposable
{
    private const string Owner = "owner-1";

    private static readonly Project Project = new() { Id = "p-1", RootPath = "/srv/p" };
    private static readonly AudioEditScope ProjectScope = AudioEditScope.Of(Project);
    private static readonly AudioEditScope PersonalScope = new(AudioEditScope.Personal, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-auto-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioEditJobService Service(params IAudioEngine[] engines) =>
        new(engines, new AudioEditWorkspace(Path.Combine(_root, "work")), NullLogger<AudioEditJobService>.Instance);

    private static Engine Fal() => new("fal", [AudioOp.Speak, AudioOp.Song]);

    // У local нет облачных операций; в личном чате local не работает — как у LocalAudioEngine
    private static Engine Local() => new("local", [AudioOp.Speak], personalRefusal: "только в проекте");

    private static async Task<string> AutoProvider(AudioEditJobService svc, AudioEditScope scope, string op = "speak",
        string? provider = null)
    {
        var quote = await svc.QuoteAsync(Owner, scope, new AudioQuoteRequest(AudioModes.Voice, op, provider), CancellationToken.None);
        quote.Error.Should().BeNull();
        return quote.Value!.Provider;
    }

    [Fact]
    public async Task Project_AutoTakesLocal()
    {
        (await AutoProvider(Service(Fal(), Local()), ProjectScope)).Should().Be("local");
    }

    [Fact]
    public async Task LocalLacksOperation_FallsBackToFal()
    {
        (await AutoProvider(Service(Fal(), Local()), ProjectScope, "song")).Should().Be("fal");
    }

    [Fact]
    public async Task LocalDown_FallsBackToFal()
    {
        var local = Local();
        local.IsEnabled = false;
        (await AutoProvider(Service(Fal(), local), ProjectScope)).Should().Be("fal");
    }

    [Fact]
    public async Task PersonalChat_OldOrder()
    {
        (await AutoProvider(Service(Fal(), Local()), PersonalScope)).Should().Be("fal");
    }

    [Fact]
    public async Task ExplicitProvider_NotSubstituted()
    {
        (await AutoProvider(Service(Fal(), Local()), ProjectScope, provider: "fal")).Should().Be("fal");
    }

    // Порядок «Авто» фронт берёт из каталога, а не считает сам: сводка обязана совпасть с котировкой
    [Fact]
    public void Catalog_AutoProviders_FollowFlag()
    {
        IAudioEngine[] engines = [Fal(), Local()];
        AudioCatalogView.Build(engines, ProjectScope, preferLocal: true).AutoProviders.Should().Equal("local", "fal");
        AudioCatalogView.Build(engines, ProjectScope, preferLocal: false).AutoProviders.Should().Equal("fal", "local");
        AudioCatalogView.Build(engines, PersonalScope, preferLocal: true).AutoProviders.Should().Equal("fal");
        // Список показа от флага не зависит
        AudioCatalogView.Build(engines, ProjectScope, preferLocal: true).Providers.Select(p => p.Key)
            .Should().Equal("fal", "local");
    }

    private sealed class Engine(string key, AudioOp[] ops, string? personalRefusal = null) : IAudioEngine
    {
        public bool IsEnabled { get; set; } = true;
        public string Key => key;
        public string Label => key;
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => IsEnabled;
        public bool Registered => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new($"{key}-model", "Модель", new AudioCaps(ops, ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
                AudioLicenses.Apache2, AudioPriceUnits.Free), null),
        ];

        public string? ScopeRefusal(AudioEditScope scope) => scope.IsPersonal ? personalRefusal : null;

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }
}
