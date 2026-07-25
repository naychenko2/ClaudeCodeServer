using ClaudeHomeServer.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Настройки бэкапа читаются ТОЛЬКО из конфига (секция Backup в appsettings.Local.json).
// В data/app-settings.json они лежать не могут: восстановление уносит data целиком и
// откатило бы настройки собственного бэкапа вместе с данными.
public class BackupOptionsTests
{
    private static BackupOptions Build(params (string Key, string? Value)[] pairs) =>
        BackupOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build());

    [Fact]
    public void БезСекции_ВыключеноИСутки()
    {
        var options = Build();

        options.Enabled.Should().BeFalse();
        options.IntervalHours.Should().Be(24);
        options.Path.Should().BeEmpty();
    }

    [Fact]
    public void ЗначенияИзКонфига_Читаются()
    {
        var options = Build(
            ("Backup:Enabled", "true"),
            ("Backup:Path", @"D:\OneDrive\CCS"),
            ("Backup:IntervalHours", "6"),
            ("Backup:SecretsPath", @"E:\secrets"));

        options.Enabled.Should().BeTrue();
        options.Path.Should().Be(@"D:\OneDrive\CCS");
        options.IntervalHours.Should().Be(6);
        options.SecretsPath.Should().Be(@"E:\secrets");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000")]
    public void НегодныйИнтервал_ПадаетНаСутки(string value)
    {
        // Опечатка в конфиге не должна означать «снимать каждую секунду» или «никогда»
        Build(("Backup:IntervalHours", value)).IntervalHours.Should().Be(24);
    }

    [Fact]
    public void ПустойПуть_РезолвитсяВПапкуПоУмолчанию()
    {
        BackupPaths.ResolveBackupDir("", @"C:\app\data")
            .Should().Be(Path.Combine(@"C:\app\data", "backups"));

        // Секреты — рядом с exe, а НЕ в data: восстановление заменяет data целиком,
        // и архив секретов уехал бы в data.old вместе с ней
        BackupPaths.ResolveSecretsDir("", @"C:\app")
            .Should().Be(Path.Combine(@"C:\app", "backups-secrets"));
    }
}
