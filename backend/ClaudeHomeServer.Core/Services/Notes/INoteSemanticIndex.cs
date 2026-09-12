using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Узкий Core-шов семантического индекса заметок (Этап 5, под-волна «разрыв
// Main → NotesKnowledgeService»): сам `NotesKnowledgeService` живёт в сборке
// `ClaudeHomeServer.Notes`, а потребители в спине держат этот Core-контракт.
//
// Состав собран ПО ФАКТИЧЕСКИМ вызовам спины (разведка 2026-09-12), не «на вырост»:
//  - Available — гейт деградации (Dify не настроен → индекс тихо выключен);
//  - GetDatasetId — датасет заметок «{username}:notes» как валидная цель привязок персон;
//  - QueueSync — отложенная переиндексация после мутации заметок (удаление проекта);
//  - SearchAsync — семантический поиск (контекст постановки задачи, привязки персон,
//    единый поиск, recall-секция промпта).
//
// Полного API индекса здесь НЕТ: полная переиндексация (`SyncAllAsync`), каскады
// жизненного цикла (`DeleteUser`/`DeleteAllAsync`), цели реконсайлера (`ListTargets`)
// и статический форматтер промпта (`BuildRecallBlock`) остаются внутри вертикали —
// их зовут `NotesController`/`NotesSubsystem`/`NotesRecallContributor` и реконсайлер
// Knowledge, а не спина.
//
// Реализация — сам `NotesKnowledgeService` в вертикали: интерфейс объявлен под его
// сигнатуры, отдельного класса-адаптера нет (тот же приём, что у `INoteAccessor`).
// Тип `NoteSemanticHit` живёт в Core (Models/Note.cs) — новых DTO шов не заводит.
public interface INoteSemanticIndex
{
    // Готов ли индекс к работе (настроен Dify). false — прочие вызовы тихо пусты.
    bool Available { get; }

    // Id Dify-датасета заметок владельца («{username}:notes»); null — индекс ещё не создавался.
    string? GetDatasetId(string userId);

    // Отложенная синхронизация после мутации заметок (дебаунс — частые правки не спамят Dify).
    void QueueSync(string userId);

    // Семантический поиск: чанки Dify → заметки пользователя (по маппингу docId → noteId).
    Task<IReadOnlyList<NoteSemanticHit>> SearchAsync(string userId, string query, int topK = 8);
}
