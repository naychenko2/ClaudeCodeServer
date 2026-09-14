namespace ClaudeHomeServer.Services.Llm;

// CLI-команда `--profile-sync-dry-run` (ADR-015 §8 этап 2): прогон триггера нормализации
// без поднятия веб-приложения. Используется для разовой проверки, что синк удалил бы
// в каждом профиле, и для разового усыновления (при первом запуске после переключения
// рубильника в dryRun). По образцу BackupCli — работает ДО поднятия DI и SignalR.
//
// Конфиг читается из appsettings*.json (как веб-приложение), но без бутстрапа хоста.
// Рубильник `Claude:ProfileSync:Mirror` можно выставить через env-переменную
// `Claude__ProfileSync__Mirror=dryRun` без правки конфигов.
public static class ProfileSyncCli
{
    public static bool TryHandle(string[] args)
    {
        if (args.Length == 0) return false;
        if (args[0] != "--profile-sync-dry-run") return false;

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        try
        {
            var registry = new LlmProviderRegistry(config);
            var mode = config.GetValue("Claude:ProfileSync:Mirror", ProfileMirrorMode.Off);
            Console.WriteLine($"[ProfileSyncCli] Режим зеркала: {mode}");
            if (mode == ProfileMirrorMode.Off)
            {
                Console.WriteLine("[ProfileSyncCli] Режим off — будет только усыновление (без расчёта удалений). " +
                                  "Для расчёта установите Claude__ProfileSync__Mirror=dryRun.");
            }

            var reports = registry.NormalizeAll();

            Console.WriteLine();
            Console.WriteLine($"Обработано профилей: {reports.Count}");
            int totalWouldDelete = 0;
            int totalDivergent = 0;
            foreach (var r in reports)
            {
                totalWouldDelete += r.WouldDelete.Count;
                totalDivergent += r.Divergent.Count;
            }
            Console.WriteLine($"Всего: wouldDelete={totalWouldDelete} divergent={totalDivergent}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
            Environment.ExitCode = 1;
            return true;
        }
    }
}