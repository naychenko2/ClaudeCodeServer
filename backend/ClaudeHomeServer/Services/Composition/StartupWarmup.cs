using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Modules;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Services.Composition;

// Стартовый ряд после builder.Build() (S6-2): статические логгеры и разрешённые корни
// транскриптов, доводка после восстановления из бэкапа, прогрев сервисов, которые обязаны
// родиться до первого запроса, и разовые миграции сторов.
//
// Почему вынесено: это не конфигурация хоста, не аутентификация, не конвейер мидлваров и
// не маршруты — то есть не то, что по плану S6 обязано остаться в Program.cs. Порядок
// внутри ряда сохранён строка в строку: часть шагов стоит «строго ДО первого обращения»
// (PostRestoreHook до MigrateFromProjects, ModuleRegistry до слоя Llm, прогрев слушателя
// сторожей до его hosted-цикла), и этот порядок — часть контракта.
//
// `app.UseSubsystems()` остаётся в Program.cs: это пост-билд фаза самих подсистем,
// а не прогрев.
public static class StartupWarmup
{
    public static async Task RunStartupWarmup(this WebApplication app, bool inspectionMode)
    {
        // Доставка алертов о нечитаемых сторах (JsonFileStore статический, DI туда не дотягивается);
        // алерты, случившиеся до этой строки, придут прямо сейчас
        ClaudeHomeServer.Services.DataLossAlerts.Attach(app.Services);

        // Логгер статического парсера workflow-транскриптов (DI туда не дотягивается)
        ClaudeHomeServer.Services.Llm.WorkflowAgentParser.Log = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(ClaudeHomeServer.Services.Llm.WorkflowAgentParser));
        // Логгер резолвера meta-блоков workflow (обогащение input вызова по имени)
        ClaudeHomeServer.Services.Llm.WorkflowMetaResolver.Log = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(ClaudeHomeServer.Services.Llm.WorkflowMetaResolver));
        // Дополнительные разрешённые корни транскриптов — пути проектов сторонних CLI-провайдеров
        // (GLM/DeepSeek используют изолированные профили, транскрипты пишутся не в ~/.claude)
        try
        {
            var registry = app.Services.GetRequiredService<ClaudeHomeServer.Services.Llm.LlmProviderRegistry>();
            foreach (var dir in registry.GetProviderProjectsDirs())
            {
                ClaudeHomeServer.Services.TranscriptRoots.AddAllowedRoot(dir);
                Console.WriteLine($"[TranscriptRoots] разрешён корень провайдера: {dir}");
            }
            // Профили подписок (sub-*) и созданные после старта: разрешаем весь корень
            // claude-profiles по шаблону {key}/projects — иначе WorkflowWatcher у таких
            // сессий молча выключается («Детали недоступны» в блоке Workflow)
            ClaudeHomeServer.Services.TranscriptRoots.ProfilesRoot = registry.ProfilesDir;
            Console.WriteLine($"[TranscriptRoots] разрешён корень профилей: {registry.ProfilesDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TranscriptRoots] не удалось зарегистрировать корни провайдеров: {ex.Message}");
        }

        // Доводка после восстановления из бэкапа: сбросить карты документов баз знаний, чтобы
        // Dify-слой пересобрался с натуры. Строго ДО первого обращения к WorkspaceKnowledgeStore
        // (тот читает файл в конструкторе) — то есть до MigrateFromProjects ниже.
        if (!inspectionMode)
        {
            ClaudeHomeServer.Services.Backup.PostRestoreHook.RunIfNeeded(
                Path.GetDirectoryName(Path.GetFullPath(app.Configuration["DataPath"]
                    ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!,
                app.Services.GetService<ILogger<Program>>());
        }

        // Прогрев сервисов на старте — UserStore печатает предупреждение если создал admin/admin
        app.Services.GetRequiredService<UserStore>();
        // Реестр модулей — строго ДО первого обращения к слою Llm: его конструктор регистрирует
        // LLM-действия модулей (§10.1) в динамическом слое LocalActionCatalog, а
        // LocalActionOverridesStore при загрузке отбрасывает оверрайды неизвестных ключей —
        // поздняя регистрация теряла бы сохранённые маршруты модульных действий
        app.Services.GetRequiredService<ModuleRegistry>();
        // Одноразовая миграция ф.2.1: усыновление per-owner higgsfield-токенов под сервисного
        // владельца (higgsfield-instance). Идемпотентна — повторный вызов без per-owner записей
        // вернёт false. Best-effort: сбой не блокирует старт.
        // В инспекционной копии НЕ запускаем: она работает только на чтение, а миграция удаляет
        // per-owner записи и переносит секреты — то есть показала бы не то, что лежит в архиве
        // (а архив до этой фичи такие записи как раз и содержит). Плюс SaveState в конце пишет
        // higgsfield.json даже когда переносить нечего.
        if (!inspectionMode)
        {
            try
            {
                app.Services.GetRequiredService<ClaudeHomeServer.Services.Mcp.HiggsfieldOAuthService>()
                    .RunMigration();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HiggsfieldMigration] миграция пропущена: {ex.Message}");
            }
        }
        if (!inspectionMode)
        {
            // Фоновый прогрев каталога моделей (опрос claude CLI ~5 с — не задерживаем старт).
            // В копии пропускаем: запуск claude зарегистрировал бы процесс в pid-файле БОЕВОГО
            // сервера (реестр живёт рядом с exe, а не в DataPath).
            _ = Task.Run(() => app.Services.GetRequiredService<ModelCatalogService>().GetModelsAsync());
            // Прогрев активной локальной LLM — IHostedService `LocalLlmWarmupService` из
            // подсистемы Llm, срабатывает автоматически при старте (условие — внутри сервиса).
        }
        app.Services.GetRequiredService<JwtService>();
        // Раздача волн «Командной реализации»: конструктор вешает хук в SessionManager
        app.Services.GetRequiredService<TeamWaveService>();
        // Слоты ревью: учёт аренд в памяти после рестарта пуст — вернуть аренды открытых ревью,
        // чаты которых пережили рестарт, иначе их слот отдали бы другой задаче и сбросили
        await app.Services.GetRequiredService<ReviewTreePool>().RestoreLeasesAsync();
        // Слушатель событий стора сторожей (watchdogs_changed): подписка встаёт в конструкторе
        app.Services.GetRequiredService<ClaudeHomeServer.Services.Watchdog.WatchdogNotifier>();
        // Синк файловых сабагентов-персон: подписки на события PersonaManager должны встать
        // до первых запросов (иначе ранние правки персон не долетят до .md-файлов).
        // В копии НЕ поднимаем: синк пишет .claude/agents/*.md в реальные папки проектов
        // из восстановленного projects.json — то есть в рабочие каталоги на диске.
        if (!inspectionMode) app.Services.GetRequiredService<PersonaAgentFileSync>();

        // Разовая миграция на JSON-канал (CLI ≥ 2.1.281): убирает синхронизированные .md-агентов
        // (только с маркером, папку целиком не трогает) из проектов, Chats и резерва persona-agents.
        // В JSON-режиме (дефолт) продукт .md больше не пишет — файлы мёртвый груз; в файловом
        // синке (Persona:AgentFiles=true) миграция no-op и маркер НЕ пишется: поздний переход
        // на JSON прогонит её ещё раз (за время файлового режима могли появиться новые .md).
        if (!inspectionMode)
            try
            {
                var agentSync = app.Services.GetRequiredService<PersonaAgentFileSync>();
                if (!agentSync.AgentFilesEnabled)
                {
                    var dataDir = Path.GetDirectoryName(
                        app.Configuration["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
                        ?? Path.Combine(AppContext.BaseDirectory, "data");
                    var marker = Path.Combine(dataDir, "agents-json-migration-v1.done");
                    if (!File.Exists(marker))
                    {
                        var removed = agentSync.MigrateLegacyAgentFiles();
                        Directory.CreateDirectory(dataDir);
                        File.WriteAllText(marker, $"{DateTime.UtcNow:O} removed={removed}");
                        if (removed > 0)
                            Console.WriteLine($"[AgentsJsonMigration] удалено синхронизированных .md-агентов: {removed}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AgentsJsonMigration] миграция пропущена: {ex.Message}");
            }

        // Однократная миграция @handle персон под контекстное правило: схлопывает лишние суффиксы
        // (masha-2 → masha там, где контексты не пересекаются) и чистит старые .md-файлы сабагентов
        // по прежнему handle. Маркер-файл — чтобы не гонять на каждом старте. Best-effort: сбой
        // миграции не мешает старту.
        // В инспекционной копии не запускаем: маркер в архиве может быть старым, и миграция
        // переименовала бы персон, удаляя и перегенерируя .md в РЕАЛЬНЫХ папках проектов.
        if (!inspectionMode)
            try
            {
                var dataDir = Path.GetDirectoryName(
                    app.Configuration["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
                    ?? Path.Combine(AppContext.BaseDirectory, "data");
                var marker = Path.Combine(dataDir, "handle-migration-v1.done");
                if (!File.Exists(marker))
                {
                    var personaManager = app.Services.GetRequiredService<PersonaManager>();
                    var agentSync = app.Services.GetRequiredService<PersonaAgentFileSync>();
                    var renamed = personaManager.MigrateContextualHandles();
                    // Сначала удалить старые .md по прежнему handle (клон с oldHandle даёт старые пути)
                    foreach (var (persona, oldHandle) in renamed)
                        try { agentSync.RemovePersona(PersonaManager.WithHandle(persona, oldHandle)); } catch { /* не критично */ }
                    // Затем перегенерировать файлы затронутых владельцев под новые handle
                    foreach (var owner in renamed.Select(r => r.Persona.OwnerId).Distinct())
                        try { agentSync.SyncOwner(owner, force: true); } catch { /* не критично */ }
                    Directory.CreateDirectory(dataDir);
                    File.WriteAllText(marker, $"{DateTime.UtcNow:O} renamed={renamed.Count}");
                    if (renamed.Count > 0)
                        Console.WriteLine($"[HandleMigration] контекстные @handle: переименовано персон — {renamed.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HandleMigration] миграция пропущена: {ex.Message}");
            }

        // Стартовый проход провижна авто-ассистента — ЕДИНСТВЕННАЯ точка провижна существующим
        // пользователям: для каждого гарантируем действующую дефолт-персону.
        // Идемпотентен (EnsureAsync не плодит дубль), поэтому гонять на каждом старте безопасно.
        // Best-effort: сбой одного пользователя не мешает старту. Синхронно (по образцу миграции
        // handle выше) — к моменту готовности сервера принимать запросы ассистент уже создан,
        // и /api/auth/me отдаёт его без провижна на чтение
        // (инвариант: провижн НИКОГДА не вызывается на GET). В копии не запускаем: пишет в
        // users.json/personas.json, а инспекционная копия чужие данные не трогает.
        if (!inspectionMode)
            try
            {
                var users = app.Services.GetRequiredService<UserStore>();
                var provisioner = app.Services.GetRequiredService<DefaultAssistantProvisioner>();
                foreach (var u in users.GetAll())
                {
                    try { _ = provisioner.EnsureAsync(u.Id).GetAwaiter().GetResult(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[DefaultAssistant] провижн пользователя {u.Id} пропущен: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DefaultAssistant] стартовый проход пропущен: {ex.Message}");
            }

        // Чистка осиротевших temp-конфигов MCP: содержат сервисный токен и могли
        // остаться после крэша (штатно удаляются в finally каждого хода).
        // Temp общий с боевым сервером — копия чужие конфиги не трогает.
        if (!inspectionMode)
            _ = Task.Run(() =>
            {
                try
                {
                    // claude-mcp-* — сервисный токен; claude-agents-* — определения сабагентов-персон;
                    // claude-prompt-* — системный промпт хода (все per-run, штатно удаляются финализацией прогона, осиротели — после крэша).
                    foreach (var pattern in new[] { "claude-mcp-*.json", "claude-agents-*.json", "claude-prompt-*.md" })
                        foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
                            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) File.Delete(f); } catch { }
                }
                catch { /* нет доступа к temp — не критично */ }
                try
                {
                    // хук потребителей: признаки «файл уже правили» (<префикс проекта>_<session_id>) и каталоги отпечатков прежних сборок, TTL сутки
                    ClaudeRuntimeSettings.CleanupHookState(Path.GetTempPath(), TimeSpan.FromDays(1));
                }
                catch { /* не критично */ }
            });

        // Однократная миграция: переносим DifyDatasetId/DocumentTags из старых Project-записей в WorkspaceKnowledge
        app.Services.GetRequiredService<WorkspaceKnowledgeStore>()
            .MigrateFromProjects(app.Services.GetRequiredService<ProjectManager>().GetAll());
    }
}
