using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Характеризационные тесты ДО выноса SessionStore (S3-1): HasSessionsOwnedBy держит guard
// смены среды исполнения (UsersController.Update → 409). Считаются все чаты владельца —
// проектные, личные и архивные; чужие не считаются.
public class SessionStoreOwnershipTests : SessionManagerTestBase
{
    private string NewOwner(string prefix) =>
        _userStore.Add(prefix + "-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user").Id;

    [Fact]
    public void HasSessionsOwnedBy_НетЧатов_False()
    {
        _sut.HasSessionsOwnedBy("nobody").Should().BeFalse();
    }

    [Fact]
    public async Task HasSessionsOwnedBy_ЧатПроектаВладельца_True_ЧужойВладелец_False()
    {
        var project = _projectManager.Create("OwnP", MkProjectDir("own-p"), TestUserId, TestUsername);
        await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.HasSessionsOwnedBy(TestUserId).Should().BeTrue("у проектной сессии владелец — владелец проекта");
        _sut.HasSessionsOwnedBy("another-user").Should().BeFalse();
    }

    [Fact]
    public async Task HasSessionsOwnedBy_ЛичныйЧатВнеПроекта_СчитаетсяТолькоВладельцу()
    {
        var chatOwner = NewOwner("chat-owner");
        await _sut.CreateChatAsync(chatOwner, ClaudeMode.Auto);

        _sut.HasSessionsOwnedBy(chatOwner).Should().BeTrue();
        _sut.HasSessionsOwnedBy(TestUserId).Should().BeFalse("чужой личный чат владельцу проекта не принадлежит");
    }

    [Fact]
    public async Task HasSessionsOwnedBy_АрхивныйЧат_ВсёРавноСчитается()
    {
        var archiver = NewOwner("archiver");
        var chat = await _sut.CreateChatAsync(archiver, ClaudeMode.Auto);
        _sut.SetArchived(chat.Id, true, "user");

        _sut.GetById(chat.Id)!.IsArchived.Should().BeTrue();
        _sut.HasSessionsOwnedBy(archiver).Should().BeTrue("среда фиксируется и при одних архивных чатах: транскрипты привязаны к путям среды");
    }

    [Fact]
    public async Task HasSessionsOwnedBy_УдалённыйЧат_ВладельцаБольшеНет()
    {
        var leaver = NewOwner("leaver");
        var chat = await _sut.CreateChatAsync(leaver, ClaudeMode.Auto);
        _sut.HasSessionsOwnedBy(leaver).Should().BeTrue();

        await _sut.DeleteAsync(chat.Id);

        _sut.HasSessionsOwnedBy(leaver).Should().BeFalse();
    }
}
