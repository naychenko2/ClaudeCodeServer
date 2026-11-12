namespace ClaudeHomeServer.Services.Docs;

// Регистрация вертикали Docs (S6-2): все сервисы этой сборки регистрируются здесь,
// а не точечно в Program.cs. Причина переноса: ни один из этих типов не нужен вне
// панели «Доки», и регистрация не смотрит ни в Main, ни в другую вертикаль — то есть
// это собственность Docs, а не композиционный клей (ADR-014 «Объём выноса меряется
// заглушками»). Подсистемы у вертикали нет (Docs — статическая сборка без рубильника),
// поэтому точка входа — расширение IServiceCollection, как AddChatContext в Core.
// Порядок и состав регистраций — ровно те, что были в Program.cs.
public static class DocsRegistration
{
    public static IServiceCollection AddDocs(this IServiceCollection services)
    {
        // Индекс документации проекта (README + docs/) для панели «Доки»: связи, поиск.
        // Кеш живёт внутри сервиса и ключуется корнем папки, поэтому singleton.
        services.AddSingleton<DocsIndexService>();
        // Каталог макетов фичи (волна 3, Б-mk-a): записки docs/mockups + linkedFiles. Stateless
        // singleton, как DocsIndexService, — корпус читает через него, своего кеша/обхода нет.
        services.AddSingleton<MockupCatalog>();
        // Гигиена карты проекта (CLAUDE.md): детерминированный сканер без модели, пороги —
        // из секции ProjectMap конфигурации. Состояния нет, читает файлы по запросу
        services.AddSingleton<ProjectMapScanner>();
        // Фаза 2 той же уборки: суждение модели поверх фактов сканера. Состояния нет, ответ
        // модели нигде не кэшируется — факты пересчитываются на каждый запрос
        services.AddSingleton<ProjectMapReviewService>();
        // Фаза 3: запись отмеченных человеком правок в CLAUDE.md. Синглтон не ради экономии —
        // лок на путь карты обязан быть общим для всех запросов процесса (два окна продукта у
        // одного человека правят одну карту)
        services.AddSingleton<ProjectMapApplyService>();
        // Документы AI: конвертация в Markdown (markitdown) + ИИ-помощь (суммари/выжимка/теги) на локальной модели
        services.AddSingleton<MarkitdownService>();
        services.AddSingleton<DocumentAiService>();
        return services;
    }
}
