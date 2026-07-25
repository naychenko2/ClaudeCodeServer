namespace ClaudeHomeServer.Services.Backup;

// Настройки бэкапа — секция «Backup» в appsettings.Local.json. Правятся руками, через UI
// не редактируются: пути машинно-специфичны (у каждого своя облачная папка и свой диск под
// секреты), а хранить их в data/app-settings.json было бы вредно — восстановление уносит
// data целиком и откатило бы вместе с ней настройки собственного бэкапа.
//
// "Backup": {
//   "Enabled": true,
//   "Path": "D:/OneDrive/CCS-backups",     // пусто = {data}/backups
//   "IntervalHours": 24,
//   "SecretsPath": "E:/ccs-secrets"        // пусто = {папка exe}/backups-secrets
// }
public class BackupOptions
{
    public bool Enabled { get; init; }
    public string Path { get; init; } = "";
    public int IntervalHours { get; init; } = 24;
    public string SecretsPath { get; init; } = "";

    public static BackupOptions From(IConfiguration config)
    {
        var hours = config.GetValue<int?>("Backup:IntervalHours") ?? 24;
        return new BackupOptions
        {
            Enabled = config.GetValue<bool?>("Backup:Enabled") ?? false,
            Path = config["Backup:Path"] ?? "",
            IntervalHours = hours is > 0 and <= 720 ? hours : 24,
            SecretsPath = config["Backup:SecretsPath"] ?? "",
        };
    }
}
