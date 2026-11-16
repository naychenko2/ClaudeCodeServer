// Типы домена «Чат»: фазы старта хода (переехали из types/index.ts, дефект 39765836).
// Реэкспорта `export type *` в хребет нет: новый реэкспорт домена в общий файл
// красный (domainReexports.test.ts) — потребители читают эти типы с этого адреса,
// а types/index.ts держит `import type` только для ChatItem (turn_start_phase):
// ServerMessage.phase остаётся string намеренно — редьюсер проверяет известные фазы
// по PHASE_KEYS (lib/chatReducer.ts), неизвестная от сервера не должна ломать типизацию.

// Фазы старта хода, которые показывает строка статуса в ленте
export type StartPhase = 'queued' | 'context' | 'launching' | 'waiting';
