using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Настройка инстанса «присылать утренний бриф» (AppSettings.DailyBriefingEnabled):
// по умолчанию включена — в том числе у app-settings.json, записанного до её появления, —
// и переживает перезапуск. По ней гейтится автозапуск в DailyBriefingService.
public class AppSettingsBriefingTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsBriefingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "appsettings_brief_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private AppSettingsService BuildService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new AppSettingsService(config);
    }

    // Файл, записанный до появления настройки: поля DailyBriefingEnabled в нём нет
    private void WriteLegacySettingsFile() =>
        File.WriteAllText(Path.Combine(_tempDir, "app-settings.json"), """{"ClaudeBilling":"api"}""");

    [Fact]
    public void Бриф_включен_если_настройки_еще_не_было()
    {
        BuildService().Get().DailyBriefingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Бриф_включен_в_файле_записанном_до_появления_настройки()
    {
        WriteLegacySettingsFile();

        BuildService().Get().DailyBriefingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Выключение_брифа_переживает_перезапуск()
    {
        BuildService().Save(new AppSettings { ClaudeBilling = "api", DailyBriefingEnabled = false });

        BuildService().Get().DailyBriefingEnabled.Should().BeFalse();
    }

    // Настройки правятся из разных экранов, каждый знает только про своё поле. Патч-семантика:
    // не присланное (null) поле остаётся прежним — иначе тумблер брифа откатывал бы ClaudeBilling
    // своим устаревшим снимком (ровно так и ловилось до правки).
    [Fact]
    public void Патч_одного_поля_не_трогает_соседнее()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { ClaudeBilling = "api" });

        svc.Save(new AppSettings { DailyBriefingEnabled = false });

        var saved = svc.Get();
        saved.ClaudeBilling.Should().Be("api");
        saved.DailyBriefingEnabled.Should().BeFalse();
    }

    [Fact]
    public void Патч_соседнего_поля_не_включает_выключенный_бриф()
    {
        var svc = BuildService();
        svc.Save(new AppSettings { DailyBriefingEnabled = false });

        svc.Save(new AppSettings { ClaudeBilling = "api" });

        svc.Get().DailyBriefingEnabled.Should().BeFalse();
    }
}
