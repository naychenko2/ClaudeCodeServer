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
// «Видео» (ADR-022) в строку контекста не переехало и живёт отдельной панелью при любом флаге
export const VIDEO_PANEL_KEY = 'videoEditor';
const GEN_CONTEXT: readonly string[] = [CONTEXT_PANEL_KEY, VIDEO_PANEL_KEY];
const GEN_LEGACY: readonly string[] = [...LEGACY_GEN_PANEL_KEYS, VIDEO_PANEL_KEY];
export const genPanelKeys = (): readonly string[] => (isOn() ? GEN_CONTEXT : GEN_LEGACY);

// Старый вызов вертикали с ключом «Картинки»/«Звук» при флаге попадает в «Контекст»: пока вертикали
// не переехали на revealContextPanel (2к, 2з), без этого их показ панели молча терялся бы
export const toGenPanelKey = (key: string): string =>
  isOn() && LEGACY_GEN_PANEL_KEYS.includes(key) ? CONTEXT_PANEL_KEY : key;
