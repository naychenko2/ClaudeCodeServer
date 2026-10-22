using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Стартовая миграция режима: чаты с персоной из Auto в AcceptEdits
public class PersonaChatModeMigrationTests
{
    private static Session Chat(ClaudeMode mode, string? personaId = "p1") =>
        new() { Mode = mode, PersonaId = personaId, UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Персона_вAuto_становитсяAcceptEdits_UpdatedAtНеДвигается()
    {
        var s = Chat(ClaudeMode.Auto);
        var before = s.UpdatedAt;

        SessionManager.MigratePersonaChatMode([s]).Should().Be(1);

        s.Mode.Should().Be(ClaudeMode.AcceptEdits);
        s.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public void ПовторныйПрогон_НичегоНеМеняет()
    {
        var s = Chat(ClaudeMode.Auto);
        SessionManager.MigratePersonaChatMode([s]);
        SessionManager.MigratePersonaChatMode([s]).Should().Be(0);
    }

    [Fact]
    public void БезПерсоны_вAuto_НеМеняется()
    {
        var s = Chat(ClaudeMode.Auto, personaId: null);
        SessionManager.MigratePersonaChatMode([s]).Should().Be(0);
        s.Mode.Should().Be(ClaudeMode.Auto);
    }

    [Fact]
    public void Персона_вPlan_НеМеняется()
    {
        var s = Chat(ClaudeMode.Plan);
        SessionManager.MigratePersonaChatMode([s]).Should().Be(0);
        s.Mode.Should().Be(ClaudeMode.Plan);
    }

    [Fact]
    public void ГрупповойЧат_вAuto_НеМеняется()
    {
        var s = Chat(ClaudeMode.Auto);
        s.Participants = ["p1", "p2"];
        SessionManager.MigratePersonaChatMode([s]).Should().Be(0);
        s.Mode.Should().Be(ClaudeMode.Auto);
    }

    [Fact]
    public void ЧатШтаба_сSavedMode_НеМеняется()
    {
        var s = Chat(ClaudeMode.Auto);
        s.TeamImplement = new SessionTeamImplement { SavedMode = ClaudeMode.AcceptEdits };
        SessionManager.MigratePersonaChatMode([s]).Should().Be(0);
        s.Mode.Should().Be(ClaudeMode.Auto);
    }
}
