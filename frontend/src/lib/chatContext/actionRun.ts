// Единая точка запуска действия поля ввода (ADR-023 §Д2, Р3): кнопка поля и низ панели «Контекст» читают
// один и тот же ActionRun — подпись, цену, состояние — и зовут один `run`. Вторая логика запуска
// невозможна по построению. Состояние хранится вне React (по чату): поле и панель живут в разных
// ветках дерева и обязаны видеть один прогресс. Здесь — ядро без React, хук — в useActionRun.ts.

import { noteRunStarted, objectKey, resolveAction } from './actionMemory';
import { acceptConflict } from './store';
import { showToast } from '../toast';
import type {
  ActionQuote, ActionResult, ActionRun, ChatContextPrimary, ChatContextRef, ContextAction, ContextKindApi,
  ContextKindCtx, LaunchParam, LaunchParams, QuoteRequest, RunState,
} from './types';

// ── Состояние по чату ──

interface QuoteSlot { key: string; value: ActionQuote | null }

export interface RunEntry {
  // Область: объект + действие. Смена сбрасывает параметры и итог прошлого запуска
  scope: string;
  // Текст поля ввода: его публикует поле, читает панель
  text: string;
  // Значения параметров: по `kind` закрытого набора и по `question.param`
  values: Record<string, number | string>;
  state: RunState;
  progress: number | null;
  result: ActionResult | null;
  quote: QuoteSlot | null;
  // Ключ запроса цены, который сейчас в полёте: второй читатель того же ключа не дублирует запрос
  quoting: string | null;
}

const _runs = new Map<string, RunEntry>();
const _unwatch = new Map<string, () => void>();
const _listeners = new Set<() => void>();
let _version = 0;
const emit = () => { _version++; _listeners.forEach(fn => fn()); };
export const subscribeRun = (fn: () => void) => { _listeners.add(fn); return () => { _listeners.delete(fn); }; };
export const getRunVersion = () => _version;

const fresh = (scope: string): RunEntry => ({
  scope, text: '', values: {}, state: 'idle', progress: null, result: null, quote: null, quoting: null,
});

export function runEntry(sessionId: string, scope: string): RunEntry {
  const e = _runs.get(sessionId);
  // Запущенное не сбрасываем сменой области: прогресс принадлежит запуску, а не выбору
  if (e && (e.scope === scope || e.state === 'running')) return e;
  const next = fresh(scope);
  // Текст поля переживает смену области: им владеет поле, у него свои черновики
  if (e) next.text = e.text;
  _runs.set(sessionId, next);
  return next;
}

const patch = (sessionId: string, p: Partial<RunEntry>) => {
  const e = _runs.get(sessionId);
  if (!e) return;
  Object.assign(e, p);
  emit();
};

export function setRunText(sessionId: string, scope: string, text: string) {
  const e = runEntry(sessionId, scope);
  if (e.text === text) return;
  e.text = text;
  emit();
}

export function setRunParam(sessionId: string, scope: string, name: string, value: number | string) {
  const e = runEntry(sessionId, scope);
  if (e.values[name] === value) return;
  e.values = { ...e.values, [name]: value };
  emit();
}

// ── Чистые вычисления ──

// Параметры вида с текущими значениями; fromQuestion своего значения не несёт
export function mergeParams(base: readonly LaunchParam[], values: Readonly<Record<string, number | string>>): LaunchParam[] {
  return base.map(p => {
    const v = values[p.kind];
    return v === undefined || p.kind === 'fromQuestion' ? p : { ...p, value: v } as LaunchParam;
  });
}

// Параметры запроса: закрытый набор параметров панели плюс ответ на вопрос под чипами
// (первое значение предвыбрано)
export function launchParams(action: ContextAction, params: readonly LaunchParam[], values: Readonly<Record<string, number | string>>): LaunchParams {
  const out: Record<string, string | number | boolean> = {};
  for (const p of params) {
    if (p.kind === 'variants' || p.kind === 'duration' || p.kind === 'aspect') out[p.kind] = p.value;
  }
  const q = action.question;
  if (q) out[q.param] = values[q.param] ?? q.options[0]?.value ?? '';
  return out;
}

export const questionValue = (action: ContextAction, values: Readonly<Record<string, number | string>>): string | null =>
  action.question ? String(values[action.question.param] ?? action.question.options[0]?.value ?? '') : null;

// Подпись кнопки (Р3), одна на поле и панель: `✦ Изменить · 3 вар. · $0.12`. Длительность пишется всегда,
// вариантов — только больше одного; цена не режется никогда, на телефоне «×3» вместо «3 вар.»
export function runLabel(o: {
  action: ContextAction | null; params: readonly LaunchParam[]; price: string | null; mobile: boolean;
  state: RunState; progress: number | null;
}): string {
  if (!o.action) return '';
  if (o.state === 'running') {
    const pct = o.progress === null ? '' : ` ${Math.round(o.progress * 100)} %`;
    return `✦ ${o.action.label}…${pct}`;
  }
  const parts: string[] = [];
  for (const p of o.params) {
    if (p.kind === 'duration') parts.push(`${p.value} с`);
    if (p.kind === 'variants' && p.value > 1) parts.push(o.mobile ? `×${p.value}` : `${p.value} вар.`);
  }
  if (o.price) parts.push(o.price);
  return [`✦ ${o.action.label}`, ...parts].join(' · ');
}

// Почему кнопка серая: серое действие или пустое поле у действия с обязательным текстом
export function runBlockReason(action: ContextAction | null, text: string): string | null {
  if (!action) return null;
  if (action.disabledReason) return action.disabledReason;
  if (action.text === 'required' && !text.trim()) return `Напишите текст: ${action.placeholder ?? action.label}`;
  return null;
}

