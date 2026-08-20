using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Core.Abstractions;

/// <summary>
/// Грань исполнения на машине: кадр и запуск цели. Живёт в AiHomeDesktop.Windows — ядру
/// про WinAPI знать нечего.
///
/// Авто-ретраев здесь нет и быть не может: клик, ввод и запуск не идемпотентны, а
/// неизвестный исход НЕ означает «не применилось».
/// </summary>
public interface IDesktopExecutor
{
    /// <summary>Умеет ли эта версия клиента такой вид вызова. Нет — исход честный, а не молчание.</summary>
    bool Supports(string kind);

    /// <summary>
    /// Исполнить вызов. Прогресс (индекс последнего применённого шага) уезжает донесением
    /// в канал: без него при обрыве и дедлайне вернуть этот индекс было бы нечем.
    /// </summary>
    Task<DeviceCallResultBody> ExecuteAsync(
        DesktopCallCommand command, IProgress<int>? progress, CancellationToken ct);
}

/// <summary>Ответ человека на тост подтверждения.</summary>
public enum ConfirmationDecision
{
    /// <summary>Человек подтвердил действие.</summary>
    Confirmed,

    /// <summary>Человек отклонил — отказ уходит модели текстом.</summary>
    Declined,

    /// <summary>Человек не ответил за отведённое время.</summary>
    NoAnswer
}

/// <summary>
/// Тост подтверждения на устройстве. Текст ему даёт <see cref="ConfirmationText"/> —
/// он собран из фактических аргументов вызова, модельного резюме в нём нет никогда.
/// </summary>
public interface IConfirmationUi
{
    Task<ConfirmationDecision> AskAsync(ConfirmationPrompt prompt, TimeSpan wait, CancellationToken ct);
}

/// <summary>Что произошло с вызовом — строка ленты «что ушло в модель» в окне клиента.</summary>
/// <param name="At">Время события.</param>
/// <param name="CallId">Вызов, к которому относится строка.</param>
/// <param name="Kind">Вид вызова.</param>
/// <param name="ChatName">Имя чата — человек обязан видеть, чей это запрос.</param>
/// <param name="Title">Короткое описание, собранное из фактических аргументов.</param>
/// <param name="Outcome">Исход, если вызов уже закончился.</param>
/// <param name="Details">
/// Что именно уехало в модель — например размер кадра. Внутри сеанса кадр уходит без
/// отдельного нажатия, и человеку важно видеть не только «ушло», но и сколько.
/// </param>
public sealed record DesktopFeedEntry(
    DateTimeOffset At,
    string CallId,
    string Kind,
    string? ChatName,
    string Title,
    string? Outcome = null,
    string? Details = null);

/// <summary>
/// Лента вызовов в окне клиента. Внутри сеанса кадры уходят без отдельного нажатия —
/// иначе «посмотри, что за ошибка» неработоспособно, — но КАЖДЫЙ обязан быть виден
/// человеку здесь (ADR-008, «Сеанс рук и согласие»).
/// </summary>
public interface ICallFeed
{
    void Add(DesktopFeedEntry entry);
}

/// <summary>
/// Хранилище учётных данных устройства. Единственная реализация — DPAPI CurrentUser в
/// AiHomeDesktop.Windows.
///
/// Здесь лежит ТОЛЬКО device-токен: API-ключ владельца и пользовательский JWT на клиент не
/// копируются никогда — если решение потребует обратного, это ошибка проектирования.
/// </summary>
public interface ISecretStore
{
    DeviceCredentials? Read();

    void Save(DeviceCredentials credentials);

    void Clear();
}
