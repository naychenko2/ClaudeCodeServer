using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Чем озвучивать текст: единственная точка склейки голоса. Здесь же проверяется главный
// принцип — отказов не бывает: любое кривое значение вырождается в дефолт, потому что
// ошибка синтеза уводит на голос браузера ОСТАТОК фразы, а не одну интонацию.
public class VoiceResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-voice-" + Guid.NewGuid().ToString("N"));
    private readonly PersonaManager _personas;

    public VoiceResolverTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PersonasPath"] = Path.Combine(_dir, "personas.json"),
        }).Build();
        _personas = new PersonaManager(config, NullLogger<PersonaManager>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка — не критично */ }
        GC.SuppressFinalize(this);
    }

    private VoiceResolver Make(string? configuredVoice = null, double? configuredSpeed = null)
    {
        var values = new Dictionary<string, string?>();
        if (configuredVoice is not null) values["Yandex:SpeechKit:Voice"] = configuredVoice;
        if (configuredSpeed is not null)
            values["Yandex:SpeechKit:Speed"] = configuredSpeed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new VoiceResolver(_personas, config, NullLogger<VoiceResolver>.Instance);
    }

    private Persona PersonaWith(string ownerId, PersonaVoice? voice)
    {
        var persona = _personas.Create(ownerId, "Тест", role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);
        return voice is null ? persona : _personas.SetVoice(persona.Id, ownerId, voice);
    }

    [Fact]
    public void БезПерсоны_ГолосИнстанса()
    {
        var choice = Make("jane", 1.2).Resolve(null, "user-1");

        choice.Voice.Should().Be("jane");
        choice.Role.Should().BeNull();
        choice.Speed.Should().Be(1.2);
    }

    [Fact]
    public void ГолосПерсоны_Перебиваетконфиг()
    {
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "masha", Role = "strict", Speed = 1.1 });

        var choice = Make("jane").Resolve(persona.Id, "user-1");

        choice.Should().BeEquivalentTo(new VoiceChoice("masha", "strict", 1.1));
    }

    [Fact]
    public void ПерсонаБезГолоса_ЗвучитКакРаньше()
    {
        // Регресс: пока голос не выбран, персона обязана звучать голосом инстанса
        var persona = PersonaWith("user-1", null);

        Make("ermil").Resolve(persona.Id, "user-1").Voice.Should().Be("ermil");
    }

    [Fact]
    public void ЧужаяПерсона_ГолосНеОтдаётся()
    {
        // personaId приходит от клиента: чужой id не должен раскрывать голос чужой персоны,
        // но и ошибкой быть не может — иначе устаревшая вкладка теряет кусок озвучки
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "masha" });

        Make("zahar").Resolve(persona.Id, "user-2").Voice.Should().Be("zahar");
    }

    [Fact]
    public void НесуществующаяПерсона_Дефолт()
    {
        Make("zahar").Resolve("нет-такой-персоны", "user-1").Voice.Should().Be("zahar");
    }

    [Fact]
    public void НезнакомыйГолосПерсоны_Дефолт()
    {
        // Голос переименовали в SpeechKit или json правили руками — озвучка обязана жить
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "зорро" });

        Make("zahar").Resolve(persona.Id, "user-1").Voice.Should().Be("zahar");
    }

    [Fact]
    public void РольНеПоддержаннаяГолосом_Сбрасывается()
    {
        // filipp не умеет амплуа вовсе: с ролью в hints SpeechKit ответил бы 400
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "filipp", Role = "strict" });

        var choice = Make().Resolve(persona.Id, "user-1");

        choice.Voice.Should().Be("filipp");
        choice.Role.Should().BeNull();
    }

    [Fact]
    public void РольОтДругогоГолоса_Сбрасывается()
    {
        // whisper есть у marina, но не у kirill
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "kirill", Role = "whisper" });

        Make().Resolve(persona.Id, "user-1").Role.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0.1)]      // «не задано» нулём: у SpeechKit это 400, а не дефолт
    [InlineData(-2, 0.1)]
    [InlineData(99, 3.0)]
    [InlineData(1.4, 1.4)]
    public void Темп_ЗажимаетсяВГраницы(double stored, double expected)
    {
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "jane", Speed = stored });

        Make().Resolve(persona.Id, "user-1").Speed.Should().Be(expected);
    }

    [Fact]
    public void НезнакомыйГолосВКонфиге_Дефолт()
    {
        Make("зорро").Resolve(null, "user-1").Voice.Should().Be(TtsVoiceCatalog.Default);
    }

    // --- Прослушивание в форме: примеряемый голос сильнее сохранённого ---

    [Fact]
    public void ЯвныйГолос_СильнееГолосаПерсоны()
    {
        // Кнопка «Послушать» играет тем, что человек выбирает СЕЙЧАС, а не сохранённым
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "masha", Speed = 1.1 });

        var choice = Make("zahar").Resolve(persona.Id, "user-1",
            new PersonaVoice { Voice = "julia", Role = "strict", Speed = 0.9 });

        choice.Should().BeEquivalentTo(new VoiceChoice("julia", "strict", 0.9));
    }

    [Fact]
    public void ЯвныйГолос_ПроходитТеЖеПроверки()
    {
        // Ветка примерки идёт через тот же резолв: амплуа не от того голоса сбрасывается,
        // темп зажимается — иначе превью падало бы там, где сохранение работает
        var choice = Make().Resolve(null, "user-1",
            new PersonaVoice { Voice = "filipp", Role = "strict", Speed = 99 });

        choice.Should().BeEquivalentTo(new VoiceChoice("filipp", null, 3.0));
    }

    [Fact]
    public void ПустаяПримерка_НеМешаетГолосуПерсоны()
    {
        var persona = PersonaWith("user-1", new PersonaVoice { Voice = "masha" });

        Make().Resolve(persona.Id, "user-1", new PersonaVoice()).Voice.Should().Be("masha");
    }
}
