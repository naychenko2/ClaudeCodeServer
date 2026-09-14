namespace ClaudeHomeServer.Services.Composition;

// Действие файлового мутатора. Дублирует FileService.FileMutationKind — нужен в Core,
// чтобы IProjectFileGateway мог объявить событие OnMutated без зависимости от Main.
public enum FileMutationKind { Write, Create, Delete, Rename }

public static class ProjectFileGatewayConstants
{
    // Каталог вложений чата. Используется ProjectKnowledgeSyncService для фильтрации
    // путей в NormalizeHint: вложения чата не должны попадать в базу знаний.
    // Форвардер: источник правды — TreeExcludes.AttachmentsDir.
    public const string AttachmentsDir = TreeExcludes.AttachmentsDir;
}
