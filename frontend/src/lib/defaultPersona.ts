// Персоны по умолчанию (фича default-personas-onboarding): модульный стор ответа
// /api/auth/me (дефолт-персона, признак онбординга) + единый хелпер создания чата
// человеком. Паттерн стора — как lib/personas.ts: модульное состояние, подписки,
// useSyncExternalStore. Инвалидация — по broadcast personas_changed (action='default').

import { useSyncExternalStore } from 'react';
import type { Me, Project, Session } from '../types';
import { api } from './api';
import { getFlag, FLAGS } from './featureFlags';
import { onMessage } from './signalr';

interface MeState {
  loaded: boolean;
  defaultPersonaId: string | null;
  needsOnboarding: boolean;
  onboardingSessionId: string | null;
}

let _me: MeState = { loaded: false, defaultPersonaId: null, needsOnboarding: false, onboardingSessionId: null };
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _listeners.forEach(fn => fn());
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  // Смена дефолта (онбординг, настройки, удаление с преемником) — перечитываем me,
  // чтобы резолвер аватаров и гейты видели свежий дефолт без перезагрузки
  onMessage(msg => {
    if (msg.type === 'personas_changed' && msg.action === 'default') void refreshMe();
  });
}

// Применить свежий ответ /auth/me (зовёт App после логина/проверки токена)
export function setMeFromServer(me: Me): void {
  wireRealtime();
  _me = {
    loaded: true,
    defaultPersonaId: me.defaultPersonaId ?? null,
    needsOnboarding: !!me.needsOnboarding,
    onboardingSessionId: me.onboardingSessionId ?? null,
  };
  emit();
}

// Сброс при разлогине — данные не должны протечь следующему аккаунту
export function clearMe(): void {
  _me = { loaded: false, defaultPersonaId: null, needsOnboarding: false, onboardingSessionId: null };
  emit();
}

export async function refreshMe(): Promise<void> {
  try { setMeFromServer(await api.auth.me()); }
  catch (e) {
    // 401 — протухшая сессия, а не офлайн: глотать нельзя, иначе экран залипнет в
    // прежнем состоянии. Уводим на логин (транспорт уже послал cc-unauthorized;
    // повтор безвреден — обработчик в App идемпотентен).
    if ((e as { status?: number } | null)?.status === 401) {
      if (typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
      return;
    }
    // офлайн/сетевой сбой — оставляем прежнее состояние
  }
}

export function getMeSnapshot(): MeState { return _me; }

export function useMe(): MeState {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _me,
    () => _me,
  );
}

// Дефолт-персона контекста: в проекте — руководитель проекта, вне проекта — личная.
// null — дефолта нет (онбординг соответствующего уровня не пройден).
export function getContextDefaultPersonaId(project?: Pick<Project, 'defaultPersonaId'> | null): string | null {
  if (project !== undefined && project !== null) return project.defaultPersonaId ?? null;
  return _me.defaultPersonaId;
}

// Ошибка-сигнал «нужен онбординг»: хелпер не может создать чат без дефолт-персоны.
// Вызывающий ловит её и ведёт пользователя в соответствующий гейт (сам гейт обычно
// уже на экране — условие needsOnboarding/defaultPersonaId==null держит его App/WorkspacePage).
export class OnboardingRequiredError extends Error {
  readonly scope: 'user' | 'project';
  constructor(scope: 'user' | 'project') {
    super(scope === 'project'
      ? 'Сначала пройдите онбординг проекта — у проекта ещё нет персоны по умолчанию'
      : 'Сначала пройдите онбординг — у вас ещё нет персоны по умолчанию');
    this.scope = scope;
  }
}

// Единый хелпер создания чата человеком (инвариант «чат только с персоной»).
// Под флагом чат создаётся от лица дефолт-персоны контекста; без флага — прежние пути
// (байт-в-байт старое поведение). При отсутствии дефолта под флагом НЕ зовём старый путь
// и не глотаем 400 — бросаем OnboardingRequiredError (гейт онбординга уже держит экран).
export async function createChatWithContextPersona(
  project?: Pick<Project, 'id' | 'defaultPersonaId'> | null,
  opts?: { mode?: string; name?: string },
): Promise<Session> {
  if (getFlag(FLAGS.defaultPersonasOnboarding)) {
    const defaultId = getContextDefaultPersonaId(project);
    if (!defaultId) throw new OnboardingRequiredError(project ? 'project' : 'user');
    return api.personas.createChat(defaultId, {
      mode: opts?.mode ?? 'auto',
      name: opts?.name,
      projectId: project?.id,
    });
  }
  return project
    ? api.sessions.create(project.id, opts?.mode ?? 'auto', undefined, opts?.name)
    : api.chats.create(opts?.mode ?? 'auto', undefined, opts?.name);
}
