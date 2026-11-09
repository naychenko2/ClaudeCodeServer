// Лист-модуль с ключами записей модуля «Плагины» (без тяжёлых импортов).
// Манифест (plugins/manifest.tsx) не может тянуть records.tsx в статический граф
// (глубокая цепочка DraftFeedCard → api/signalr → цикл M1), а records.tsx раньше
// дублировал те же константы. Оба берут их отсюда: манифест — напрямую, records.tsx
// — ре-экспортом (публичная поверхность модуля не меняется).
export const MODULE = 'plugins';
export const recordKey = (recordType: string) => `${MODULE}:${recordType}`;
export const RECORD_DRAFT = 'plugin_draft';
