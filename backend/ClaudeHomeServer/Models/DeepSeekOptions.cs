namespace ClaudeHomeServer.Models;

// Конфигурация DeepSeek-провайдера (секция "DeepSeek"). API-ключ — в appsettings.Local.json.
// Пустой ключ = провайдер выключен: модели не попадают в каталог, адаптер недоступен.
public class DeepSeekOptions
{
    public const string Section = "DeepSeek";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public int TimeoutSeconds { get; set; } = 600;
    // Лимит итераций tool-цикла на один ход
    public int MaxToolIterations { get; set; } = 15;
    public int MaxTokens { get; set; } = 8192;
    // Инструмент запуска команд (run_command): каждый запуск требует разрешения пользователя
    public bool EnableShellTool { get; set; } = true;
    public int ShellTimeoutSeconds { get; set; } = 120;
    public List<DeepSeekModelConfig> Models { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    public DeepSeekModelConfig? FindModel(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

// Модели DeepSeek меняются (алиасы deepseek-chat/reasoner выводятся 24.07.2026),
// поэтому список — строго из конфига, без хардкода в коде.
public class DeepSeekModelConfig
{
    // Значение в каталоге моделей (хранится в Session.Model); должно начинаться с "deepseek"
    public string Id { get; set; } = "";
    // Id модели для API; null → совпадает с Id (позволяет две записи каталога на одну модель:
    // с thinking и без)
    public string? ApiModel { get; set; }
    public string DisplayName { get; set; } = "";
    public int ContextWindow { get; set; } = 1_000_000;
    // Включать thinking-режим (параметр thinking: { type: enabled })
    public bool Thinking { get; set; }
    public bool SupportsTools { get; set; } = true;
    // Цены $/1M токенов — для расчёта TotalCostUsd; 0 → стоимость не считаем
    public double PriceInMissPer1M { get; set; }
    public double PriceInHitPer1M { get; set; }
    public double PriceOutPer1M { get; set; }

    public string EffectiveApiModel => string.IsNullOrWhiteSpace(ApiModel) ? Id : ApiModel;
}
