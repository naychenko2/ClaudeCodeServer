using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Разовая миграция: досеять всем существующим ПРОЕКТНЫМ персонам дефолтные привязки к их
// проекту (файлы/заметки/знания) — тем же правилом, что применяется при создании новых
// (PersonaBindingsService.SeedProjectDefaults). Идемпотентна и одноразова: после успешного
// прохода пишет marker-файл в каталоге DataPath, поэтому удалённые пользователем привязки
// повторный старт НЕ возвращает. Ошибки не роняют старт приложения (best-effort).
public class PersonaProjectBindingsMigration : IHostedService
{
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly ILogger<PersonaProjectBindingsMigration> _log;
    private readonly string _markerPath;

    public PersonaProjectBindingsMigration(PersonaManager personas, PersonaBindingsService bindings,
        IConfiguration config, ILogger<PersonaProjectBindingsMigration> log)
    {
        _personas = personas;
        _bindings = bindings;
        _log = log;
        // Каталог данных — как у остальных сторов (в контейнере это /data volume)
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _markerPath = Path.Combine(dataDir, ".personas-project-bindings-migrated");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Уже мигрировали — повторно не трогаем (иначе вернём удалённые вручную привязки)
            if (File.Exists(_markerPath)) return Task.CompletedTask;

            var projectPersonas = _personas.GetAllInternal()
                .Where(p => p.Scope == PersonaScope.Project && !string.IsNullOrEmpty(p.ProjectId))
                .ToList();

            var updated = 0;
            foreach (var persona in projectPersonas)
            {
                var before = persona.Bindings?.Count ?? 0;
                var result = _bindings.SeedProjectDefaults(persona.OwnerId, persona);
                if ((result.Bindings?.Count ?? 0) != before) updated++;
            }

            File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("O"));
            _log.LogInformation(
                "Миграция привязок проектных персон: обработано {Total}, досеяно {Updated}",
                projectPersonas.Count, updated);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Миграция дефолтных привязок проектных персон не выполнена — старт продолжается");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
