// Типы домена «История решений» (переехали из types/index.ts, S5-P7). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// --- История решений (change-dossiers, этап 1) ---
// Запись у файла/символа/коммита: «зачем менялось, что решили, что отвергли, какие
// грабли» — AI-выжимка из чата/задачи, привязанная к коммиту. Контракт REST —
// GET /api/projects/{id}/dossiers?file=|symbol=|commit= (ADR-004 §4/§8).
export type DossierStatus = 'active' | 'degraded' | 'archived';

// Происхождение записи (этап 4 — импорт «Историй решений»). own — норма,
// imported — приехала из ветки ccs/dossiers/v1 (см. ADR-004 §6). В UI своя запись
// пометки не получает: 90% списка — свои, и пилюля «Своя» превращает ленту в шум.
export type DossierOrigin = 'own' | 'imported';

export interface DossierEntry {
  id: string;
  commitSha: string;
  commitSubject: string;
  committedAt: string;        // ISO
  sessionId: string | null;   // чат-источник — может протухнуть (sessionId остаётся, см. linksStale)
  taskId: string | null;      // задача-источник — может протухнуть
  personaId: string | null;   // null — коммит человека-владельца, иначе персона
  files: string[];
  symbols: string[];
  why: string;
  decisions: string[];
  rejected: string[];
  pitfalls: string[];
  invariants: string[];
  // active — код почти не менялся с записи; degraded — файл заметно переписан
  // (нюанс, не ошибка); archived — символ исчез из графа (показывается только по запросу)
  status: DossierStatus;
  // Выжимка не собралась — сохранён только факт изменения, why не показываем
  summaryFailed: boolean;
  // Чат/задача, на которые указывают sessionId/taskId, больше не существуют —
  // соответствующие ссылки гасятся текстом вместо кнопки
  linksStale: boolean;
  // Происхождение записи (этап 4): own — записана здесь, imported — приехала из
  // ветки ccs/dossiers/v1. У импортированных заполнен importedAuthor — имя автора
  // tip-коммита ветки (кто нажал «Выгрузить», тот и подписан).
  origin: DossierOrigin;
  importedAuthor: string | null;
  // Момент снятия паспорта для own-записей; у импортированных null — там время брать
  // неоткуда, и строка в карточке для них не рисуется (см. DossierHistoryPanel).
  capturedAt?: string | null;
}
