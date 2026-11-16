// Типы домена «Чат»: фазы старта хода (переехали из types/index.ts, дефект 39765836).
// Реэкспорта `export type *` в хребет нет: новый реэкспорт домена в общий файл
// красный (domainReexports.test.ts) — потребители читают эти типы с этого адреса,
// а types/index.ts держает только `import type` для ServerMessage (turn_start_phase).

// Фазы старта хода, которые показывает строка статуса в ленте
export type StartPhase = 'queued' | 'context' | 'launching' | 'waiting';
