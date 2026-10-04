// Стор контекста чата на фронте: зеркало серверного (ADR-023 §2.2). Кэш Map<sessionId, DTO>
// поверх useSyncExternalStore; источник — GET, событие chat_context_changed и перечитка на
// onReconnected (образец — threadStore нитей). Мутации идут от текущей ревизии; 409 —
// подставляем DTO из тела и говорим об этом.

import { useEffect, useSyncExternalStore } from 'react';
import { createReleaseUndo, type ReleaseUndoController } from '../../components/generation/useReleaseUndo';
import { onReconnected } from '../signalr';
import { showToast } from '../toast';
import { chatContextApi, conflictContext } from './api';
import { EMPTY_CONTEXT, type ChatContextDto, type ContextRefInput } from './types';

interface Entry { state: ChatContextDto; loaded: boolean; loading: boolean }

const _entries = new Map<string, Entry>();
const _undo = new Map<string, ReleaseUndoController<ContextRefInput>>();
let _version = 0;
const _listeners = new Set<() => void>();
let _unsub: (() => void) | null = null;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void) {
  _listeners.add(fn);
  ensureLive();
  return () => { _listeners.delete(fn); };
}
const getVersion = () => _version;

// «Вернуть» после снятия основного объекта: свой контроллер на чат (плашка живёт 4 с)
function undoOf(sessionId: string): ReleaseUndoController<ContextRefInput> {
  let c = _undo.get(sessionId);
  if (!c) { c = createReleaseUndo<ContextRefInput>(); _undo.set(sessionId, c); }
  return c;
}

function apply(sessionId: string, state: ChatContextDto) {
  const e = _entries.get(sessionId);
  // Событие старше того, что уже знаем, — пропускаем
  if (e?.loaded && e.state.revision > state.revision) return;
  // Основной объект пропал не по нашей кнопке (агент, другое окно) — плашка «Вернуть» неактуальна
  if (e?.loaded && e.state.primary && !state.primary) _undo.get(sessionId)?.dismiss();
  _entries.set(sessionId, { state, loaded: true, loading: false });
  emit();
}

function ensureLive() {
  if (_unsub) return;
  const offEvents = chatContextApi.subscribe(ev => {
    // Чат, который не показываем, не кэшируем: прочитаем, когда понадобится
    if (!_entries.has(ev.sessionId)) return;
    // Рассылка на бэкенде идёт без ожидания: события приходят не по порядку, берём только более новую ревизию
    const known = _entries.get(ev.sessionId)!;
    if (known.loaded && ev.context.revision <= known.state.revision) return;
    apply(ev.sessionId, ev.context);
  });
  // После обрыва события могли потеряться — перечитываем всё, что показано
  const offRe = onReconnected(() => {
    _entries.forEach((e, sid) => { if (e.loaded) void load(sid, true); });
  });
  _unsub = () => { offEvents(); offRe(); };
}

async function load(sessionId: string, force = false) {
  const e = _entries.get(sessionId);
  if (e && (e.loading || (e.loaded && !force))) return;
  _entries.set(sessionId, { state: e?.state ?? EMPTY_CONTEXT, loaded: e?.loaded ?? false, loading: true });
  try {
    apply(sessionId, await chatContextApi.get(sessionId));
  } catch {
    // Модуль выключен или чат не свой — контекста просто нет
    _entries.set(sessionId, { state: e?.state ?? EMPTY_CONTEXT, loaded: true, loading: false });
    emit();
  }
}

export function getChatContextState(sessionId: string | null): ChatContextDto {
  return (sessionId && _entries.get(sessionId)?.state) || EMPTY_CONTEXT;
}

export function useChatContext(sessionId: string | null): ChatContextDto {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (sessionId) void load(sessionId); }, [sessionId]);
  return getChatContextState(sessionId);
}

export type MutateResult = 'ok' | 'conflict' | 'failed';

// Мутация от текущей ревизии. 409 — применяем DTO из тела и говорим об этом
async function mutate(sessionId: string, run: (revision: number) => Promise<ChatContextDto>): Promise<MutateResult> {
  try {
    apply(sessionId, await run(getChatContextState(sessionId).revision));
    return 'ok';
  } catch (e) {
    const fresh = conflictContext(e);
    if (fresh) {
      apply(sessionId, fresh);
      showToast('Контекст только что поменяли в другом месте — показываем свежее состояние', '', 'info');
      return 'conflict';
    }
    showToast(failureText(e), '', 'error');
    return 'failed';
  }
}

