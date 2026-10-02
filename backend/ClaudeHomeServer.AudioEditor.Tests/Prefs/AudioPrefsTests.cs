using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Prefs;

// Цепочка разрешения при запуске: настройки нити → префы режима → умолчание каталога. Запись
// префов на режим отдельная; новая нить получает последние префы своего режима
public sealed class AudioPrefsTests : IDisposable
{
    private const string Owner = "owner-1";

    private static readonly AudioEditScope Scope = new("project-1", null);

    private static readonly AudioModePrefs Catalog = new("tts", "local", "qwen", 1, new JsonObject { ["language"] = "ru", ["speed"] = 1.0 });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-prefs-" + Guid.NewGuid().ToString("N"));
    private readonly AudioPrefsStore _store;
    private readonly AudioPrefsService _prefs;

    public AudioPrefsTests()
    {
        _store = new AudioPrefsStore(_root);
        _prefs = new AudioPrefsService(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Файл_префов_лежит_по_владельцу_и_области()
    {
        _store.PathOf(Owner, AudioEditScope.Personal).Should().Be(Path.Combine(_root, Owner, "personal.json"));
        AudioPrefsStore.DirName.Should().Be("audio-editor-prefs");
    }

    [Fact]
    public void Без_нити_и_префов_берётся_умолчание_каталога()
    {
        var resolved = _prefs.Resolve(Owner, Scope, AudioModes.Voice, null, Catalog);

        resolved.Should().BeEquivalentTo(new AudioLaunchSettings(AudioModes.Voice, "tts", "local", "qwen", 1, []),
            o => o.Excluding(s => s.Fields));
        resolved.Fields.ToJsonString().Should().Be(Catalog.Fields!.ToJsonString());
    }

    [Fact]
    public void Префы_режима_перекрывают_каталог_а_настройки_нити_перекрывают_префы()
    {
        _prefs.Save(Owner, Scope, AudioModes.Voice,
            new AudioModePrefs(null, "fal", "minimax-speech", 3, new JsonObject { ["speed"] = 1.2, ["voice"] = "anna" }));
        var thread = new AudioThreadSettings(AudioModes.Voice, null, null, "elevenlabs",
            new JsonObject { ["voice"] = "boris", ["text"] = "привет" });

        var resolved = _prefs.Resolve(Owner, Scope, AudioModes.Voice, thread, Catalog);

        resolved.Operation.Should().Be("tts", "ни нить, ни префы операцию не задали — каталог");
        resolved.Provider.Should().Be("fal", "нить поставщика не задала — префы режима");
        resolved.Model.Should().Be("elevenlabs", "модель нити старше префов");
        resolved.Count.Should().Be(3, "число вариантов нить не задала — префы");
        resolved.Fields.ToJsonString().Should().Be(
            new JsonObject { ["language"] = "ru", ["speed"] = 1.2, ["voice"] = "boris", ["text"] = "привет" }.ToJsonString(),
            "поля сливаются по ключам: каталог ← префы ← нить");
    }

    [Fact]
    public void Число_вариантов_нити_старше_префов()
    {
        _prefs.Save(Owner, Scope, AudioModes.Music, new AudioModePrefs(null, null, null, 3, null));
        var thread = new AudioThreadSettings(AudioModes.Music, null, null, null, null, Count: 2);

        _prefs.Resolve(Owner, Scope, AudioModes.Music, thread, Catalog).Count.Should().Be(2);
    }

    [Fact]
    public void Настройки_нити_другого_режима_не_в_счёт()
    {
        _prefs.Save(Owner, Scope, AudioModes.Music, new AudioModePrefs("song", "local", "ace", 2, null));
        var voiceThread = new AudioThreadSettings(AudioModes.Voice, "tts", "fal", "minimax-speech", null, 4);

        var resolved = _prefs.Resolve(Owner, Scope, AudioModes.Music, voiceThread, Catalog);

        resolved.Provider.Should().Be("local");
        resolved.Model.Should().Be("ace");
        resolved.Count.Should().Be(2);
    }

    [Fact]
    public void Режимы_независимы_запись_одного_не_трогает_другой()
    {
        var voice = new AudioModePrefs("tts", "fal", "minimax-speech", 2, new JsonObject { ["voice"] = "anna" });
        _prefs.Save(Owner, Scope, AudioModes.Voice, voice);
        _prefs.Save(Owner, Scope, AudioModes.Music, new AudioModePrefs("song", "local", "ace", 4, null));

        var saved = _prefs.Get(Owner, Scope, AudioModes.Voice);
        saved.Should().BeEquivalentTo(voice, o => o.Excluding(p => p.Fields), "запись «Музыки» не затёрла «Голос»");
        saved!.Fields!.ToJsonString().Should().Be(voice.Fields!.ToJsonString());
        _prefs.Get(Owner, Scope, AudioModes.Process).Should().BeNull("«Обработку» никто не сохранял");
        _prefs.Resolve(Owner, Scope, AudioModes.Process, null, Catalog).Provider.Should().Be("local",
            "у режима без префов — умолчание каталога, а не префы соседнего режима");
        _prefs.Resolve(Owner, Scope, AudioModes.Voice, null, Catalog).Model.Should().Be("minimax-speech");
    }

    [Fact]
    public void Префы_областей_раздельны()
    {
        _prefs.Save(Owner, Scope, AudioModes.Voice, new AudioModePrefs(null, "fal", null, null, null));

        _prefs.Get(Owner, new AudioEditScope(AudioEditScope.Personal, null), AudioModes.Voice).Should().BeNull();
        _prefs.Get("owner-2", Scope, AudioModes.Voice).Should().BeNull();
    }

    [Fact]
    public void Новая_нить_берёт_последние_префы_своего_режима()
    {
        _prefs.Save(Owner, Scope, AudioModes.Music, new AudioModePrefs("song", "local", "ace", 2, new JsonObject { ["bpm"] = 120 }));
        _prefs.Save(Owner, Scope, AudioModes.Music, new AudioModePrefs("song", "local", "yue2", 3, new JsonObject { ["bpm"] = 90 }));
        _prefs.Save(Owner, Scope, AudioModes.Voice, new AudioModePrefs("tts", "fal", "minimax-speech", 1, null));

        var settings = _prefs.ForNewThread(Owner, Scope, AudioModes.Music);

        settings.Should().NotBeNull();
        settings!.Mode.Should().Be(AudioModes.Music);
        settings.Model.Should().Be("yue2", "последние префы режима, а не первые");
        settings.Count.Should().Be(3);
        settings.Fields!["bpm"]!.GetValue<int>().Should().Be(90);

        // Нить, заведённая с этими настройками, при запуске их и отдаёт
        var threads = new AudioThreadStore(Path.Combine(_root, "threads"));
        var thread = threads.Open(Owner, "chat-1", null, "", null, settings).Thread!;
        var resolved = _prefs.Resolve(Owner, Scope, AudioModes.Music, thread.Settings, Catalog);
        resolved.Should().BeEquivalentTo(new AudioLaunchSettings(AudioModes.Music, "song", "local", "yue2", 3, []),
            o => o.Excluding(s => s.Fields));
        resolved.Fields.ToJsonString().Should().Be(
            new JsonObject { ["language"] = "ru", ["speed"] = 1.0, ["bpm"] = 90 }.ToJsonString());
    }

    [Fact]
    public void Без_префов_режима_новая_нить_без_настроек()
    {
        _prefs.Save(Owner, Scope, AudioModes.Voice, new AudioModePrefs(null, "fal", null, null, null));

        _prefs.ForNewThread(Owner, Scope, AudioModes.Music).Should().BeNull();
    }

    [Fact]
    public void Поля_новой_нити_копия_префов()
    {
        _prefs.Save(Owner, Scope, AudioModes.Voice, new AudioModePrefs(null, null, null, null, new JsonObject { ["voice"] = "anna" }));

        var settings = _prefs.ForNewThread(Owner, Scope, AudioModes.Voice)!;
        settings.Fields!["voice"] = "boris";

        _prefs.Get(Owner, Scope, AudioModes.Voice)!.Fields!["voice"]!.GetValue<string>().Should().Be("anna");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Число_вариантов_вне_пределов_не_сохраняется(int count)
    {
        var act = () => _prefs.Save(Owner, Scope, AudioModes.Voice, new AudioModePrefs(null, null, null, count, null));

        act.Should().Throw<ArgumentException>();
        _prefs.Get(Owner, Scope, AudioModes.Voice).Should().BeNull();
    }

    [Fact]
    public void Неизвестный_режим_отказ()
    {
        var act = () => _prefs.Resolve(Owner, Scope, "video", null, Catalog);
        act.Should().Throw<ArgumentException>();
        var save = () => _prefs.Save(Owner, Scope, "video", AudioModePrefs.Empty);
        save.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Битый_файл_читается_как_отсутствие_префов()
    {
        var path = _store.PathOf(Owner, Scope.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ не json");

        _prefs.Get(Owner, Scope, AudioModes.Voice).Should().BeNull();
        _prefs.Resolve(Owner, Scope, AudioModes.Voice, null, Catalog).Provider.Should().Be("local");
    }

    [Fact]
    public void Область_с_разделителем_пути_отказ()
    {
        var act = () => _prefs.Get(Owner, new AudioEditScope("../x", null), AudioModes.Voice);
        act.Should().Throw<ArgumentException>();
    }
}
