namespace ClaudeHomeServer.Services;

// Единый реестр имён файлов секретов инстанса: jwt-secret.txt, vapid-keys.json,
// module-keys.json, mcp-secrets.json. Эти файлы никогда не попадают в основной
// архив `data/`, а уезжают в отдельный локальный архив секретов (BACKUP-restrictions
// в `BackupPaths.ShouldInclude`).
//
// Вынесен из `Backup.BackupPaths` (задача `57b5e9bc`, шаг 5): примитив — это
// инфраструктурные данные, общие для обеих подсистем (Backup и редактор секретов
// `Dossiers.InstanceSecretsProvider`). Хранить их внутри Backup = плодить шов
// `Dossiers → Backup` ради списка имён. По образцу `TranscriptRoots` (волна 4C).
public static class InstanceSecretFiles
{
    public static readonly string[] Names =
        ["jwt-secret.txt", "vapid-keys.json", "module-keys.json", "mcp-secrets.json"];
}
