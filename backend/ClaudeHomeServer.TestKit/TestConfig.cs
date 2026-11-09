using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Конфиг юнит-тестов с изоляцией профиля CLI. Без ClaudeUserProfileDir реестр
/// провайдеров берёт реальный ~/.claude разработчика и синкает его (13k файлов,
/// ~95 MB, в основном plugins/) в temp-профиль на каждый BuildCliEnv/BuildOAuthCliEnv —
/// по диагностике 2026-07-30 это было ~150 с честного времени прогона. Хелпер
/// подставляет пустую temp-папку, чтобы новый тест не забыл про оверрайд.
/// </summary>
public static class TestConfig
{
    /// <summary>
    /// Собрать IConfiguration из словаря, догрузив безопасные тестовые дефолты.
    /// Явный ClaudeUserProfileDir из settings (проверки самого синка) не трогает.
    /// </summary>
    public static IConfiguration Build(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings.TryAdd("ClaudeUserProfileDir", EmptyClaudeProfileDir());
        // Фоновый автосейв SessionManager в юнит-тестах не нужен: внутри SaveSessions живёт
        // sweep-terminus, и его срабатывание по таймеру — источник плавающих падений (тест
        // проверил статус, фон его поменял). Тесты, которым sweep нужен, зовут его явно.
        settings.TryAdd("Session:AutoSaveSeconds", "0");
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// Пустая папка «пользовательского профиля CLI» на весь процесс testhost:
    /// SyncUserProfile по ней проходит мгновенно (копировать нечего). Одна на прогон,
    /// не удаляем — приберёт ОС.
    /// </summary>
    public static string EmptyClaudeProfileDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs_tests_empty_claude_profile");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
