namespace ClaudeHomeServer.Services.Llm;

// Разовая миграция закреплённых моделей GLM: 19.08.2026 каталог провайдера пересобран под
// живые пробы z.ai — вся линейка 5.x оказалась алиасом glm-5.3, а glm-4.5-air алиасом
// glm-4.7 (подробности и доказательства — LlmProviders:glm#comment-tiers в appsettings.json).
// Записи исчезнувших id остались бы в сторах мёртвыми пинами: BuildCliEnv ставит
// CLAUDE_CODE_MAX_CONTEXT_TOKENS по ТОЧНОМУ id каталога, поэтому 36 боевых чатов на
// glm-5.2[1m] молча вернулись бы к окну по умолчанию вместо 1M.
//
// Идемпотентна и одноразова: после прохода пишет marker-файл в каталоге DataPath, поэтому
// повторный старт (и осознанный ручной откат пина пользователем) ничего не переписывает.
// Ошибки не роняют старт приложения (best-effort) — образец PersonaProjectBindingsMigration.
public class GlmModelAliasMigration : IHostedService
{
    // Точное совпадение id → новый id. Всё остальное (preset:{id}, tier:*, local/claude/default,
    // незнакомые модели) не адресуется картой и остаётся как есть.
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["glm-5.2[1m]"] = "glm-5.3[1m]",
            ["glm-5.2"] = "glm-5.3",
            ["glm-5.1"] = "glm-5.3",
            ["glm-5"] = "glm-5.3",
            ["glm-4.5-air"] = "glm-4.7",
        };

    private readonly SessionManager _sessions;
    private readonly SpecialtySettingsStore _specialties;
    private readonly ILogger<GlmModelAliasMigration> _log;
    private readonly string _dataDir;
    private readonly string _markerPath;

    public GlmModelAliasMigration(SessionManager sessions, SpecialtySettingsStore specialties,
        IConfiguration config, ILogger<GlmModelAliasMigration> log)
    {
        _sessions = sessions;
        _specialties = specialties;
        _log = log;
        // Каталог данных — как у остальных сторов (в контейнере это /data volume)
        _dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _markerPath = Path.Combine(_dataDir, ".glm-model-aliases-migrated");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Уже мигрировали — повторно не трогаем (иначе перебивали бы ручной выбор
            // пользователя при каждом рестарте)
            if (File.Exists(_markerPath)) return Task.CompletedTask;

            // Правки идут через живые сторы, а не по файлу: к моменту StartAsync они уже
            // загрузились в память, и запись мимо них потерялась бы на первом же Save.
            var chats = MigrateStore("sessions.json", () => _sessions.RemapModels(Map));
            var specialties = MigrateStore("specialty-settings.json", () => _specialties.RemapModels(Map));

            File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("O"));
            _log.LogInformation(
                "Миграция моделей GLM: sessions.json — изменено чатов {Chats}, specialty-settings.json — изменено записей {Specialties}",
                chats, specialties);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Миграция моделей GLM не выполнена — старт продолжается");
        }
        return Task.CompletedTask;
    }

    // Снимок стора рядом с ним (прецедент: personas.json.bak-<таймстемп>) ДО перезаписи —
    // это боевые пользовательские данные. Ничего не изменилось — снимок убираем, чтобы
    // не плодить копии на каждом чистом развёртывании.
    private int MigrateStore(string fileName, Func<int> migrate)
    {
        var path = Path.Combine(_dataDir, fileName);
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
