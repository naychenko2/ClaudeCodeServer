namespace ClaudeHomeServer.Models;

public class AppSettings
{
    public string DefaultProjectsPath { get; set; } = "";
    // Тип доступа к Claude: "subscription" (подписка — стоимость показывается как ≈ API-эквивалент,
    // отдельно не списывается) | "api" (оплата по API-ключу — реальная цена). По умолчанию подписка.
    public string ClaudeBilling { get; set; } = "subscription";
}
