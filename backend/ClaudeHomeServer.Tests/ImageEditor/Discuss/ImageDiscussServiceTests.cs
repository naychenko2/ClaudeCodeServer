using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Discuss;

// Сервис «Обсудить с Claude» без HTTP-обвязки (ADR-017, раздел 6): отказы до создания
// чата, выбор собеседника, текст про персонажа, переиспользование чата сеанса только
// своего владельца и проекта. Ход уходит в стаб LLM тестового хоста.
public class ImageDiscussServiceTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly TestWebApplicationFactory _factory = new();
    private readonly SessionManager _sessions;
    private readonly string _ownerId;
    private readonly Project _project;

    public ImageDiscussServiceTests()
    {
        _sessions = _factory.Services.GetRequiredService<SessionManager>();
        _ownerId = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var (projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _project = Projects.GetById(projectId)!;
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();

    private ImageDiscussService Service() =>
        ActivatorUtilities.CreateInstance<ImageDiscussService>(_factory.Services);

    private static ImageDiscussInput Input(string text, byte[]? annotated = null, string? source = null,
        ImageDiscussCharacter? character = null, string? sessionId = null)
    {
        // Сигнатуру размеченной копии определяет ручка модуля, сервис получает готовое расширение
        var bytes = annotated ?? Png;
        return new(text, bytes, ImageFormatSniffer.DetectExtension(bytes), source, character, sessionId);
    }

    private Task<ImageDiscussOutcome> Start(ImageDiscussInput input, string? ownerId = null) =>
        Service().StartAsync(ownerId ?? _ownerId, _project, input, CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    public async Task Пустой_текст_отказ_без_чата(string text)
    {
        var result = await Start(Input(text));

        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Напишите, что обсудить");
        _sessions.GetByProject(_project.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Текст_длиннее_предела_отказ_а_ровно_предел_проходит()
    {
        var tooLong = await Start(Input(new string('а', ImageDiscussService.MaxTextChars + 1)));
        tooLong.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        _sessions.GetByProject(_project.Id).Should().BeEmpty();

        var atLimit = await Start(Input(new string('а', ImageDiscussService.MaxTextChars)));
        atLimit.Value.Should().NotBeNull(atLimit.Error);
    }

    [Fact]
    public async Task Размеченная_копия_не_картинка_отказ_без_чата_и_без_вложения()
    {
        var result = await Start(Input("что здесь?", annotated: "<html>не картинка</html>"u8.ToArray()));

        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("картинкой");
        _sessions.GetByProject(_project.Id).Should().BeEmpty();
        Directory.Exists(Path.Combine(_project.RootPath, FileService.AttachmentsDir)).Should().BeFalse();
    }

    [Fact]
    public async Task Без_исходника_один_вложенный_файл_и_имя_чата_по_умолчанию()
    {
        var result = await Start(Input("  как сделать светлее?  "));

        var dto = result.Value!;
        dto.Created.Should().BeTrue();
        dto.Attachments.Should().ContainSingle()
            .Which.Should().StartWith(FileService.AttachmentsDir + "/").And.EndWith("/annotated.png");
        File.ReadAllBytes(Path.Combine(_project.RootPath, dto.Attachments[0])).Should().Equal(Png);

        var session = _sessions.GetById(dto.SessionId)!;
        session.Name.Should().Be("Картинка");
        session.PersonaId.Should().NotBeNullOrEmpty("новый чат человека — только с персоной");
        var message = (await _sessions.GetHistoryAsync(dto.SessionId)).OfType<StoredUserMessage>().Should().ContainSingle().Subject;
        message.Text.Should().Be("как сделать светлее?", "текст уходит обрезанным по краям");
    }

    [Fact]
    public async Task Персонаж_уходит_путём_папки_в_тексте_а_не_вложением()
    {
        var result = await Start(Input("поставь её в парк", character: new ImageDiscussCharacter("Аня", "characters/anya")));

        var dto = result.Value!;
        dto.Attachments.Should().ContainSingle("папка персонажа вложением не отдаётся");
        var message = (await _sessions.GetHistoryAsync(dto.SessionId)).OfType<StoredUserMessage>().Single();
        message.Text.Should().StartWith("поставь её в парк")
            .And.Contain("Персонаж «Аня»: папка characters/anya/");
    }

    [Fact]
    public async Task Собеседник_руководитель_проекта_если_он_назначен()
    {
        var lead = _factory.Services.GetRequiredService<PersonaManager>().Create(_ownerId, "Алекс", "Тимлид",
            null, null, null, null, PersonaScope.Project, _project.Id, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(_project.Id, lead.Id);

        var result = await Start(Input("что поправить?"));

        _sessions.GetById(result.Value!.SessionId)!.PersonaId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task Руководитель_чужого_владельца_не_берётся()
    {
        var foreign = _factory.Services.GetRequiredService<PersonaManager>().Create("someone-else", "Чужой", null,
            null, null, null, null, PersonaScope.Global, null, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(_project.Id, foreign.Id);

        var result = await Start(Input("что поправить?"));

        _sessions.GetById(result.Value!.SessionId)!.PersonaId.Should().NotBe(foreign.Id)
            .And.NotBeNullOrEmpty("вместо чужого руководителя — личный ассистент владельца");
    }

    [Fact]
    public async Task Чужой_чат_не_переиспользуется_а_без_собеседника_отказ_Unavailable()
    {
        var ownChat = (await Start(Input("мой вопрос"))).Value!.SessionId;

        // Незнакомый владелец присылает id чужого чата: переиспользования нет, а подобрать
        // ему собеседника нечем — честный отказ, а не запись в чужой чат
        var result = await Start(Input("вопрос", sessionId: ownChat), ownerId: "ghost-user");

        result.ErrorCode.Should().Be(ImageEditErrorCodes.Unavailable);
        _sessions.GetByProject(_project.Id).Should().ContainSingle();
        (await _sessions.GetHistoryAsync(ownChat)).OfType<StoredUserMessage>()
            .Should().ContainSingle("в чужой чат ничего не дописано");
    }

    [Fact]
    public async Task Несуществующий_id_чата_даёт_новый_а_свой_переиспользуется()
    {
        var fresh = (await Start(Input("раз", sessionId: "no-such-session"))).Value!;
        fresh.Created.Should().BeTrue();

        var again = (await Start(Input("два", sessionId: fresh.SessionId))).Value!;
        again.SessionId.Should().Be(fresh.SessionId);
        again.Created.Should().BeFalse();
        _sessions.GetByProject(_project.Id).Should().ContainSingle();
    }
}
