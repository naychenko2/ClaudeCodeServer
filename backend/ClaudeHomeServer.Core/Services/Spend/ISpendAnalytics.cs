namespace ClaudeHomeServer.Services.Spend;

// Фильтр среза (значения id разрезов). Для не-админа Me принудительно = текущий
// пользователь (SpendAccess), поэтому чужие данные на уровне запросов недостижимы.
// В Core: шов ISpendAnalytics принимает его, Main конструирует его.
//
// Три разных смысла «пользователя» — три поля, не одно:
//  • Owner — точный арендатор (ключ хранилища): запись лежит в чужом проекте,
//    относится его владельцу.
//  • Me — записи, принадлежащие человеку: как арендатору (свои проекты, включая чужие
//    ходы в них) или как актору (свои ходы в чужом общем проекте). Ровно «своё».
//  • User — ключ оси пользователя в pivot («актор, а без актора — владелец», тот же
//    порядок, что у уровня «user» в GroupRaw): для сужения по строке разбивки.
public sealed record SpendFilter(
    string? Owner = null, string? Project = null, string? Chat = null, string? Task = null,
    string? Persona = null, string? Provider = null, string? Model = null, string? Source = null,
    string? Me = null, string? User = null);

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
