// Память выбора чипа действия поля ввода (ADR-023 §Д2, замена composerModeMemory): ключ —
// чат и основной объект `{kind:ref}`, значение — id действия или null («Чат»). Живёт вне
// компонента: поле ввода перемонтируется при каждой смене чата, а ручной уход в «Чат» обязан
// пережить возврат (поведение bac70c9aa). Предвыбор (`preset`) — отложенная просьба вертикали:
// хост применяет его один раз, когда основным становится объект с этим ключом.

import type { ActionPreset, ChatContextItem, ContextAction, ContextActor } from './types';

interface SessionMemory {
  // objectKey → id выбранного действия; null — выбран «Чат»
  choice: Map<string, string | null>;
  presets: Map<string, ActionPreset>;
}
const _memory = new Map<string, SessionMemory>();

function of(sessionId: string): SessionMemory {
  let m = _memory.get(sessionId);
  if (!m) { m = { choice: new Map(), presets: new Map() }; _memory.set(sessionId, m); }
  return m;
}

// Канонический JSON: порядок ключей ref не влияет на ключ объекта
function canon(v: unknown): string {
  if (Array.isArray(v)) return `[${v.map(canon).join(',')}]`;
  if (v && typeof v === 'object') {
    const o = v as Record<string, unknown>;
    return `{${Object.keys(o).sort().map(k => `${JSON.stringify(k)}:${canon(o[k])}`).join(',')}}`;
  }
  return JSON.stringify(v) ?? 'null';
}

// Ключ объекта: вид и ссылка, но не id элемента (он меняется при каждом setPrimary того же объекта)
export const objectKey = (item: Pick<ChatContextItem, 'kind' | 'ref'>): string => `${item.kind}:${canon(item.ref)}`;

// Умолчание хоста (Р1): объект человека — первое run-действие без disabledReason, иначе «Чат»
// (null); объект агента — всегда «Чат»
export function pickDefaultAction(actions: readonly ContextAction[], by: ContextActor): string | null {
  if (by === 'agent') return null;
  return actions.find(a => a.kind === 'run' && !a.disabledReason)?.id ?? null;
}

// Человек выбрал чип (id действия или null — «Чат»)
export function rememberAction(sessionId: string, key: string, actionId: string | null) {
  of(sessionId).choice.set(key, actionId);
}

export function presetAction(sessionId: string, key: string, preset: ActionPreset) {
  of(sessionId).presets.set(key, preset);
}

export interface ResolvedAction {
  // id выбранного run-действия; null — «Чат»
  actionId: string | null;
  // Предвыбор, применённый в этом вызове (затравка текста и параметры); один раз
  preset?: ActionPreset;
}

// Какое действие выбрано у объекта. Порядок: предвыбор → память → умолчание. Память,
// указывающая на пропавшее или недоступное действие, даёт «Чат»; ручной «Чат» держится, пока
// ключ объекта тот же. Только run-действия бывают выбраны
export function resolveAction(
  sessionId: string, key: string, by: ContextActor, actions: readonly ContextAction[],
): ResolvedAction {
  const m = of(sessionId);
  const usable = (id: string) => actions.some(a => a.id === id && a.kind === 'run' && !a.disabledReason);
  const preset = m.presets.get(key);
  if (preset && usable(preset.actionId)) {
    m.presets.delete(key);
    m.choice.set(key, preset.actionId);
    return { actionId: preset.actionId, preset };
  }
  if (m.choice.has(key)) {
    const id = m.choice.get(key)!;
    return { actionId: id !== null && usable(id) ? id : null };
  }
  const actionId = pickDefaultAction(actions, by);
  m.choice.set(key, actionId);
  return { actionId };
}

// Удаление чата
export function forgetActionMemory(sessionId: string) { _memory.delete(sessionId); }

// Выход из аккаунта: память прошлого владельца вкладка не держит
export function resetActionMemory() { _memory.clear(); }
