using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Версия сессий (User.TokenVersion): растёт при любой смене пароля, по ней JwtService
/// отвергает токены, выданные до неё. Проверяем сам счётчик и его сверку.
/// </summary>
public class UserStoreTokenVersionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _sut;

    public UserStoreTokenVersionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "userstore_tv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = CreateStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore CreateStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build(),
        new Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    [Fact]
    public void ChangePassword_IncrementsVersion()
    {
        var user = _sut.Add("alice", "password-1", "user");
        var before = user.TokenVersion;

        _sut.ChangePassword(user.Id, "password-1", "password-2").Should().BeTrue();

        user.TokenVersion.Should().Be(before + 1);
    }

    [Fact]
    public void ChangePassword_WrongCurrent_KeepsVersion()
    {
        var user = _sut.Add("alice", "password-1", "user");
        var before = user.TokenVersion;

        _sut.ChangePassword(user.Id, "не-тот-пароль", "password-2").Should().BeFalse();

        user.TokenVersion.Should().Be(before);
    }

    [Fact]
    public void ResetPassword_IncrementsVersion()
    {
        // Админский сброс идёт мимо SetPasswordInternal — версия должна расти и там
        var user = _sut.Add("alice", "password-1", "user");
        var before = user.TokenVersion;

        _sut.ResetPassword(user.Id, "reset-password").Should().BeTrue();

        user.TokenVersion.Should().Be(before + 1);
    }

    [Fact]
    public void SetPassword_IncrementsVersion()
    {
        var user = _sut.Add("alice", "password-1", "user");
        var before = user.TokenVersion;

        _sut.SetPassword(user, "password-3");

        user.TokenVersion.Should().Be(before + 1);
    }

    [Fact]
    public void IsTokenVersionCurrent_MatchesOnlyCurrentVersion()
    {
        var user = _sut.Add("alice", "password-1", "user");
        var issued = user.TokenVersion;

        _sut.IsTokenVersionCurrent(user.Id, issued).Should().BeTrue();

        _sut.ChangePassword(user.Id, "password-1", "password-2").Should().BeTrue();

        _sut.IsTokenVersionCurrent(user.Id, issued).Should().BeFalse();
        _sut.IsTokenVersionCurrent(user.Id, user.TokenVersion).Should().BeTrue();
    }

    [Fact]
    public void IsTokenVersionCurrent_UnknownUser_False()
    {
        _sut.IsTokenVersionCurrent("нет-такого", 0).Should().BeFalse();
        _sut.IsTokenVersionCurrent("нет-такого", 1).Should().BeFalse();
    }

    [Fact]
    public void Version_SurvivesRestart()
    {
        // «Рестарт сервера»: новый стор читает тот же users.json — отозванные токены
        // не должны воскресать после перезапуска
        var user = _sut.Add("alice", "password-1", "user");
        _sut.ChangePassword(user.Id, "password-1", "password-2").Should().BeTrue();
        var current = user.TokenVersion;

        var restarted = CreateStore();

        restarted.IsTokenVersionCurrent(user.Id, current).Should().BeTrue();
        restarted.IsTokenVersionCurrent(user.Id, current - 1).Should().BeFalse();
    }
}