// Текст отказа: человеческое `message` ответа сервера, код (`error`) — только если сообщения нет
export function failureText(e: unknown): string {
  const msg = (e as { body?: { message?: unknown } } | null)?.body?.message;
  if (typeof msg === 'string' && msg.trim()) return msg;
  return (e as Error)?.message || 'Не удалось изменить контекст';
}

// 409 context_changed у запуска вида: подставляем свежий DTO из тела (перечитывать не нужно) и говорим об
// этом. false — ошибка не про контекст
export function acceptConflict(sessionId: string, e: unknown, quiet = false): boolean {
  const fresh = conflictContext(e);
  if (!fresh) return false;
  apply(sessionId, fresh);
  if (!quiet) showToast('Контекст только что поменяли — цена пересчитана, запустите ещё раз', '', 'info');
  return true;
}

export const setPrimary = (sessionId: string, input: Pick<ContextRefInput, 'kind' | 'ref'>) =>
  mutate(sessionId, rev => chatContextApi.setPrimary(sessionId, input, rev));

export const attachRef = (sessionId: string, input: ContextRefInput) =>
  mutate(sessionId, rev => chatContextApi.attachRef(sessionId, input, rev));

export const detachRef = (sessionId: string, itemId: string) =>
  mutate(sessionId, rev => chatContextApi.detachRef(sessionId, itemId, rev));

export const clearContext = (sessionId: string) =>
  mutate(sessionId, rev => chatContextApi.clear(sessionId, rev));

// Снять основной объект. byHuman — ✕ на чипе: над строкой встаёт «Вернуть» на 4 с. Снятие
// агентом или автоматикой плашку не показывает
export async function releasePrimary(sessionId: string, byHuman = true): Promise<MutateResult> {
  const prev = getChatContextState(sessionId).primary;
  if (!prev) return 'ok';
  const result = await mutate(sessionId, rev => chatContextApi.setPrimary(sessionId, null, rev));
  if (result === 'ok') {
    undoOf(sessionId).release({ snapshot: { kind: prev.kind, ref: prev.ref }, text: prev.label }, byHuman);
  }
  return result;
}

// «Вернуть»: прежний основной объект встаёт обратно
export async function undoReleasePrimary(sessionId: string): Promise<MutateResult | null> {
  const snap = undoOf(sessionId).undo();
  return snap ? setPrimary(sessionId, snap) : null;
}

// Плашка «Вернуть» чата: null — показывать нечего
export function useReleaseOffer(sessionId: string | null) {
  const ctl = sessionId ? undoOf(sessionId) : null;
  return useSyncExternalStore(
    ctl ? ctl.subscribe : noSubscribe,
    () => ctl?.current() ?? null,
    () => ctl?.current() ?? null,
  );
}
const noSubscribe = () => () => {};

// Удаление чата: кэш и плашка этого чата не нужны
export function forgetChatContext(sessionId: string) {
  _entries.delete(sessionId);
  _undo.get(sessionId)?.dispose();
  _undo.delete(sessionId);
}

// Выход из аккаунта: кэш прошлого владельца вкладка не держит
export function resetChatContext() {
  _undo.forEach(c => c.dispose());
  _undo.clear();
  _entries.clear();
  emit();
}

// Для тестов: подставить состояние чата, как будто оно пришло с сервера
export function __applyChatContext(sessionId: string, state: ChatContextDto) {
  apply(sessionId, state);
}

// Для тестов: чистый стор и заново подключаемые подписки
export function __resetChatContextStore() {
  resetChatContext();
  _unsub?.();
  _unsub = null;
}

// Для тестов: подписка стора без рендера хука (в node её ставит первый useSyncExternalStore)
export function __subscribeForTests() { return subscribe(() => {}); }

// Для тестов: текущая плашка «Вернуть» чата
export const __offerForTests = (sessionId: string) => _undo.get(sessionId)?.current() ?? null;

export const ensureChatContext = (sessionId: string) => load(sessionId);

// Перечитать контекст чата: вертикаль завела объект (черновик), а событие рассылки могло опоздать или потеряться
export const refreshChatContext = (sessionId: string) => load(sessionId, true);
