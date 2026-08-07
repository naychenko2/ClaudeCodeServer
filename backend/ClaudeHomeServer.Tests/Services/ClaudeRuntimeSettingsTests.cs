using System.Text.Json;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Файл --settings хода: выключение хуков и гейт плагина браузера. Проверяем содержимое
// файла, а не запуск CLI: неверный ключ настроек CLI молча проглотит, и плагин поедет
// в контекст всем персонам подряд (ровно то, ради чего гейт и делался).
public class ClaudeRuntimeSettingsTests
{
    private static (string Path, JsonElement Json) Settings(bool browserEnabled)
    {
        var args = ClaudeRuntimeSettings
            .HooksOffArgs(LocalProcessRunner.Instance, browserEnabled).ToList();
        args[0].Should().Be("--settings");
        var path = args[1];
        return (path, JsonDocument.Parse(File.ReadAllText(path)).RootElement);
    }

    [Fact]
    public void ХукиВыключеныВОбоихРежимах()
    {
        foreach (var browser in new[] { true, false })
            Settings(browser).Json.GetProperty("disableAllHooks").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void БраузерРазрешён_ПлагинPlaywrightВключенЯвно()
    {
        // Явная запись true нужна, даже когда доступ разрешён по умолчанию: иначе синк
        // settings.json профиля (LlmProviderRegistry.SyncUserProfile) может затереть
        // enabledPlugins хостовым {} и браузер пропадёт молча
        var plugins = Settings(browserEnabled: true).Json.GetProperty("enabledPlugins");
        plugins.GetProperty("playwright@claude-plugins-official").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void БраузерЗапрещён_ПлагинPlaywrightВыключен()
    {
        var plugins = Settings(browserEnabled: false).Json.GetProperty("enabledPlugins");
        plugins.GetProperty("playwright@claude-plugins-official").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void РежимыЖивутВРазныхФайлах()
    {
        // Иначе один режим переписывал бы файл другого, а сигнатура прогона (в неё входит
        // путь --settings) не отличала бы сессию с браузером от сессии без него
        Settings(browserEnabled: true).Path.Should().NotBe(Settings(browserEnabled: false).Path);
    }
}
