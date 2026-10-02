using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Voices;

// Библиотека «Голоса» (ADR-021 §2): voices/<slug>/ серверного проекта, CRUD, отказ личной области и
// локальному проекту до диска, slug не выводит за voices/, пара RVC пишется вместе, признак «клон
// MiniMax протух» считается по подменяемым часам.
public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly MutableTime _time = new();
    private readonly VoiceLibrary _lib;
    private readonly AudioEditScope _scope;

    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 1, 2, 3];
    private static readonly byte[] Mp3 = [.. "ID3"u8, 4, 5, 6];

    public VoiceLibraryTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "voices_" + Guid.NewGuid().ToString("N")[..8]);
        _root = Path.Combine(baseDir, "project");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _lib = new VoiceLibrary(_time);
        _scope = AudioEditScope.Of(new Project { Id = "p1", RootPath = _root, OwnerId = "u1" });
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { }
    }

    private VoiceManifest Create(string name = "Аня", params byte[][] samples)
    {
        var result = _lib.CreateFromSamples(_scope, name, "Привет, меня зовут Аня",
            [.. (samples.Length == 0 ? [Wav] : samples).Select(b => new VoiceSampleUpload(b))]);
        result.Error.Should().BeNull();
        return result.Value!.Manifest;
    }

    private string VoiceDir(string slug) => Path.Combine(_root, "voices", slug);

    // ── CRUD ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_WritesSamplesAndManifest_ListAndGetReadThemBack()
    {
        var result = _lib.CreateFromSamples(_scope, "  Аня  ", " текст ", [new(Wav), new(Mp3)]);

        var change = result.Value!;
        var slug = change.Manifest.Slug;
        VoiceStore.IsValidSlug(slug).Should().BeTrue();
        change.Written.Should().BeEquivalentTo(
            $"voices/{slug}/sample-01.wav", $"voices/{slug}/sample-02.mp3", $"voices/{slug}/voice.json");
        File.ReadAllBytes(Path.Combine(VoiceDir(slug), "sample-02.mp3")).Should().Equal(Mp3);

        var voice = _lib.Get(_scope, slug)!;
        voice.Name.Should().Be("Аня");
        voice.Kind.Should().Be(VoiceKinds.Samples);
        voice.Transcript.Should().Be("текст");
        voice.Samples.Select(s => s.File).Should().Equal("sample-01.wav", "sample-02.mp3");
        voice.Path.Should().Be($"voices/{slug}");
        _lib.List(_scope)!.Select(v => v.Slug).Should().Equal(slug);
    }

    [Fact]
    public void Create_SameName_GetsNextSlug_AndNeverOverwrites()
    {
        var first = Create("Аня");
        var second = Create("Аня");

        second.Slug.Should().Be(first.Slug + "-2");
        _lib.List(_scope)!.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("", 1, "имя")]
    [InlineData("Аня", 0, "записей")]
    [InlineData("Аня", 6, "записей")]
    public void Create_Invalid_RefusesWithoutFolder(string name, int count, string hint)
    {
        var result = _lib.CreateFromSamples(_scope, name, null, [.. Enumerable.Repeat(new VoiceSampleUpload(Wav), count)]);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain(hint);
        (Directory.Exists(Path.Combine(_root, "voices")) ? Directory.GetDirectories(Path.Combine(_root, "voices")) : [])
            .Should().BeEmpty();
    }

    [Fact]
    public void Create_NotAudio_Refused()
    {
        var result = _lib.CreateFromSamples(_scope, "Аня", null, [new([1, 2, 3, 4, 5])]);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("WAV");
    }

    [Fact]
    public void Update_RenamesAndSetsTranscript_SlugStays()
    {
        var slug = Create().Slug;

        var result = _lib.Update(_scope, slug, " Анна ", "")!;

        result.Value!.Manifest.Slug.Should().Be(slug);
        var voice = _lib.Get(_scope, slug)!;
        voice.Name.Should().Be("Анна");
        voice.Transcript.Should().BeNull();
        _lib.Update(_scope, slug, "  ", null)!.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        _lib.Update(_scope, "nope", "X", null).Should().BeNull();
    }

    [Fact]
    public void Samples_AddContinuesNumbering_RemoveDeletesFile_LastOneStays()
    {
        var slug = Create("Аня", Wav, Wav).Slug;

        var added = _lib.AddSamples(_scope, slug, [new(Mp3)])!.Value!;
        added.Written.Should().Contain($"voices/{slug}/sample-03.mp3");

        var removed = _lib.RemoveSample(_scope, slug, "sample-01.wav")!.Value!;
        removed.Deleted.Should().Equal($"voices/{slug}/sample-01.wav");
        File.Exists(Path.Combine(VoiceDir(slug), "sample-01.wav")).Should().BeFalse();
        _lib.RemoveSample(_scope, slug, "sample-02.wav")!.Value.Should().NotBeNull();

        var last = _lib.RemoveSample(_scope, slug, "sample-03.mp3")!;
        last.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        File.Exists(Path.Combine(VoiceDir(slug), "sample-03.mp3")).Should().BeTrue();
        _lib.RemoveSample(_scope, slug, "sample-09.wav")!.ErrorCode.Should().Be(AudioEditErrorCodes.FileNotFound);
        _lib.AddSamples(_scope, slug, [.. Enumerable.Repeat(new VoiceSampleUpload(Wav), 5)])!.ErrorCode
            .Should().Be(AudioEditErrorCodes.InvalidRequest);
    }

    // Сбой на N-м образце: уже записанные этим вызовом убираются, манифест прежний — отказ, а не полуфабрикат
    [Fact]
    public void AddSamples_FailureOnSecond_RollsBackFirst_ManifestUntouched()
    {
        var slug = Create("Аня", Wav).Slug;
        var dir = VoiceDir(slug);
        // Каталог на месте второго имени: запись в него падает
        Directory.CreateDirectory(Path.Combine(dir, "sample-03.mp3"));

        var result = _lib.AddSamples(_scope, slug, [new(Mp3), new(Mp3)])!;

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        File.Exists(Path.Combine(dir, "sample-02.mp3")).Should().BeFalse();
        _lib.Get(_scope, slug)!.Samples.Select(s => s.File).Should().Equal("sample-01.wav");
    }

    [Fact]
    public void Delete_RemovesOnlyVoiceFolder()
    {
        var keep = Create("Андрей").Slug;
        var slug = Create("Аня").Slug;
        File.WriteAllText(Path.Combine(_root, "voices", "readme.txt"), "x");

        _lib.Delete(_scope, slug).Should().BeTrue();

        Directory.Exists(VoiceDir(slug)).Should().BeFalse();
        Directory.Exists(VoiceDir(keep)).Should().BeTrue();
        File.Exists(Path.Combine(_root, "voices", "readme.txt")).Should().BeTrue();
        _lib.Delete(_scope, slug).Should().BeFalse();
    }

    [Fact]
    public void Manifest_UnknownKeys_SurviveRewrite()
    {
        var slug = Create().Slug;
        var file = Path.Combine(VoiceDir(slug), "voice.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["future"] = "x";
        json["providers"]!["elevenlabs"] = new JsonObject { ["voiceId"] = "e1" };
        File.WriteAllText(file, json.ToJsonString());

        _lib.Update(_scope, slug, "Анна", null);

        var after = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        after["future"]!.GetValue<string>().Should().Be("x");
        after["providers"]!["elevenlabs"]!["voiceId"]!.GetValue<string>().Should().Be("e1");
    }

    // ── Граница проекта ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("-x")]
    public void Slug_OutsideWhitelist_IsNotFound_AndTouchesNothingOutside(string slug)
    {
        Create();
        // Приманки снаружи и в самом корне проекта: «..» из voices/ уводит в корень, а он внутри границы
        const string bait = """{"name":"чужой","samples":[{"file":"voice.json"}]}""";
        File.WriteAllText(Path.Combine(_outside, "voice.json"), bait);
        File.WriteAllText(Path.Combine(_root, "voice.json"), bait);
        _lib.List(_scope).Should().ContainSingle();

        _lib.Get(_scope, slug).Should().BeNull();
        _lib.Update(_scope, slug, "X", null).Should().BeNull();
        _lib.AddSamples(_scope, slug, [new(Wav)]).Should().BeNull();
        _lib.RemoveSample(_scope, slug, "voice.json").Should().BeNull();
        _lib.OpenFile(_scope, slug, "voice.json").Should().BeNull();
        _lib.Delete(_scope, slug).Should().BeFalse();

        Directory.GetFiles(_outside).Should().Equal(Path.Combine(_outside, "voice.json"));
        File.ReadAllText(Path.Combine(_root, "voice.json")).Should().Be(bait);
        Directory.Exists(Path.Combine(_root, "voices")).Should().BeTrue();
    }

    [Theory]
    [InlineData("../voice.json")]
    [InlineData("voice.json")]
    [InlineData("..")]
    public void SampleFile_NotListed_IsNotServedOrRemoved(string file)
    {
        var slug = Create().Slug;

        _lib.OpenFile(_scope, slug, file).Should().BeNull();
        _lib.RemoveSample(_scope, slug, file)!.ErrorCode.Should().Be(AudioEditErrorCodes.FileNotFound);
        File.Exists(Path.Combine(VoiceDir(slug), "voice.json")).Should().BeTrue();
        _lib.OpenFile(_scope, slug, "sample-01.wav").Should().Be(Path.Combine(VoiceDir(slug), "sample-01.wav"));
    }

    [Fact]
    public void VoicesFolderAsLink_IsRefused()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "voices"), _outside);

        _lib.CreateFromSamples(_scope, "Аня", null, [new(Wav)]).ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        _lib.List(_scope).Should().BeEmpty();
        Directory.GetFileSystemEntries(_outside).Should().BeEmpty();
    }

    [Fact]
    public void VoiceFolderAsLink_IsInvisible_AndNotDeletedThrough()
    {
        Create();
        File.WriteAllText(Path.Combine(_outside, "voice.json"), """{"name":"чужой","samples":[{"file":"secret.wav"}]}""");
        File.WriteAllBytes(Path.Combine(_outside, "secret.wav"), Wav);
        Directory.CreateSymbolicLink(Path.Combine(_root, "voices", "evil"), _outside);

        _lib.List(_scope)!.Select(v => v.Slug).Should().NotContain("evil");
        _lib.Get(_scope, "evil").Should().BeNull();
        _lib.OpenFile(_scope, "evil", "secret.wav").Should().BeNull();
        _lib.Delete(_scope, "evil").Should().BeFalse();
        File.Exists(Path.Combine(_outside, "secret.wav")).Should().BeTrue();
    }

    // ── Отказ до диска ───────────────────────────────────────────────────────────

    [Fact]
    public void PersonalScope_Refused()
    {
        var personal = new AudioEditScope(AudioEditScope.Personal, null);

        _lib.List(personal).Should().BeNull();
        _lib.CreateFromSamples(personal, "Аня", null, [new(Wav)]).ErrorCode.Should().Be(VoiceLibrary.ProjectOnlyCode);
        _lib.Update(personal, "anya", "X", null)!.ErrorCode.Should().Be(VoiceLibrary.ProjectOnlyCode);
    }

    [Fact]
    public void LocalProject_RefusedBeforeDisk()
    {
        // Каталог-ловушка: у локального проекта RootPath — путь устройства, сервер его не создаёт
        var trap = Path.Combine(_outside, "trap");
        var local = AudioEditScope.Of(new Project { Id = "p2", RootPath = trap, OwnerId = "u1", DeviceId = "dev" });
        var model = Path.Combine(_outside, "m.pth");
        File.WriteAllBytes(model, [1]);

        _lib.List(local).Should().BeNull();
        _lib.CreateFromSamples(local, "Аня", null, [new(Wav)]).ErrorCode.Should().Be(ProjectCapabilityGuard.Code);
        _lib.CreateFromRvc(local, "Андрей", model, model).ErrorCode.Should().Be(ProjectCapabilityGuard.Code);
        _lib.AddSamples(local, "anya", [new(Wav)])!.ErrorCode.Should().Be(ProjectCapabilityGuard.Code);
        _lib.Delete(local, "anya").Should().BeFalse();

        Directory.Exists(trap).Should().BeFalse();
    }

    // ── RVC ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rvc_PairIsSavedTogether()
    {
        var model = Path.Combine(_outside, "job.pth");
        var index = Path.Combine(_outside, "job.index");
        File.WriteAllBytes(model, [1, 2]);
        File.WriteAllBytes(index, [3, 4]);

        var change = _lib.CreateFromRvc(_scope, "Андрей", model, index).Value!;

        var slug = change.Manifest.Slug;
        File.ReadAllBytes(Path.Combine(VoiceDir(slug), "voice.pth")).Should().Equal(1, 2);
        File.ReadAllBytes(Path.Combine(VoiceDir(slug), "voice.index")).Should().Equal(3, 4);
        var voice = _lib.Get(_scope, slug)!;
        voice.Kind.Should().Be(VoiceKinds.Rvc);
        voice.Providers.Single(p => p.Provider == VoiceProviders.Rvc).State.Should().Be(VoiceProviderStates.Ok);
        VoiceProviders.RvcPair(VoiceStore.Get(_root, slug)!.Providers).Should().Be(("voice.pth", "voice.index"));
        _lib.OpenFile(_scope, slug, "voice.index").Should().NotBeNull();
        _lib.AddSamples(_scope, slug, [new(Wav)])!.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
    }

    [Fact]
    public void Rvc_MissingIndex_WritesNothing()
    {
        var model = Path.Combine(_outside, "job.pth");
        File.WriteAllBytes(model, [1, 2]);

        var result = _lib.CreateFromRvc(_scope, "Андрей", model, Path.Combine(_outside, "nope.index"));

        result.ErrorCode.Should().Be(AudioEditErrorCodes.FileNotFound);
        _lib.List(_scope).Should().BeEmpty();
        (Directory.Exists(Path.Combine(_root, "voices")) ? Directory.GetFileSystemEntries(Path.Combine(_root, "voices")) : [])
            .Should().BeEmpty();
    }

    // ── Кеш поставщиков и «протух» ───────────────────────────────────────────────

    private static string MiniMaxState(VoiceDto voice) =>
        voice.Providers.Single(p => p.Provider == VoiceProviders.MiniMax).State;

    [Fact]
    public void MiniMax_StaleAfterSevenDaysWithoutUse_TouchRestartsCountdown()
    {
        var slug = Create().Slug;
        MiniMaxState(_lib.Get(_scope, slug)!).Should().Be(VoiceProviderStates.None);

        _lib.UpdateProviders(_scope, slug, (p, now) => VoiceProviders.SetMiniMax(p, "mm-voice-1", now)).Should().NotBeNull();
        MiniMaxState(_lib.Get(_scope, slug)!).Should().Be(VoiceProviderStates.Ok);

        _time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        MiniMaxState(_lib.Get(_scope, slug)!).Should().Be(VoiceProviderStates.Ok);
        _lib.Get(_scope, slug)!.NeedsAttention.Should().BeFalse();

        _time.Advance(TimeSpan.FromMinutes(1));
        var stale = _lib.Get(_scope, slug)!;
        MiniMaxState(stale).Should().Be(VoiceProviderStates.Stale);
        stale.NeedsAttention.Should().BeTrue();

        // Использование отмечено — отсчёт заново, дата живёт в voice.json и читается после перезаписи
        _lib.TouchMiniMax(_scope, slug).Should().BeTrue();
        _time.Advance(TimeSpan.FromDays(6));
        MiniMaxState(_lib.Get(_scope, slug)!).Should().Be(VoiceProviderStates.Ok);
        _time.Advance(TimeSpan.FromDays(1));
        MiniMaxState(_lib.Get(_scope, slug)!).Should().Be(VoiceProviderStates.Stale);
    }

    [Fact]
    public void Providers_CacheIsKeptPerProvider_IdsNotExposed()
    {
        var slug = Create().Slug;
        _lib.TouchMiniMax(_scope, slug).Should().BeFalse();

        _lib.UpdateProviders(_scope, slug, (p, now) =>
        {
            VoiceProviders.SetHiggsfield(p, "el_7f2", now);
            VoiceProviders.SetFalQwen(p, "https://fal.media/emb.safetensors", now);
        });

        var voice = _lib.Get(_scope, slug)!;
        voice.Providers.Where(p => p.State == VoiceProviderStates.Ok).Select(p => p.Provider)
            .Should().BeEquivalentTo(VoiceProviders.Higgsfield, VoiceProviders.FalQwen);
        var json = File.ReadAllText(Path.Combine(VoiceDir(slug), "voice.json"));
        json.Should().Contain("el_7f2").And.Contain("emb.safetensors");
        System.Text.Json.JsonSerializer.Serialize(voice).Should().NotContain("el_7f2");
    }

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
