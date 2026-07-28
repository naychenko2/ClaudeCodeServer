using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Слот тира модели: три именованные модели инстанса, на которые ссылаются
// назначения мест («новый чат», «теги заметок»…) значением "tier:<slot>".
public enum ModelTier { Strong, Medium, Weak }

// Разбор уровня модели, пришедшего с провода (задача, персона): "strong|medium|weak",
// регистр не важен. Только белый список трёх имён — Enum.TryParse сюда не годится: он
// принимает индексы enum ("0" → Strong, со знаком "+0" мимо любого digit-guard) и списки
// флагов ("strong,weak" → Weak) независимо от FlagsAttribute. Мусор от LLM должен давать
// 400, а не молча уезжать на самую дорогую модель.
public static class ModelTiers
{
    // Текст ошибки 400 при неизвестном уровне — один на все точки входа (задачи, персоны)
    public const string WireError = "Уровень модели должен быть strong, medium или weak";

    public static bool TryParse(string? value, out ModelTier tier)
    {
        tier = default;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "strong": tier = ModelTier.Strong; return true;
            case "medium": tier = ModelTier.Medium; return true;
            case "weak": tier = ModelTier.Weak; return true;
            default: return false;
        }
    }

    // Значение поля в запросе: null — не прислали, "" — сброс, иначе — обязан быть уровнем
    public static bool IsValidWireValue(string? value) =>
        value is null || value.Trim().Length == 0 || TryParse(value, out _);
}

public class AppSettingsService
{
    private readonly string _storePath;
    // Источник истины для DefaultProjectsPath — ТОЛЬКО конфиг (appsettings.Local.json).
    // В рантайм-файле этот путь НЕ хранится: он не редактируется через UI, а лишь протекал бы
    // туда как побочный эффект сохранения других настроек (Save берёт весь AppSettings).
    // Осевшее устаревшее значение после смены окружения (напр. докеровский /projects при
    // переезде на хост) тихо затеняло бы конфиг и ломало резолв домашних папок — поэтому
    // читаем путь всегда из конфига, а в файл пишем лишь ClaudeBilling.
    private readonly string _configDefault;
    private AppSettings _settings = new();
    private readonly object _lock = new();

    public AppSettingsService(IConfiguration config, ILogger<AppSettingsService>? log = null)
    {
        var projectsPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(projectsPath)!, "app-settings.json");
        _configDefault = config["DefaultProjectsPath"] ?? "";
        Load();
        log?.LogInformation(
            "AppSettings: DefaultProjectsPath = «{Path}» (источник: конфиг DefaultProjectsPath)",
            string.IsNullOrWhiteSpace(_configDefault) ? "<не задан>" : _configDefault);
    }

    public AppSettings Get()
    {
        lock (_lock)
        {
            return new AppSettings
            {
                DefaultProjectsPath = _configDefault,
                ClaudeBilling = string.IsNullOrWhiteSpace(_settings.ClaudeBilling) ? "subscription" : _settings.ClaudeBilling,
                DailyBriefingEnabled = _settings.DailyBriefingEnabled ?? true,
                ModelTierStrong = _settings.ModelTierStrong,
                ModelTierMedium = _settings.ModelTierMedium,
                ModelTierWeak = _settings.ModelTierWeak,
            };
        }
    }

    // Модель слота. Пустой слот → null: место, ссылающееся на него, уходит на дефолт CLI.
    public string? TierModel(ModelTier tier)
    {
        lock (_lock)
        {
            var v = tier switch
            {
                ModelTier.Strong => _settings.ModelTierStrong,
                ModelTier.Weak => _settings.ModelTierWeak,
                _ => _settings.ModelTierMedium,
            };
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    public AppSettings Save(AppSettings settings)
    {
        lock (_lock)
        {
            // Персистим только редактируемые поля; DefaultProjectsPath намеренно НЕ сохраняем
            // (иначе резолвнутое значение осело бы в файле и затенило конфиг после смены среды).
            // Патч-семантика: null = «поле не прислали, оставить прежним» (см. комментарий
            // к AppSettings) — экран правит свою настройку, не зная про остальные.
            _settings = new AppSettings
            {
                ClaudeBilling = settings.ClaudeBilling ?? _settings.ClaudeBilling,
                DailyBriefingEnabled = settings.DailyBriefingEnabled ?? _settings.DailyBriefingEnabled,
                // null = не прислали (оставить прежним), "" = сознательная очистка слота
                ModelTierStrong = settings.ModelTierStrong ?? _settings.ModelTierStrong,
                ModelTierMedium = settings.ModelTierMedium ?? _settings.ModelTierMedium,
                ModelTierWeak = settings.ModelTierWeak ?? _settings.ModelTierWeak,
            };
            // Запись через JsonFileStore: атомарна (tmp + move), крэш посреди сохранения
            // не оставляет обрезанный app-settings.json — раньше был голый WriteAllText
            JsonFileStore.Save(_storePath, _settings);
        }
        return Get();
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            // Гигиена: устаревший DefaultProjectsPath мог осесть здесь раньше — обнуляем, чтобы
            // он не читался и не сериализовался обратно (Get всё равно берёт путь из конфига).
            _settings.DefaultProjectsPath = "";
            // Миграция v1→v2: одиночная DefaultChatModel переезжает в слот «средняя»
            // (не перетирая уже заданный) и очищается; персистим сразу, чтобы миграция
            // была одноразовой, а не повторялась на каждом старте.
            if (!string.IsNullOrWhiteSpace(_settings.DefaultChatModel))
            {
                if (string.IsNullOrWhiteSpace(_settings.ModelTierMedium))
                    _settings.ModelTierMedium = _settings.DefaultChatModel;
                _settings.DefaultChatModel = null;
                JsonFileStore.Save(_storePath, _settings);
            }
        }
        catch { /* первый запуск или повреждённый файл */ }
    }
}
