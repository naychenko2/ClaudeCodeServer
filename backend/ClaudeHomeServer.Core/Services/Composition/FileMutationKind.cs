namespace ClaudeHomeServer.Services.Composition;

// Действие файлового мутатора — единый вид для FileService (вертикаль Files) и швов
// IProjectFileGateway/IProjectFiles: событие OnMutated объявлено в Core.
public enum FileMutationKind { Write, Create, Delete, Rename }

public static class ProjectFileGatewayConstants
{
    // Каталог вложений чата. Используется ProjectKnowledgeSyncService для фильтрации
    // путей в NormalizeHint: вложения чата не должны попадать в базу знаний.
    // Форвардер: источник правды — TreeExcludes.AttachmentsDir.
    public const string AttachmentsDir = TreeExcludes.AttachmentsDir;
}
