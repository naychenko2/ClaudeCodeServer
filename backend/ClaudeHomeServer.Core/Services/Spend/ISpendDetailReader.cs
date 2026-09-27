using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Узкий шов для чтения детальных записей расхода по диапазону дат.
// Отдельный от ISpendCollector: коллектор пишет, а здесь — только чтение деталей
// (не агрегатов). Потребители вне вертикали Spend (IncidentLocalContext) зависят от
// интерфейса, а не от конкретного SpendStore.
public interface ISpendDetailReader
{
    List<SpendRecord> DetailsBetween(DateOnly from, DateOnly to);
}
