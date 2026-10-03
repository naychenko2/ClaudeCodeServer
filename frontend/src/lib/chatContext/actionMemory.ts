// Память выбора чипа действия поля ввода (ADR-023 §Д2, замена composerModeMemory): ключ —
// чат и основной объект `{kind:ref}`, значение — id действия или null («Чат»). Живёт вне
// компонента: поле ввода перемонтируется при каждой смене чата, а ручной уход в «Чат» обязан
// пережить возврат (поведение bac70c9aa). Предвыбор (`preset`) — отложенная просьба вертикали:
// хост применяет его один раз, когда основным становится объект с этим ключом.

import { useSyncExternalStore } from 'react';
import type { ActionPreset, ChatContextItem, ContextAction, ContextActor } from './types';

interface SessionMemory {
  // objectKey → id выбранного действия; null — выбран «Чат»
  choice: Map<string, string | null>;
  presets: Map<string, ActionPreset>;
}
const _memory = new Map<string, SessionMemory>();

// Подписка на выбор чипа: строка, панель и поле ввода живут в разных ветках дерева, а память —
// вне React. Версия растёт на каждую явную смену (клик, предвыбор, чистка), но не на запись
// умолчания внутри resolveAction: та идёт из рендера, и сигнал оттуда дал бы петлю
let _version = 0;
const _listeners = new Set<() => void>();
const emit = () => { _version++; _listeners.forEach(fn => fn()); };
export const subscribeActionMemory = (fn: () => void) => { _listeners.add(fn); return () => { _listeners.delete(fn); }; };
export const getActionMemoryVersion = () => _version;
// Хук-сигнал: компонент, читающий resolveAction, перерисуется при смене выбора
export function useActionMemoryVersion(): number {
  return useSyncExternalStore(subscribeActionMemory, getActionMemoryVersion, getActionMemoryVersion);
}

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
  const m = of(sessionId);
  m.choice.set(key, actionId);
  // Ручной выбор старше висящего предвыбора: иначе тот перебил бы клик и затравил поле позже
  m.presets.delete(key);
  emit();
}

export function presetAction(sessionId: string, key: string, preset: ActionPreset) {
  of(sessionId).presets.set(key, preset);
  emit();
}

// Запуск начат на объекте fromKey действием actionId: по итогу вертикаль ставит новую версию (другой
// ключ объекта), и выбор переезжает за ней, если у новой версии есть действие с тем же id
const _carry = new Map<string, { fromKey: string; actionId: string }>();
export const noteRunStarted = (sessionId: string, fromKey: string, actionId: string) => {
  _carry.set(sessionId, { fromKey, actionId });
};
// Запуск закончился ошибкой — версии не будет, выбору переезжать некуда
export const clearRunCarry = (sessionId: string) => { _carry.delete(sessionId); };
// Вид объекта — до первого «:» ключа; переезжает выбор только к объекту того же вида
const kindOfKey = (key: string) => key.slice(0, key.indexOf(':'));

export interface ResolvedAction {
  // id выбранного run-действия; null — «Чат»
  actionId: string | null;
  // Предвыбор, применённый в этом вызове (затравка текста и параметры); один раз
  preset?: ActionPreset;
}

// Какое действие выбрано у объекта. Порядок: предвыбор → память → умолчание. Память,
// указывающая на пропавшее или недоступное действие, даёт «Чат»; ручной «Чат» держится, пока
// ключ объекта тот же. Только run-действия бывают выбраны. consume=false — «подглядеть»: так читают
// строка и панель, предвыбор остаётся ждать хоста поля, который один и применяет его (затравка
// текста и параметры живут у него)
export function resolveAction(
  sessionId: string, key: string, by: ContextActor, actions: readonly ContextAction[], consume = true,
): ResolvedAction {
  const m = of(sessionId);
  const usable = (id: string) => actions.some(a => a.id === id && a.kind === 'run' && !a.disabledReason);
  const preset = m.presets.get(key);
  if (preset && usable(preset.actionId)) {
    if (!consume) return { actionId: preset.actionId };
    m.presets.delete(key);
    m.choice.set(key, preset.actionId);
    return { actionId: preset.actionId, preset };
  }
  if (m.choice.has(key)) {
    const id = m.choice.get(key)!;
    return { actionId: id !== null && usable(id) ? id : null };
  }
  // Объект сменился после запуска: «Чат» у агентского объекта, иначе то же действие, если оно есть
  const carry = _carry.get(sessionId);
  if (carry && carry.fromKey !== key && kindOfKey(carry.fromKey) === kindOfKey(key)) {
    // Действий ещё нет — carry ждёт: решение «Чат» при пустом списке запомнилось бы навсегда
    if (actions.length === 0) return { actionId: null };
    _carry.delete(sessionId);
    const id = by !== 'agent' && usable(carry.actionId) ? carry.actionId : null;
    m.choice.set(key, id);
    return { actionId: id };
  }
  const actionId = pickDefaultAction(actions, by);
  // Действий ещё нет (вертикаль догружается позже контекста): умолчание не запоминаем, иначе оно
  // навсегда осело бы как «ручной Чат»
  if (actions.length > 0) m.choice.set(key, actionId);
  return { actionId };
}

// Хост поля применяет предвыбор вертикали ОДИН раз и вне рендера (StrictMode зовёт рендер дважды,
// и расход предвыбора из него пропал бы): выбор встаёт на действие предвыбора, остальные читатели
// увидят его по сигналу памяти. null — предвыбора нет или действие недоступно
export function takePreset(
  sessionId: string, key: string, by: ContextActor, actions: readonly ContextAction[],
): ActionPreset | null {
  const { preset } = resolveAction(sessionId, key, by, actions, true);
  if (preset) emit();
  return preset ?? null;
}

// Удаление чата
export function forgetActionMemory(sessionId: string) { _memory.delete(sessionId); _carry.delete(sessionId); emit(); }

// Выход из аккаунта: память прошлого владельца вкладка не держит
export function resetActionMemory() { _memory.clear(); _carry.clear(); emit(); }
