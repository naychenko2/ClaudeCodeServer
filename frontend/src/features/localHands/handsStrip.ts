// Полоса «Руки» над композером (реестр composer-strip, ADR-016 §7): стор состояния рук
// чатов и правило фокуса. Компоненты только рисуют и подписываются — логика здесь, под
// юнит-тестом handsStrip.test.ts.
//
// Источник состояния — тот же, что был у бейджа: первая отрисовка GET
// /api/sessions/{id}/hands-status, дальше события hands_status. Питает стор
// LocalHandsStripFeed, смонтированный в чате, пока тот открыт: сама полоса рисуется
// только активной и не может сама себя запросить.

import { useSyncExternalStore } from 'react';
import { getPendingFocus, releaseStrip, requestStrip } from '../../lib/composerStrips';
import {
  HANDS_BADGE_LOADING, HandsChatState, handsEventReceived, handsInitialFailed, handsInitialLoaded,
  type HandsBadgeState,
} from '../../lib/localHands';
import type { ServerMessage } from '../../types';

export const HANDS_STRIP = 'hands';

// Руки проекта глазами полосы: available — тумблер проекта включён и матрица их пускает
// (handsRefusal == null)
export interface HandsProjectInfo { available: boolean; deviceName: string | null }
// Провайдер чата (Session.provider) — для строки «видит снимки окон / видит только текст окон»
export interface HandsSessionInfo { state: HandsBadgeState; provider: string | null }

const _projects = new Map<string, HandsProjectInfo>();
const _sessions = new Map<string, HandsSessionInfo>();
let _version = 0;
const _listeners = new Set<() => void>();

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

export function subscribeHandsStrip(fn: () => void) {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}
export function getHandsStripVersion() { return _version; }

export function setHandsProject(projectId: string, info: HandsProjectInfo) {
  const prev = _projects.get(projectId);
  if (prev && prev.available === info.available && prev.deviceName === info.deviceName) return;
  _projects.set(projectId, info);
  emit();
}
export function getHandsProject(projectId: string): HandsProjectInfo | null {
  return _projects.get(projectId) ?? null;
}

export function getHandsSession(sessionId: string): HandsSessionInfo {
  return _sessions.get(sessionId) ?? { state: HANDS_BADGE_LOADING, provider: null };
}
function patchSession(sessionId: string, patch: Partial<HandsSessionInfo>) {
  _sessions.set(sessionId, { ...getHandsSession(sessionId), ...patch });
  emit();
}
export function setHandsSessionProvider(sessionId: string, provider: string | null) {
  if (getHandsSession(sessionId).provider === provider && _sessions.has(sessionId)) return;
  patchSession(sessionId, { provider });
}

// ---------- фокус ----------

// Руки взяли ход → полоса просит фокус; любое другое состояние — отпускает. Человек
// сам ушёл с полосы — запрос в силе, но повторный active его не освежает: иначе каждое
// событие хода навязывало бы полосу заново вопреки ручному выбору (правило старшинства
// из docs/design/composer-strips-and-modes.md). Новый ход после отпуска просит заново.
export function applyHandsFocus(sessionId: string, state: string | null) {
  if (state === HandsChatState.Active) {
    if (getPendingFocus(sessionId) !== HANDS_STRIP) requestStrip(sessionId, HANDS_STRIP);
  } else {
    releaseStrip(sessionId, HANDS_STRIP);
  }
}

// ---------- события ----------

export function handsStatusLoaded(
  sessionId: string,
  view: { state: string | null; reason?: string | null; deviceName?: string | null },
) {
  const prev = getHandsSession(sessionId).state;
  const next = handsInitialLoaded(prev, view);
  patchSession(sessionId, { state: next });
  // Событие успело раньше ответа — фокус уже выставило оно
  if (prev.kind !== 'status') applyHandsFocus(sessionId, next.kind === 'status' ? next.status.state : null);
}

export function handsStatusFailed(sessionId: string) {
  patchSession(sessionId, { state: handsInitialFailed(getHandsSession(sessionId).state) });
}

// Конец хода: руки ход больше не держат, даже если отчёт устройства потерялся
const TURN_END_STATUSES = new Set(['finished', 'error', 'orphaned']);
function isTurnEnd(msg: ServerMessage): boolean {
  return msg.type === 'result' || (msg.type === 'status_changed' && TURN_END_STATUSES.has(msg.status));
}

export function handsStripOnMessage(sessionId: string, msg: ServerMessage) {
  if (msg.sessionId !== sessionId) return;
  if (msg.type === 'hands_status') {
    patchSession(sessionId, { state: handsEventReceived({ state: msg.state, deviceName: msg.deviceName, reason: msg.reason }) });
    applyHandsFocus(sessionId, msg.state);
  } else if (isTurnEnd(msg)) {
    releaseStrip(sessionId, HANDS_STRIP);
  }
}

// Первый вход в чат: состояние грузится заново, прежнее не показываем
export function resetHandsSession(sessionId: string) {
  patchSession(sessionId, { state: HANDS_BADGE_LOADING });
}

// ---------- вклад в реестр ----------

// Полоса предлагается, только пока руки проекта включены и доступны, а сервер не ответил
// «у чата рук нет»
export function handsStripAvailable({ projectId, sessionId }: { projectId: string; sessionId: string | null }): boolean {
  if (!sessionId) return false;
  if (getHandsProject(projectId)?.available !== true) return false;
  return getHandsSession(sessionId).state.kind !== 'none';
}

export function useHandsStripVersion() {
  return useSyncExternalStore(subscribeHandsStrip, getHandsStripVersion, getHandsStripVersion);
}

// Сброс состояния — только для тестов
export function __resetHandsStrip() {
  _projects.clear();
  _sessions.clear();
  emit();
}
