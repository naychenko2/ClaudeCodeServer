using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Composition;

// Запись модуля в ленту чата вне хода (ADR-019 §2): тихая строка, якорь карточки. Общий шов
// для всех подсистем — модуль не видит SessionManager, а писать историю и рассылать её умеет
// только ядро. Реализация — адаптер ChatFeed в Main поверх SessionManager.AppendStoredAsync.
public interface IChatFeed
{
    // Дописать запись в history.json чата и разослать живую пару ModuleRecordMessage. Запись —
    // активность: UpdatedAt двигается, чат поднимается в списке и выходит из архива.
    // Модель запись не видит (история, а не транскрипт CLI). Владение и проект проверяет
    // вызывающий. false — чата нет.
    Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default);
}
