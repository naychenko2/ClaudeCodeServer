// Ключи панелей генерации в зависимости от флага composer-context-row (ADR-023 §Д1, §Д6).
//
// Без флага справа живут «Картинки» и «Звук», с флагом — одна панель «Контекст». Единственный
// источник: panelCatalog.ts и genPanelDismissed.ts отдают его же, чтобы две копии списка не
// разошлись (их равенство держит contextPanelHost.test.ts). Модуль без зависимостей от
// страниц и панелей — его читает и registryCore.
import { FLAGS, getFlag } from './featureFlags';

export const CONTEXT_PANEL_KEY = 'chatContext';
export const LEGACY_GEN_PANEL_KEYS: readonly string[] = ['images', 'sound'];

const isOn = () => getFlag(FLAGS.composerContextRow);

// Ключи панелей генерации сейчас: при флаге — одна «Контекст»
export const genPanelKeys = (): readonly string[] => (isOn() ? [CONTEXT_PANEL_KEY] : LEGACY_GEN_PANEL_KEYS);

// Старый вызов вертикали с ключом «Картинки»/«Звук» при флаге попадает в «Контекст»: пока вертикали
// не переехали на revealContextPanel (2к, 2з), без этого их показ панели молча терялся бы
export const toGenPanelKey = (key: string): string =>
  isOn() && LEGACY_GEN_PANEL_KEYS.includes(key) ? CONTEXT_PANEL_KEY : key;
