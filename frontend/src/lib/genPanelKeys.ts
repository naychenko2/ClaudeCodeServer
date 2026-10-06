// Ключи панелей генерации (ADR-023 §Д1, §Д6).
//
// Справа живёт одна панель «Контекст» вместо прежних «Картинок», «Звука» и «Видео». Единственный
// источник: panelCatalog.ts и genPanelDismissed.ts отдают его же, чтобы две копии списка не разошлись.
// Модуль без зависимостей от страниц и панелей — его читает и registryCore.

export const CONTEXT_PANEL_KEY = 'chatContext';
export const GEN_PANEL_KEYS: readonly string[] = [CONTEXT_PANEL_KEY];
export const genPanelKeys = (): readonly string[] => GEN_PANEL_KEYS;

// Упразднённые ключи панелей генерации; сохранённая раскладка переводит их в «Контекст» (LEGACY_KEY_ALIASES)
export const LEGACY_GEN_PANEL_KEYS: readonly string[] = ['images', 'sound', 'videoEditor'];

// Старый вызов с ключом упразднённой панели попадает в «Контекст»
export const toGenPanelKey = (key: string): string => (LEGACY_GEN_PANEL_KEYS.includes(key) ? CONTEXT_PANEL_KEY : key);
