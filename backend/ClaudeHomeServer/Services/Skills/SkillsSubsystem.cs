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
// 1) `ClaudeHomeServer.Services.Llm` — префикс-шов для `LocalActionCatalog.SkillSuggest/
//    SkillTranslate/SkillGenerate` (ключи действий, по ним раннер ищет маршрут).
//    `ICheapTextRunner` (контракт) уже в Core (assembly-фильтр IsCoreAssembly), тут
//    шов для остального Llm-слоя, который Skills использует. Префикс-шов по прецеденту
//    `Git`/`Backgrounds`/`Deploy`/`Changelog`/`ProjectIcons`/`Tasks`/`Docs`.
// 2) Точечных допусков к корню `Services.*` нет после Этапа 3 (волна 1):
//    - `PersonaManager` снят — `SkillSuggestService` берёт персону через
//      `IPersonaSkillBindingLookup` (Core).
//    - `ProjectManager` снят — `SkillSuggestService` берёт проект через
//      `IProjectSummaryLookup` (Core).
//    - `ILauncherFactory`/`IProcessLauncher`/`ProcessSpec`/`IPathMapper` —
//      контракты в Core (assembly-фильтр), реализации (`LauncherFactory`/
//      `LocalProcessRunner`/`DockerProcessRunner`/`DockerPathMapper`) Skills не нужны:
//      `SkillsCliService` зовёт только интерфейсы.
// 3) Известные потребители (шов `SessionManager → SkillsService` остаётся до этапа 4 —
//    расщепление SessionManager): `SessionManager.InstalledSkillNames` собирает имена
//    глобальных + workflow + плагинных скиллов для фильтра каталога «Командных механик»
//    руководителя проекта (блок системного промпта). Также `PersonaLayerContributor`
//    и `ClaudeSession` держат `SkillsService?` как опциональную зависимость для
//    раскрытия /skill в сообщениях и инжекции тела агента в системный промпт.
//
// `SkillsController` лежит в `ClaudeHomeServer.Controllers`, не в этой вертикали;
// под сторож границ не попадает (по-прежнему держит `PersonaManager`/`ProjectManager`
// для своих нужд — `personas.Get/UpdateBindings`, `projects.GetById` для `RootPath`).
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