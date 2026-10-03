// Единая точка запуска действия поля ввода (ADR-023 §Д2, Р3): кнопка поля и низ панели «Контекст» читают
// один и тот же ActionRun — подпись, цену, состояние — и зовут один `run`. Вторая логика запуска
// невозможна по построению. Состояние хранится вне React (по чату): поле и панель живут в разных
// ветках дерева и обязаны видеть один прогресс. Здесь — ядро без React, хук — в useActionRun.ts.

import { isReported } from './errors';
import { baseName } from './labels';
import { clearRunCarry, noteRunStarted, objectKey, resolveAction } from './actionMemory';
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
// Остановка идущего запуска: у дескриптора может не быть
const _cancel = new Map<string, () => void | Promise<void>>();
const _listeners = new Set<() => void>();
let _version = 0;
const emit = () => { _version++; _listeners.forEach(fn => fn()); };
export const subscribeRun = (fn: () => void) => { _listeners.add(fn); return () => { _listeners.delete(fn); }; };
export const getRunVersion = () => _version;
// Внешнее состояние вида (нити, отметки, каталог, настройки) изменилось: хост пересчитает действия, «Чем» и цену
export const notifyKindChanged = emit;

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

// Число вариантов, выбранное человеком: переживает запуск и смену объекта в этом чате
const _variants = new Map<string, number>();

export function setRunParam(sessionId: string, scope: string, name: string, value: number | string) {
  if (name === 'variants' && typeof value === 'number') _variants.set(sessionId, value);
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

// Подпись кнопки (Р3) двумя кусками: имя действия (ужимается многоточием, на узком экране первым) и хвост
// «параметры · цена» (не ужимается никогда). Хвост несёт свой разделитель
export function runLabelParts(o: {
  action: ContextAction | null; params: readonly LaunchParam[]; price: string | null; mobile: boolean;
  state: RunState; progress: number | null; objectName?: string | null;
}): { name: string; tail: string } {
  if (!o.action) return { name: '', tail: '' };
  if (o.state === 'running') {
    const what = o.action.verb ? `${o.action.verb}${o.objectName ? ` ${o.objectName}` : ''}` : o.action.label;
    return { name: `✦ ${what}…`, tail: o.progress === null ? '' : ` ${Math.round(o.progress * 100)} %` };
  }
  const parts: string[] = [];
  for (const p of o.params) {
    if (p.kind === 'duration') parts.push(`${p.value} с`);
    if (p.kind === 'variants' && p.value > 1) parts.push(o.mobile ? `×${p.value}` : `${p.value} вар.`);
  }
  if (o.price) parts.push(o.price);
  return { name: `✦ ${o.action.label}`, tail: parts.map(x => ` · ${x}`).join('') };
}

// Подпись цельной строкой: `✦ Изменить · 3 вар. · $0.12`
export function runLabel(o: Parameters<typeof runLabelParts>[0]): string {
  const { name, tail } = runLabelParts(o);
  return name + tail;
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
  // Ответ пишем, только если запрос всё ещё текущий: на быстрой печати ответы приходят не по порядку,
  // и старый затёр бы цену нового
  const current = () => _runs.get(sessionId) === e && e.quoting === key;
  try {
    const value = await api.quote(ctx, req);
    if (current()) patch(sessionId, { quote: { key, value }, quoting: null });
  } catch (err) {
    if (current()) {
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
    if (handle.cancel) _cancel.set(d.sessionId, handle.cancel); else _cancel.delete(d.sessionId);
    let off: (() => void) | null = null;
    off = handle.watch(ev => {
      // Остановил сам человек: тихо возвращаем кнопку, ошибки нет
      if (ev.cancelled) { patch(d.sessionId, { state: 'idle', progress: null }); clearRunCarry(d.sessionId); off?.(); return; }
      if (ev.error) {
        patch(d.sessionId, { state: 'error', progress: null }); clearRunCarry(d.sessionId);
        showToast(ev.error, '', 'error'); off?.(); return;
      }
      if (ev.result) { patch(d.sessionId, { state: 'done', progress: 1, result: ev.result }); off?.(); return; }
      if (ev.progress !== undefined) patch(d.sessionId, { progress: ev.progress });
    });
    _unwatch.set(d.sessionId, off);
  } catch (err) {
    // Контекст сменился: DTO уже в сторе, цену пересчитает новый ключ; запуск сам не повторяется
    if (acceptConflict(d.sessionId, err)) { patch(d.sessionId, { state: 'idle', progress: null, quote: null }); throw err; }
    patch(d.sessionId, { state: 'error', progress: null }); clearRunCarry(d.sessionId);
    if (!isReported(err)) showToast((err as Error).message || 'Не удалось запустить', '', 'error');
    throw err;
  }
}

// Состояние запуска после «готово»: следующий выбор начинает с чистого листа
export function resetRun(sessionId: string) {
  _unwatch.get(sessionId)?.();
  _unwatch.delete(sessionId);
  _cancel.delete(sessionId);
  _runs.delete(sessionId);
  emit();
}

export function resetAllRuns() {
  _unwatch.forEach(off => off());
  _unwatch.clear(); _cancel.clear(); _runs.clear(); _variants.clear();
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
  const none = { action: null, stop: null, label: '', quote: null, state: 'idle' as const, progress: null, result: null, text: '', answer: null, blocked: null, labelParts: { name: '', tail: '' }, params: [] as readonly LaunchParam[], scope: '', req: null };
  const { sessionId, api, ctx, primary, refs, revision } = o;
  if (!api || !primary) return { ...none, setText: () => {}, setParam: () => {}, run: async () => {} };
  const actions = api.actions(ctx, { primary, refs });
  const { actionId } = resolveAction(sessionId, objectKey(primary), primary.by, actions, false);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  if (!action) return { ...none, setText: () => {}, setParam: () => {}, run: async () => {} };
  const scope = `${objectKey(primary)}:${action.id}`;
  const e = runEntry(sessionId, scope);
  const values = _variants.has(sessionId) && e.values.variants === undefined ? { ...e.values, variants: _variants.get(sessionId)! } : e.values;
  const params = mergeParams(api.params?.(ctx, action.id) ?? [], values).map(p => (p.kind === 'variants' ? { ...p, value: Math.min(p.max, Math.max(p.min, p.value)) } : p));
  const lp = launchParams(action, params, values);
  const salt = api.priceSalt?.(ctx, action.id);
  const req: QuoteRequest = { op: action.op ?? action.id, text: e.text, params: lp, contextRevision: revision, ...(salt ? { salt } : null) };
  const price = e.quote?.key === quoteKey(req) ? e.quote.value : null;
  const labelIn = { action, params, price: price?.price ?? null, mobile: ctx.isMobile, state: e.state, progress: e.progress, objectName: baseName(primary.label) };
  return {
    action,
    scope,
    req,
    label: runLabel(labelIn),
    labelParts: runLabelParts(labelIn),
    quote: price,
    state: e.state,
    progress: e.progress,
    result: e.result,
    stop: e.state === 'running' && _cancel.has(sessionId) ? () => { void Promise.resolve(_cancel.get(sessionId)?.()); } : null,
    text: e.text,
    answer: questionValue(action, e.values),
    blocked: runBlockReason(action, e.text),
    params,
    setText: t => setRunText(sessionId, scope, t),
    setParam: (name, v) => setRunParam(sessionId, scope, name, v),
    run: text => launchAction({ sessionId, api, ctx, primary, action, scope, params: lp, revision }, text),
  };
}
