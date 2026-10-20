using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.TurnAutoResume;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.TurnAutoResume;

/// <summary>
/// Тесты чистой функции отбора TurnAutoResumeEligibility.Check:
/// по одному тесту на каждый код исключения + happy path.
/// </summary>
public class TurnAutoResumeEligibilityTests
{
    // Базовая сессия, проходящая все проверки (happy path).
    private static Session MakeEligibleSession() => new()
    {
        RestartResumeAttempts = 0,
        TeamImplement = null,
        Participants = null,
        ArchivedAt = null,
        UpdatedAt = DateTime.UtcNow,
    };

    // Серверный проект (DeviceId = null) — deviceOnline не влияет.
    private static Project MakeServerProject() => new() { DeviceId = null };

    // Локальный проект (DeviceId != null).
    private static Project MakeLocalProject() => new() { DeviceId = "device-1" };

    [Fact]
    public void HappyPath_AllChecksPass_ReturnsEligible()
    {
        var session = MakeEligibleSession();
        var project = MakeServerProject();

        var result = TurnAutoResumeEligibility.Check(session, project, deviceOnline: true, lastTurnWasDeployOrRollback: false);

        Assert.True(result.Eligible);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void AlreadyAttempted_Excludes()
    {
        var session = MakeEligibleSession();
        session.RestartResumeAttempts = 1;

        var result = TurnAutoResumeEligibility.Check(session, MakeServerProject(), true, false);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonAlreadyAttempted, result.Reason);
    }

    [Fact]
    public void TeamImplement_Excludes()
    {
        var session = MakeEligibleSession();
        session.TeamImplement = new SessionTeamImplement();

        var result = TurnAutoResumeEligibility.Check(session, MakeServerProject(), true, false);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonTeamImplement, result.Reason);
    }

    [Fact]
    public void GroupChat_Excludes()
    {
        var session = MakeEligibleSession();
        session.Participants = new List<string> { "persona-1", "persona-2" };

        var result = TurnAutoResumeEligibility.Check(session, MakeServerProject(), true, false);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonGroupChat, result.Reason);
    }

    [Fact]
    public void LocalProject_DeviceOffline_Excludes()
    {
        var session = MakeEligibleSession();
        var project = MakeLocalProject();

        var result = TurnAutoResumeEligibility.Check(session, project, deviceOnline: false, lastTurnWasDeployOrRollback: false);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonDeviceOffline, result.Reason);
    }

    [Fact]
    public void LocalProject_DeviceOnline_Passes()
    {
        var session = MakeEligibleSession();
        var project = MakeLocalProject();

        var result = TurnAutoResumeEligibility.Check(session, project, deviceOnline: true, lastTurnWasDeployOrRollback: false);

        Assert.True(result.Eligible);
    }

    [Fact]
    public void DeployLoop_Excludes()
    {
        var session = MakeEligibleSession();
        var project = MakeServerProject();

        var result = TurnAutoResumeEligibility.Check(session, project, true, lastTurnWasDeployOrRollback: true);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonDeployLoop, result.Reason);
    }

    [Fact]
    public void Archived_Excludes()
    {
        var session = MakeEligibleSession();
        // Архив: ArchivedAt задан, UpdatedAt <= ArchivedAt.
        var archivedAt = DateTime.UtcNow.AddHours(-1);
        session.ArchivedAt = archivedAt;
        session.UpdatedAt = archivedAt;

        var result = TurnAutoResumeEligibility.Check(session, MakeServerProject(), true, false);

        Assert.False(result.Eligible);
        Assert.Equal(TurnAutoResumeEligibility.ReasonArchived, result.Reason);
    }

    [Fact]
    public void ServerProject_DeviceOffline_Irrelevant()
    {
        // Серверный проект (DeviceId = null): deviceOnline не влияет на результат.
        var session = MakeEligibleSession();
        var project = MakeServerProject();

        var result = TurnAutoResumeEligibility.Check(session, project, deviceOnline: false, lastTurnWasDeployOrRollback: false);

        Assert.True(result.Eligible);
    }

    [Fact]
    public void NullProject_TreatedAsServer_Passes()
    {
        // Чат вне проекта (project = null) — считается серверным.
        var session = MakeEligibleSession();

        var result = TurnAutoResumeEligibility.Check(session, project: null, deviceOnline: false, lastTurnWasDeployOrRollback: false);

        Assert.True(result.Eligible);
    }
}
