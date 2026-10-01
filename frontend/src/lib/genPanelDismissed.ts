// Признак «человек закрыл панель генерации в этом чате» (ADR-021 §3).
//
// Выбор картинки или звука человеком открывает панель сам — но только пока в этом
// чате её не закрыли: ✕ в шапке и закрытие пунктом рельсы ставят признак, а сводка в
// полосе и пункт рельсы открывают панель всегда и признак НЕ снимают (закрыл — дальше
// только руками). Выбор, сделанный агентом, autoReveal не зовёт вовсе.
//
// Это настройка вида, как ширина панели: живёт в localStorage, серверных префов и
// UpdatedAt чата не трогает. Ключ — `{sessionId}:{panelKey}`, общий список на все
// чаты; удаление чата его не чистит, поэтому держим не больше MAX_KEYS последних.
import { revealWorkspacePanel } from './subsystems/registryCore';

const STORAGE_KEY = 'cc_gen_panel_dismissed';
export const MAX_KEYS = 500;
export const GEN_PANEL_KEYS: readonly string[] = ['images', 'sound'];

export function isGenPanelKey(key: string): boolean {
  return GEN_PANEL_KEYS.includes(key);
}

// Порядок — от старых к свежим: вытесняются ключи с начала
function read(): string[] {
  try {
    const raw = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]');
    return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

function write(keys: string[]) {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(keys)); } catch { /* квота — признак не критичен */ }
}

const keyOf = (sessionId: string, panelKey: string) => `${sessionId}:${panelKey}`;

export function isGenPanelDismissed(sessionId: string, panelKey: string): boolean {
  return read().includes(keyOf(sessionId, panelKey));
}

// Человек закрыл панель в чате. Чужие ключи и вызов без чата пропускаются, чтобы
// хост мог звать это на любом закрытии панели, не разбирая ключ сам
export function markGenPanelDismissed(sessionId: string | null | undefined, panelKey: string) {
  if (!sessionId || !isGenPanelKey(panelKey)) return;
  const k = keyOf(sessionId, panelKey);
  const keys = read().filter(x => x !== k);
  keys.push(k);
  write(keys.length > MAX_KEYS ? keys.slice(keys.length - MAX_KEYS) : keys);
}

// Выбор картинки/звука человеком: открыть панель, если в этом чате её не закрывали.
// true — панель запрошена
export function autoRevealGenerationPanel(panelKey: string, sessionId: string | null | undefined, tab?: string): boolean {
  if (!sessionId || !isGenPanelKey(panelKey) || isGenPanelDismissed(sessionId, panelKey)) return false;
  revealWorkspacePanel(panelKey, tab);
  return true;
}
