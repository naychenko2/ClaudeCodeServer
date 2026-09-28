using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Разовая миграция моделей родного Claude к семействам: храним только opus/fable/sonnet/haiku,
// версию выбирает CLI по алиасу, окно 1M дописывает сервер при запуске (ClaudeModelFamily).
// До этого каталог CLI отдавал часть пунктов версионными id, и выбор такого пункта прибивал
// чат к версии: на 2026-09-27 в sessions.json лежали claude-opus-4-8 ×17, claude-fable-5[1m] ×16,
// claude-fable-5-1[1m] ×4, opus[1m] ×451 и т.д. Новые записи сводит каждый стор сам на записи,
// эта миграция — для уже осевших.
//
// Идемпотентна (сведение к семейству повторно ничего не меняет) и одноразова: после прохода
// пишет маркер в каталоге DataPath. Перед правкой стора снимает копию рядом с ним, без копии
// стор не трогает; ничего не изменилось — копию убирает. Правки идут через живые сторы, а не
// по файлам: к StartAsync они уже в памяти, и запись мимо них потерялась бы на первом Save.
// Сторонние модели (glm-*, kimi-*…) не трогаются: сведение идёт через
// LlmProviderRegistry.CanonicalizeModel, который сперва спрашивает ResolveByModel.
// Образец — GlmModelAliasMigration; ошибки не роняют старт.
public class ClaudeModelFamilyMigration : IHostedService
{
    public const string MarkerFileName = "model-families-migration-v1.done";

    private readonly LlmProviderRegistry _providers;
    private readonly SessionManager _sessions;
    private readonly PersonaManager? _personas;
    private readonly SpecialtySettingsStore? _specialties;
    private readonly AppSettingsService? _appSettings;
    private readonly UserStore? _users;
    private readonly LocalActionOverridesStore? _localActions;
    private readonly ILogger<ClaudeModelFamilyMigration> _log;
    private readonly string _dataDir;
    private readonly string _personasPath;
    private readonly string _markerPath;

    public ClaudeModelFamilyMigration(LlmProviderRegistry providers, SessionManager sessions,
        IConfiguration config, ILogger<ClaudeModelFamilyMigration> log,
        PersonaManager? personas = null, SpecialtySettingsStore? specialties = null,
        AppSettingsService? appSettings = null, UserStore? users = null,
        LocalActionOverridesStore? localActions = null)
    {
        _providers = providers;
        _sessions = sessions;
        _personas = personas;
        _specialties = specialties;
        _appSettings = appSettings;
        _users = users;
        _localActions = localActions;
        _log = log;
        _dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _personasPath = config["PersonasPath"] ?? Path.Combine(_dataDir, "personas.json");
        _markerPath = Path.Combine(_dataDir, MarkerFileName);
    }

    // Новое значение или null — менять нечего (уже семейство, default, не модель, сторонний id)
    internal string? Canonical(string model) =>
        _providers.CanonicalizeModel(model) is { } next && next != model ? next : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_markerPath)) return Task.CompletedTask;

            var counts = new List<string>
            {
                $"sessions.json — {MigrateStore(Path.Combine(_dataDir, "sessions.json"), () => _sessions.RemapModels(Canonical))}",
            };
            if (_personas is not null)
                counts.Add($"personas.json — {MigrateStore(_personasPath, () => _personas.RemapModels(Canonical))}");
            if (_specialties is not null)
                counts.Add($"specialty-settings.json — {MigrateStore(Path.Combine(_dataDir, "specialty-settings.json"), () => _specialties.RemapModels(Canonical))}");
            if (_appSettings is not null)
                counts.Add($"app-settings.json — {MigrateStore(Path.Combine(_dataDir, "app-settings.json"), () => _appSettings.RemapModels(Canonical))}");
            if (_users is not null)
                counts.Add($"users.json — {MigrateStore(Path.Combine(_dataDir, "users.json"), () => _users.RemapModels(Canonical))}");
            if (_localActions is not null)
                counts.Add($"local-actions.json — {MigrateStore(Path.Combine(_dataDir, "local-actions.json"), () => _localActions.RemapModels(Canonical))}");

            File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("O"));
            _log.LogInformation("Миграция моделей Claude к семействам, изменено записей: {Counts}",
                string.Join("; ", counts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Миграция моделей Claude к семействам не выполнена — старт продолжается");
        }
        return Task.CompletedTask;
    }

    private int MigrateStore(string path, Func<int> migrate)
    {
        var backupPath = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backedUp = false;
        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, backupPath, overwrite: true);
                backedUp = true;
            }
            catch (Exception ex)
            {
                // Без снимка стор не трогаем: потерять боевые пины хуже, чем не мигрировать
                _log.LogWarning(ex, "Не удалось снять копию {Path} — миграция стора пропущена", path);
                return 0;
            }
        }

        var changed = migrate();
        if (changed == 0 && backedUp)
        {
            try { File.Delete(backupPath); } catch { /* лишний .bak безобиден */ }
        }
        return changed;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
