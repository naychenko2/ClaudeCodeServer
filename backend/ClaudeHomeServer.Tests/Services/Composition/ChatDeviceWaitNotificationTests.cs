using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// S2d-4f (дефект e127cef0, правило И.2): уведомление о снятой очереди приватного чата
// уходит его автору, а не владельцу проекта — иначе по потолку DeviceWaitCeiling владелец
// получает push с именем и id чужого приватного чата, который он не видит.
// Общий чат — прежний адресат (владелец): это покрыто ОчередьЧата_ПотолокИстёк_…
// в TaskExecutionServiceDeviceWaitTests; здесь — приватный случай и fail-closed.
public class ChatDeviceWaitNotificationTests : IDisposable
{
    private readonly DeviceWaitTestHost H = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        H.Dispose();
    }

    // Чат на офлайн-устройстве, приватный редактору-участнику проекта
    private async Task<(Session Chat, User Owner, string AuthorId)> PrivateWaitingChatAsync()
    {
        var (owner, project) = H.LocalProject();
        var editor = H._userStore.Add("editor-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        H._projects.SetMembers(project.Id, [new ProjectMember { UserId = editor.Id, Role = ProjectRole.Editor, AddedBy = owner.Id }]);
        var chat = await H._sessions.CreateAsync(project.Id, ClaudeMode.AcceptEdits, name: "Приватный чат редактора");
        // Приватность — на живом объекте каталога (тот же приём, что PrivateChatBroadcastLeakTests)
        H._sessions.GetById(chat.Id)!.PrivateToUserId = editor.Id;
        return (H._sessions.GetById(chat.Id)!, owner, editor.Id);
    }

    private ChatDeviceWaitHandler Handler() =>
        new(H._sessions, H._projectSeam, H._gate, H._notif, NullLogger<ChatDeviceWaitHandler>.Instance);

    [Fact]
    public async Task ОчередьЧата_ПриватныйЧатПотолокИстёк_УведомлениеТолькоАвтору()
    {
        var (chat, owner, authorId) = await PrivateWaitingChatAsync();
        await H._sessions.SendMessageAndWaitAsync(chat.Id, "Отчёт исполнителя", TimeSpan.Zero, agentDepth: 1);

        await Handler().SweepAsync(DateTime.UtcNow + ProjectCapabilities.DeviceWaitCeiling + TimeSpan.FromMinutes(1));

        (await H.NotificationTitlesAsync(authorId)).Should().Contain("Сообщения не доставлены",
            "свои ждущие сообщения знает и снимает их автор приватного чата");
        (await H.NotificationTitlesAsync(owner.Id)).Should().BeEmpty(
            "владельцу проекта не уходят ни имя, ни id чужого приватного чата");
    }

    [Fact]
    public async Task ОчередьЧата_ПриватностьПустая_FailClosed_УведомленияНетНикому()
    {
        var (chat, owner, _) = await PrivateWaitingChatAsync();
        // Мусорное значение поля — «не видит никто» (как SessionAccess.CanSee): адресата нет
        H._sessions.GetById(chat.Id)!.PrivateToUserId = "";
        await H._sessions.SendMessageAndWaitAsync(chat.Id, "Отчёт исполнителя", TimeSpan.Zero, agentDepth: 1);

        await Handler().SweepAsync(DateTime.UtcNow + ProjectCapabilities.DeviceWaitCeiling + TimeSpan.FromMinutes(1));

        (await H.NotificationTitlesAsync(chat.PrivateToUserId!)).Should().BeEmpty("пустая строка — не адресат");
        (await H.NotificationTitlesAsync(owner.Id)).Should().BeEmpty("и не владелец проекта");
    }
}
