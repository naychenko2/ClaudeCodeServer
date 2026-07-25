using System.Diagnostics;
using System.IO.Compression;

namespace ClaudeHomeServer.Services.Backup;

// Режимы командной строки: exe --backup | --restore <zip> | --inspect <zip>.
//
// Зачем не только кнопка в вебе: бэкап нужен ровно тогда, когда приложение не работает —
// сторы обнулились, сервер не стартует, диск новый. Механизм восстановления, живущий
// внутри сервера, в этот момент недоступен. Поэтому ядро — здесь, а трей и API поверх.
public static class BackupCli
{
    public const string InspectionChildFlag = "--inspection-child";
    private const int DefaultInspectionPort = 5599;

    /// <summary>
    /// Обработать CLI-режим. true = режим отработал, приложению стартовать не надо.
    /// Вызывается ДО ProcessRegistry.Initialize: чистка «сирот» по pid-файлу тут
    /// не нужна и была бы вредна (убила бы процессы работающего сервера).
    /// </summary>
    public static bool TryHandle(string[] args)
    {
        if (args.Length == 0) return false;

        var mode = args[0];
        if (mode is not ("--backup" or "--restore" or "--inspect")) return false;

        try
        {
            return mode switch
            {
                "--backup" => RunBackup(),
                "--restore" => RunRestore(args),
                "--inspect" => RunInspect(args),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static BackupContext BuildContext(IConfiguration config) =>
        BackupContext.FromConfiguration(config);

    private static bool RunBackup()
    {
        var ctx = BuildContext(BuildConfiguration());
        Console.WriteLine($"Бэкап: {ctx.DataDir} → {ctx.BackupDir}");

        var result = BackupCore.Snapshot(ctx);
        if (result.Ok)
        {
            var s = result.Manifest!.Summary;
            Console.WriteLine($"Готово: {result.ArchivePath}");
            Console.WriteLine($"  {s.Chats} чатов · {s.Personas} персон · {s.Tasks} задач · " +
                              $"{s.Notes} заметок · {s.Projects} проектов");
            Console.WriteLine($"  секреты отдельно: {ctx.SecretsDir}");
        }
        else
        {
            Console.Error.WriteLine($"ОШИБКА: {result.Error}");
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static bool RunRestore(string[] args)
    {
        var archive = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(archive))
        {
            Console.Error.WriteLine("Укажи архив: --restore <файл.zip> [--secrets <файл.zip>] [--force]");
            Environment.ExitCode = 1;
            return true;
        }

        var secrets = ValueOf(args, "--secrets");
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        var ctx = BuildContext(BuildConfiguration());
        var result = BackupRestore.Restore(ctx, archive, secrets, force, Console.WriteLine);

        if (result.Ok)
        {
            Console.WriteLine();
            Console.WriteLine("Восстановлено. Что дальше:");
            Console.WriteLine("  1. Запусти сервер — карты баз знаний пересоберутся сами.");
            Console.WriteLine($"  2. Прежние данные лежат в {result.MovedDataTo} — удали, когда убедишься, что всё на месте.");
            if (string.IsNullOrWhiteSpace(secrets))
                Console.WriteLine("  3. Секреты не восстанавливались (ключ --secrets не задан) — текущие остались на месте.");
        }
        else
        {
            Console.Error.WriteLine($"ОТКАЗ: {result.Error}");
            Environment.ExitCode = 1;
        }
        return true;
    }

    // Временная копия инстанса для ручного переноса данных: поднимается на отдельном
    // порту рядом с боевым и обезврежена (см. InspectionMode в Program.cs).
    private static bool RunInspect(string[] args)
    {
        var archive = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
        {
            Console.Error.WriteLine("Укажи существующий архив: --inspect <файл.zip> [--port 5599]");
            Environment.ExitCode = 1;
            return true;
        }

        var port = int.TryParse(ValueOf(args, "--port"), out var parsed) ? parsed : DefaultInspectionPort;
        var ctx = BuildContext(BuildConfiguration());

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ctx.DataDir)) ?? ctx.DataDir;
        var root = Path.Combine(parent, $"inspect-{DateTime.Now:yyyyMMdd-HHmmss}");
        var dataDir = Path.Combine(root, "data");

        Console.WriteLine($"Распаковка в {dataDir}…");
        Directory.CreateDirectory(dataDir);
        ZipFile.ExtractToDirectory(archive, dataDir);

        var manifest = BackupCore.TryReadManifestFromArchive(archive);
        if (manifest is not null)
        {
            var s = manifest.Summary;
            Console.WriteLine($"Состав: {s.Chats} чатов · {s.Personas} персон · {s.Tasks} задач · {s.Notes} заметок");
            Console.WriteLine($"Снят: {manifest.CreatedAt:dd.MM.yyyy HH:mm} (окружение «{manifest.Environment}»)");
        }

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClaudeHomeServer.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(InspectionChildFlag);
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("--inspect-data");
        psi.ArgumentList.Add(Path.Combine(dataDir, "projects.json"));
        psi.ArgumentList.Add("--inspect-port");
        psi.ArgumentList.Add(port.ToString());

        // Окружение Production принесло бы Kestrel:Endpoints с боевыми 0.0.0.0:80/443 и
        // сертификатом — копия либо не поднялась бы, либо встала наружу вместо loopback.
        // Для «Inspection» файла appsettings нет, а в базовом appsettings.json секции
        // Kestrel не существует — значит адрес берётся из urls, который мы задаём сами.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Inspection";

        Console.WriteLine();
        Console.WriteLine($"Запускаю копию на http://127.0.0.1:{port}");
        Console.WriteLine("  Копия работает ТОЛЬКО на чтение: правки, ходы чатов и терминал отключены,");
        Console.WriteLine("  базы знаний и песочница не подключены, фоновые задачи не запускаются.");
        Console.WriteLine($"  Папку {root} удали руками, когда перенесёшь нужное.");
        Console.WriteLine();

        Process.Start(psi);
        return true;
    }

    private static string? ValueOf(string[] args, string key)
    {
        var index = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Override'ы для дочернего процесса инспекции — их надо положить ПОСЛЕДНИМ источником
    /// конфигурации. Командная строка тут не спасает: appsettings.Local.json подключается
    /// поверх неё и вернул бы боевые DataPath, Dify:ApiKey и корень песочницы.
    /// null — процесс запущен обычным образом.
    /// </summary>
    public static Dictionary<string, string?>? InspectionOverrides(string[] args)
    {
        if (!args.Contains(InspectionChildFlag, StringComparer.OrdinalIgnoreCase)) return null;

        var dataPath = ValueOf(args, "--inspect-data");
        var port = ValueOf(args, "--inspect-port") ?? DefaultInspectionPort.ToString();
        if (string.IsNullOrWhiteSpace(dataPath)) return null;

        return new Dictionary<string, string?>
        {
            ["DataPath"] = dataPath,
            ["InspectionMode"] = "true",
            // Общий Dify у dev и prod: живая копия начала бы «поправлять» боевые датасеты
            // под своё (заведомо отставшее) состояние
            ["Dify:ApiKey"] = "",
            // Иначе ленивый EnsureRunningAsync пересоздал бы боевой контейнер песочницы
            ["Sandbox:ProjectsRoot"] = "",
            ["urls"] = $"http://127.0.0.1:{port}",
        };
    }
}
