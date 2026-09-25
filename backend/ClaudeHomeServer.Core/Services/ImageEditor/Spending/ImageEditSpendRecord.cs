namespace ClaudeHomeServer.Services.ImageEditor.Spending;

// Единица суммы траты — своей валюты у AI Home нет (ADR-016, раздел 4): показываем
// провайдерскую сумму как есть, доллары и кредиты нигде не складываются.
public enum ImageEditSpendUnit { Usd, Credits }

// Одна трата редактора картинок: OwnerId — тот, кто нажал кнопку, НИКОГДА не инстансный
// аккаунт поставщика (у Higgsfield кредиты общие на всех, но платит конкретный человек).
public sealed record ImageEditSpendRecord(
    string Id,
    string OwnerId,
    string Provider,
    string Model,
    double Amount,
    ImageEditSpendUnit Unit,
    DateTime Timestamp,
    string TaskId);
