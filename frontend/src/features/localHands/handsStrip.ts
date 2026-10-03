// Руки в чате (ADR-016 §7): стор состояния рук чатов для пилюли «Руки» в губе поля ввода. Компоненты
// только рисуют и подписываются — логика здесь, под юнит-тестом handsStrip.test.ts.
//
// Источник состояния — первая отрисовка GET /api/sessions/{id}/hands-status, дальше события
// hands_status. Питает стор LocalHandsStripFeed, смонтированный в чате, пока тот открыт.

import { useSyncExternalStore } from 'react';
import {
  HANDS_BADGE_LOADING, handsEventReceived, handsInitialFailed, handsInitialLoaded,
  type HandsBadgeState,
} from '../../lib/localHands';
import { isFeatureAvailable } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project, type ServerMessage } from '../../types';

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
// Руки проекта из его DTO: тумблер проекта и отказ матрицы. Флага и списка провайдеров
// больше нет, сверх DTO полоса ничего не ждёт
export function handsProjectInfo(project: Project): HandsProjectInfo {
  return {
    available: project.handsEnabled === true && isFeatureAvailable(project, ProjectFeature.Hands),
    deviceName: project.device?.name ?? null,
  };
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

// ---------- события ----------

export function handsStatusLoaded(
  sessionId: string,
  view: { state: string | null; reason?: string | null; deviceName?: string | null },
) {
  patchSession(sessionId, { state: handsInitialLoaded(getHandsSession(sessionId).state, view) });
}

export function handsStatusFailed(sessionId: string) {
  patchSession(sessionId, { state: handsInitialFailed(getHandsSession(sessionId).state) });
}

export function handsStripOnMessage(sessionId: string, msg: ServerMessage) {
  if (msg.sessionId !== sessionId) return;
  if (msg.type === 'hands_status') {
    patchSession(sessionId, { state: handsEventReceived({ state: msg.state, deviceName: msg.deviceName, reason: msg.reason }) });
  }
}

// Первый вход в чат: состояние грузится заново, прежнее не показываем
export function resetHandsSession(sessionId: string) {
  patchSession(sessionId, { state: HANDS_BADGE_LOADING });
}

// ---------- вклад в реестр ----------

// Полоса предлагается, только пока руки проекта включены и доступны, а сервер не ответил
// «у чата рук нет». Рук у личного чата вне проекта нет: они живут на устройстве проекта
export function handsStripAvailable({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }): boolean {
  if (!projectId || !sessionId) return false;
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
