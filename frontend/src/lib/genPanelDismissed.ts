// Признак «человек закрыл панель генерации в этом чате» (ADR-021 §3).
//
// «✦ Новый звук», «Нарисовать новую», ярлыки и «Редактировать» из дерева открывают панель
// сами — но только пока в этом чате её не закрыли: ✕ в шапке и закрытие пунктом рельсы
// ставят признак, а сводка в полосе и пункт рельсы открывают панель всегда и признак НЕ
// снимают (закрыл — дальше только руками). Клик по карточке в ленте закрытую панель не
// открывает вовсе (genPanelFollow), выбор агентом autoReveal не зовёт.
//
// Это настройка вида, как ширина панели: живёт в localStorage, серверных префов и
// UpdatedAt чата не трогает. Ключ — `{sessionId}:{panelKey}`, общий список на все
// чаты; удаление чата его не чистит, поэтому держим не больше MAX_KEYS последних.
import { genPanelKeys, toGenPanelKey } from './genPanelKeys';
import { revealWorkspacePanel } from './subsystems/registryCore';

const STORAGE_KEY = 'cc_gen_panel_dismissed';
export const MAX_KEYS = 500;
// Ключи панелей генерации: одна «chatContext» (ADR-023 §Д1)
export { genPanelKeys };

export function isGenPanelKey(key: string): boolean {
  return genPanelKeys().includes(key);
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
  return read().includes(keyOf(sessionId, toGenPanelKey(panelKey)));
}

// Человек закрыл панель в чате. Чужие ключи и вызов без чата пропускаются, чтобы
// хост мог звать это на любом закрытии панели, не разбирая ключ сам
export function markGenPanelDismissed(sessionId: string | null | undefined, panelKey: string) {
  const key = toGenPanelKey(panelKey);
  if (!sessionId || !isGenPanelKey(key)) return;
  const k = keyOf(sessionId, key);
  const keys = read().filter(x => x !== k);
  keys.push(k);
  write(keys.length > MAX_KEYS ? keys.slice(keys.length - MAX_KEYS) : keys);
}

// Выбор картинки/звука человеком: открыть панель, если в этом чате её не закрывали.
// true — панель запрошена
export function autoRevealGenerationPanel(panelKey: string, sessionId: string | null | undefined, tab?: string): boolean {
  const key = toGenPanelKey(panelKey);
  if (!sessionId || !isGenPanelKey(key) || isGenPanelDismissed(sessionId, key)) return false;
  // Вкладок у «Контекста» нет: при переводе старого ключа вкладка отбрасывается
  revealWorkspacePanel(key, key === panelKey ? tab : undefined, { sessionId });
  return true;
}
