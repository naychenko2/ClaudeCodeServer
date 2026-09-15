namespace ClaudeHomeServer.Services.Spend;

// Фильтр среза (значения id разрезов). Owner для не-админа принудительно = текущий
// пользователь (SpendAccess), поэтому чужие данные на уровне запросов недостижимы.
// В Core: шов ISpendAnalytics принимает его, Main конструирует его.
public sealed record SpendFilter(
    string? Owner = null, string? Project = null, string? Chat = null, string? Task = null,
    string? Persona = null, string? Provider = null, string? Model = null, string? Source = null);

// Расход в рублях (сервисы Яндекса — сейчас только озвучка SpeechKit): сумма и число
// оплаченных запросов. null — за период таких трат не было.
// В Core: return-тип ISpendAnalytics.Rub.
public sealed record SpendRubDto(double Total, int Requests);

// Узкий шов Spend-аналитики для Main: YandexController вызывает только Rub.
// Остальные методы (Overview, Turns, Badge, …) живут в вертикали и не нужны Main.
public interface ISpendAnalytics
{
    SpendRubDto? Rub(DateOnly from, DateOnly to, SpendFilter filter);
}
