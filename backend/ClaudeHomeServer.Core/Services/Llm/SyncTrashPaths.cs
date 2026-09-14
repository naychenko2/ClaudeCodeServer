namespace ClaudeHomeServer.Services.Llm;

// Имя корневого каталога корзины синка профилей (ADR-015 §5.3). Примитив спины:
// константу пишет вертикаль `Llm` (SyncTrashStore кладёт туда удалённые файлы), а
// читает спина — `BackupPaths` исключает корзину из архива (мусорная зона с TTL,
// восстановлению не подлежит: по ADR-015 восстановление архива = повторное
// усыновление, а не возврат `.sync-trash`).
//
// Инверсия по образцу `TreeExcludes.AttachmentsDir` (волна 6): сам `SyncTrashStore`
// остаётся internal внутри Llm и форвардит на эту константу, а спина больше не
// обращается к типу чужой вертикали ради одного имени папки.
public static class SyncTrashPaths
{
    public const string RootDirName = ".sync-trash";
}