// ── Цена ──

const quoteKey = (req: { op: string; text: string; params: LaunchParams; contextRevision: number }) => JSON.stringify(req);

// Запрос цены вида; тот же ключ второй раз не уходит. Отказ цены не мешает запуску — цены просто нет
export async function ensureQuote(
  sessionId: string, scope: string, api: ContextKindApi, ctx: ContextKindCtx,
  req: { op: string; text: string; params: LaunchParams; contextRevision: number },
) {
  if (!api.quote) return;
  const e = runEntry(sessionId, scope);
  const key = quoteKey(req);
  if (e.quote?.key === key || e.quoting === key) return;
  e.quoting = key;
  try {
    const value = await api.quote(ctx, req);
    if (_runs.get(sessionId) === e) patch(sessionId, { quote: { key, value }, quoting: null });
  } catch (err) {
    if (_runs.get(sessionId) === e) {
      // Ревизия устарела — свежий контекст уже в сторе, цена пересчитается сама новым ключом
      acceptConflict(sessionId, err, true);
      patch(sessionId, { quote: { key, value: null }, quoting: null });
    }
  }
}

// ── Запуск ──
// Отказ запуска (409, сеть) причину уже показал и бросает исключение: поле ввода по нему оставляет набранный
// текст, как у режимов подсистем. Запуск сам не повторяется

export interface RunDeps {
  sessionId: string;
  api: ContextKindApi;
  ctx: ContextKindCtx;
  primary: ChatContextPrimary;
  action: ContextAction;
  scope: string;
  params: LaunchParams;
  revision: number;
}

export async function launchAction(d: RunDeps, text: string): Promise<void> {
  const e = runEntry(d.sessionId, d.scope);
  if (e.state === 'running') return;
  if (!d.api.launch || !d.action.op) {
    showToast(`«${d.action.label}» пока нельзя запустить`, '', 'error');
    return;
  }
  patch(d.sessionId, { state: 'running', progress: 0, result: null });
  _unwatch.get(d.sessionId)?.();
  try {
    const handle = await d.api.launch(d.ctx, { op: d.action.op, text, params: d.params, contextRevision: d.revision });
    noteRunStarted(d.sessionId, objectKey(d.primary), d.action.id);
    let off: (() => void) | null = null;
    off = handle.watch(ev => {
      if (ev.error) { patch(d.sessionId, { state: 'error', progress: null }); showToast(ev.error, '', 'error'); off?.(); return; }
      if (ev.result) { patch(d.sessionId, { state: 'done', progress: 1, result: ev.result }); return; }
      if (ev.progress !== undefined) patch(d.sessionId, { progress: ev.progress });
    });
    _unwatch.set(d.sessionId, off);
  } catch (err) {
    // Контекст сменился: DTO уже в сторе, цену пересчитает новый ключ; запуск сам не повторяется
    if (acceptConflict(d.sessionId, err)) { patch(d.sessionId, { state: 'idle', progress: null, quote: null }); throw err; }
    patch(d.sessionId, { state: 'error', progress: null });
    showToast((err as Error).message || 'Не удалось запустить', '', 'error');
    throw err;
  }
}

// Состояние запуска после «готово»: следующий выбор начинает с чистого листа
export function resetRun(sessionId: string) {
  _unwatch.get(sessionId)?.();
  _unwatch.delete(sessionId);
  _runs.delete(sessionId);
  emit();
}

export function resetAllRuns() {
  _unwatch.forEach(off => off());
  _unwatch.clear(); _runs.clear();
  emit();
}

// ── Сборка ActionRun ──

// Одна сборка на всех: поле ввода и панель зовут её (через хук) с одним и тем же состоянием
export function buildActionRun(o: {
  sessionId: string;
  api: ContextKindApi | null;
  ctx: ContextKindCtx;
  primary: ChatContextPrimary | null;
  refs: readonly ChatContextRef[];
  revision: number;
}): ActionRun & { scope: string; req: QuoteRequest | null } {
  const none = { action: null, label: '', quote: null, state: 'idle' as const, progress: null, result: null, text: '', answer: null, blocked: null, params: [] as readonly LaunchParam[], scope: '', req: null };
  const { sessionId, api, ctx, primary, refs, revision } = o;
  if (!api || !primary) return { ...none, setText: () => {}, setParam: () => {}, run: async () => {} };
  const actions = api.actions(ctx, { primary, refs });
  const { actionId } = resolveAction(sessionId, objectKey(primary), primary.by, actions, false);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  if (!action) return { ...none, setText: () => {}, setParam: () => {}, run: async () => {} };
  const scope = `${objectKey(primary)}:${action.id}`;
  const e = runEntry(sessionId, scope);
  const params = mergeParams(api.params?.(ctx, action.id) ?? [], e.values);
  const lp = launchParams(action, params, e.values);
  const req: QuoteRequest = { op: action.op ?? action.id, text: e.text, params: lp, contextRevision: revision };
  const price = e.quote?.key === quoteKey(req) ? e.quote.value : null;
  return {
    action,
    scope,
    req,
    label: runLabel({ action, params, price: price?.price ?? null, mobile: ctx.isMobile, state: e.state, progress: e.progress }),
    quote: price,
    state: e.state,
    progress: e.progress,
    result: e.result,
    text: e.text,
    answer: questionValue(action, e.values),
    blocked: runBlockReason(action, e.text),
    params,
    setText: t => setRunText(sessionId, scope, t),
    setParam: (name, v) => setRunParam(sessionId, scope, name, v),
    run: text => launchAction({ sessionId, api, ctx, primary, action, scope, params: lp, revision }, text),
  };
}
