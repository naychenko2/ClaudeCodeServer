using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Skills;

// Подсистема навыков (Skills, волна 4C, шаг 3): чтение скиллов и агентов из глобального
// (~/.claude/skills, ~/.claude/workflows, ~/.claude/plugins) и проектного (.claude/skills,
// .claude/agents) каталога; обёртка над CLI «npx skills» (SkillsCliService) для поиска
// и установки из реестра skills.sh; LLM-подбор навыков под персону/проект/запрос
// (SkillSuggestService); LLM-генерация тела нового навыка по свободному промпту
// (SkillGenerationService); перевод описаний и поисковых запросов RU→EN (SkillTranslationService);
// фоновый перевод описаний плагиновых скиллов с персистентным кешем (PluginSkillLocalizer);
// инжекция тела агента в системный промпт сессии и раскрытие /skill в сообщениях.
//
// Известные границы (сознательные):
// 1) `ClaudeHomeServer.Services.Llm` — префикс-шов через `ICheapTextRunner` и
//    `LocalActionCatalog.SkillSuggest/SkillTranslate/SkillGenerate` (SkillSuggestService,
//    SkillTranslationService, SkillGenerationService). Префикс-шов по прецеденту
//    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Tasks`/`Docs`.
// 2) `ClaudeHomeServer.Services.Execution` — префикс-шов через `ILauncherFactory` для
//    запуска CLI «npx skills» в `SkillsCliService.RunAsync` (запуск процесса — общий
//    слой, по аналогии с `ProjectServices`/`Terminal`/`Git`/`Deploy`). Execution — нижний
//    слой, ссылаться на него сверху законно.
// 3) Точечные допуски к корню `ClaudeHomeServer.Services.*` — «вертикаль → спинка»:
//    - `PersonaManager` — `SkillSuggestService.SuggestForPersonaAsync` резолвит персону
//      владельца и читает существующие Skill-привязки (PersonaBinding.Target) для
//      исключения уже привязанных скиллов из кандидатов.
//    - `ProjectManager` — `SkillSuggestService.SuggestForProjectAsync` берёт контекст
//      проекта (имя + системный промпт); `SkillsService.GetProjectSkills/Agents`
//      работают с `projectRootPath`; `SkillsController` использует `GetById(projectId)`
//      для получения пути.
// 4) Известные потребители (шов `SessionManager → SkillsService` остаётся до этапа 4 —
//    расщепление SessionManager): `SessionManager.InstalledSkillNames` собирает имена
//    глобальных + workflow + плагинных скиллов для фильтра каталога «Командных механик»
//    руководителя проекта (блок системного промпта). Также `PersonaLayerContributor`
//    и `ClaudeSession` держат `SkillsService?` как опциональную зависимость для
//    раскрытия /skill в сообщениях и инжекции тела агента в системный промпт.
//
// Шов `Skills → Models.Persona` (binding-источник `PersonaBinding.Target`) идёт через
// `ClaudeHomeServer.Models` (SharedAllowedPrefixes).
public sealed class SkillsSubsystem : IAppSubsystem
{
    public string Key => "skills";

    public string Title => "Навыки";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Singleton-ы без внутреннего порядка между собой; SkillsSuggestService зависит
        // от SkillsCliService + SkillsTranslationService, SkillsController — от всех шести.
        services.AddSingleton<SkillsService>();
        services.AddSingleton<SkillsCliService>();
        services.AddSingleton<SkillTranslationService>();
        services.AddSingleton<PluginSkillLocalizer>();
        services.AddSingleton<SkillSuggestService>();
        services.AddSingleton<SkillGenerationService>();
    }
}