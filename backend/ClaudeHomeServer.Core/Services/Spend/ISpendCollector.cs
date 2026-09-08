using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Точка записи расхода для всех источников (ходы, one-shot, fal, бесплатные модели).
// Интерфейс, а не класс — чтобы точки сбора (SessionManager, раннеры, HTTP-клиенты)
// мокались в тестах без файлового стора.
//
// Объявление переехало в Core (этап 5, шаг 1) — вертикали Llm/SessionManager/потребители
// Spend ссылаются на ISpendCollector как на Core-DTO, и цикл Llm ⇄ Spend рвётся при переезде
// Spend в отдельный .csproj. Реализация `SpendStore : ISpendCollector` остаётся в
// `ClaudeHomeServer/Services/Spend/SpendStore.cs` и продолжает жить в сборке Main,
// пока сама вертикаль Spend не уедет в свой проект (следующий этап).
public interface ISpendCollector
{
    void Record(SpendRecord record);
}
