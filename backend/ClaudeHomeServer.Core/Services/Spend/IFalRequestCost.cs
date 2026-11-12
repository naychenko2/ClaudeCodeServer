namespace ClaudeHomeServer.Services.Spend;

// Шов фактической цены запроса fal.ai: потребитель (вертикаль Images и др.) получает
// стоимость по request_id, не завися от сервиса чата. Реализация — FalCostService в Main
// (там же дедуп _seen и публикация в чат через OnCostResolved): в Core едет только
// контракт, по прецеденту ISpendCollector рядом (дефект 22068e9d, шаг S-SPEND-1).
public interface IFalRequestCost
{
    // Фактически списанная стоимость запроса (USD) либо null — если биллинг не найден
    // за отведённое окно опроса. Без исключений: «цены ещё нет» — штатный исход, не ошибка.
    Task<double?> ResolveUsdAsync(string requestId, CancellationToken ct);
}
