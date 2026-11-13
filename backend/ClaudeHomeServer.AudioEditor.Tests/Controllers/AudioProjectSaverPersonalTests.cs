using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Controllers;

// ADR-040, Ф2-3: сохранение звука в «Личное» идёт в Personal/audio/ только новыми файлами: следующая версия
// черновика — audio.mp3, затем audio.v2.mp3; «Сохранить как» с занятым именем — name_taken; выход из корня
// и символическая ссылка — отказ
public sealed class AudioProjectSaverPersonalTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "aud-personal-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _source;
    private readonly AudioThread _draft = new("t1", null, [], "", DateTime.UtcNow);

    public AudioProjectSaverPersonalTests()
    {
        _root = Path.Combine(_base, "personal");
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_base, "work", "main.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllBytes(_source, "MP3"u8.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<AudioProjectSaver.Item> Items() => [new(AudioFileRoles.Main, _source)];

    private AudioProjectSaver.Outcome Save(string mode, string? folder = null, string? fileName = null)
    {
        PersonalMediaFolders.EnsureDirectory(_root, PersonalMediaFolders.Audio).Should().NotBeNull();
        return AudioProjectSaver.Save(_root, _draft, Items(), mode, folder, fileName, PersonalMediaFolders.Audio);
    }

    [Fact]
    public void Черновик_ложится_в_audio_а_повторное_сохранение_получает_v2()
    {
        var first = Save(AudioProjectSaver.NextVersion);
        var second = Save(AudioProjectSaver.NextVersion);

        first.Value!.Path.Should().Be("audio/audio.mp3");
        second.Value!.Path.Should().Be("audio/audio.v2.mp3", "перезаписи нет: занятое имя — следующий номер");
        File.ReadAllBytes(Path.Combine(_root, "audio", "audio.mp3")).Should().Equal("MP3"u8.ToArray());
    }

    [Fact]
    public void Сохранить_как_с_занятым_именем_409_с_подсказкой_и_байты_не_меняются()
    {
        Save(AudioProjectSaver.As, "audio", "intro");
        File.WriteAllBytes(Path.Combine(_root, "audio", "intro.mp3"), "OLD"u8.ToArray());

        var taken = Save(AudioProjectSaver.As, "audio", "intro");

        taken.ErrorCode.Should().Be(AudioEditErrorCodes.NameTaken);
        taken.Suggestion.Should().Be("audio/intro.v2.mp3");
        File.ReadAllBytes(Path.Combine(_root, "audio", "intro.mp3")).Should().Equal("OLD"u8.ToArray());
    }

    [Fact]
    public void Папка_вне_корня_Личного_отказ_и_ничего_не_пишется()
    {
        var outside = Save(AudioProjectSaver.As, "../outside", "x");

        outside.Value.Should().BeNull();
        outside.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        Directory.Exists(Path.Combine(_base, "outside")).Should().BeFalse();
    }

    [Fact]
    public void Папка_audio_как_символическая_ссылка_наружу_отказ()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "audio"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        PersonalMediaFolders.EnsureDirectory(_root, PersonalMediaFolders.Audio).Should().BeNull();
        var saved = AudioProjectSaver.Save(_root, _draft, Items(), AudioProjectSaver.NextVersion, null, null, PersonalMediaFolders.Audio);

        saved.Value.Should().BeNull();
        Directory.GetFiles(outside).Should().BeEmpty("ссылка не выпускает запись из корня «Личного»");
    }
}
