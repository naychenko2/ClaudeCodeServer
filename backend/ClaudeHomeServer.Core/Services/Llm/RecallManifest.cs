namespace ClaudeHomeServer.Services.Llm;

// Элемент манифеста recall — что персона подтянула в ход (память/заметка/база/команда) для
// атрибуции «опирается на…» / «использовано сейчас» (F3). Kind ∈ memory|note|knowledge|team|
// dossier (team — память команды проекта, ③-3.4; dossier — паспорт изменения, ADR-004 §5);
// Ref — id/ссылка.
//
// Этап 5, шаг 2: record-тип переехал в Core, чтобы Turn ссылался на Core-DTO
// без зависимости от вертикали Llm (разрыв цикла Llm ⇄ Turn). Реализация
// record'а в Llm больше не нужна — тип тонкий, не трогается внешним кодом
// кроме контрибьюторов секций промпта и шины Turn (Services/Turn).
public sealed record RecallItem(string Kind, string? Ref, string Title, string? Snippet);

// Результат recall-провайдера: текст для системного промпта + айтемы манифеста (F3).
// DossierText — блок паспортов изменений ОТДЕЛЬНО от Text (план «Секции промптов» этап 3,
// флаг specialty-prompt-sections): секция dossier-recall клеится своим местом промпта;
// null — флаг выключен/досье нет (тогда, если есть, оно уже внутри Text — как до фичи).
public sealed record RecallBlock(string? Text, IReadOnlyList<RecallItem> Items, string? DossierText = null);
