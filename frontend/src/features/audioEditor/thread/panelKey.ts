// Ключи панели и полосы «Звук» отдельным файлом без зависимостей: их читают и стор нитей, и context/reveal.ts,
// а общий импорт через стор дал бы цикл
export const SOUND_STRIP = 'sound';
export const SOUND_PANEL = 'sound';
// Библиотека «Голоса» отдельной панелью зоны при флаге composer-context-row (ADR-023 §Д1, 2з-3)
export const VOICES_KEY = 'voices';
