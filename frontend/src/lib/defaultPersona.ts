// Персоны по умолчанию: модульный стор ответа /api/auth/me (дефолт-персона,
// признак онбординга) + единый хелпер создания чата человеком. Паттерн стора — как lib/personas.ts: модульное состояние, подписки,
// useSyncExternalStore. Инвалидация — по broadcast personas_changed (action='default').

import { useSyncExternalStore } from 'react';
import type { Me, Project, Session } from '../types';
import { api } from './api';
import { OfflineError, RequestTimeoutError } from './offline';
import { onMessage } from './signalr';

interface MeState {
  loaded: boolean;
  // id текущего пользователя (== Project.ownerId бэка) — нужен гейтам «только владелец»
  // (например, смена фона проекта), без прокидывания auth пропсом через всё дерево
  userId: string | null;
  defaultPersonaId: string | null;
  // Роль пользователя (копия Me.role) — нужна гейтам раздела «Персоны» (например,
  // видимость вкладки «Специальности» и admin-only слоёв), без отдельного auth/me-фетча
  role: string;
  needsOnboarding: boolean;
  onboardingSessionId: string | null;
  // Среда исполнения процессов пользователя: 'local' — на хосте, 'container' — в
  // общей Docker-песочнице cc-sandbox. Карточка каталога MCP-серверов рисует по
  // нему бейдж среды и предупреждение у stdio-записей у local-владельцев. null —
  // /me ещё не ответил; устаревшее значение не показываем, чтобы сигнал был свежим
  executionEnvironment: 'local' | 'container' | null;
}

let _me: MeState = {
  loaded: false, userId: null, defaultPersonaId: null, role: '',
  needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null,
};
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _listeners.forEach(fn => fn());
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  // Смена дефолта (онбординг, настройки, удаление с преемником) — перечитываем me,
  // чтобы резолвер аватаров и гейты видели свежий дефолт без перезагрузки.
  // 'updated' тоже: ручная правка заготовки-ассистента снимает её статус на бэкенде
  // (needsOnboarding → false), и без перечитывания точка на плавающей кнопке висела бы
  // до полной перезагрузки страницы — QA поймал это сценарием M10б. Правка персоны
  // руками случается редко, лишнее чтение /me здесь дешевле рассинхрона.
  onMessage(msg => {
    if (msg.type === 'personas_changed' && (msg.action === 'default' || msg.action === 'updated'))
      void refreshMe();
  });
}

// Применить свежий ответ /auth/me (зовёт App после логина/проверки токена)
export function setMeFromServer(me: Me): void {
  wireRealtime();
  _me = {
    loaded: true,
    userId: me.userId ?? null,
    defaultPersonaId: me.defaultPersonaId ?? null,
    role: me.role ?? '',
    needsOnboarding: !!me.needsOnboarding,
    onboardingSessionId: me.onboardingSessionId ?? null,
    executionEnvironment: me.executionEnvironment ?? null,
  };
  emit();
}

// Сброс при разлогине — данные не должны протечь следующему аккаунту
export function clearMe(): void {
  _me = {
    loaded: false, userId: null, defaultPersonaId: null, role: '',
    needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null,
  };
  emit();
}

// Явный таймаут /auth/me: дефолтного транспортного (30с) для освежения дефолт-персоны
// многовато — лёгкий запрос должен падать быстро, чтобы фолбэк к прежнему состоянию
// (и, при сетевом сбое, одна повторная попытка) не висел полминуты.
const ME_TIMEOUT_MS = 10_000;
const ME_RETRY_DELAY_MS = 1500;

function isAuthError(e: unknown): boolean {
  return (e as { status?: number } | null)?.status === 401;
}
// Сетевой сбой: сервер не ответил (OfflineError) либо упёрся в наш таймаут
// (RequestTimeoutError). Только такие претендуют на повтор — 401 и HTTP-ошибки нет.
function isNetworkFailure(e: unknown): boolean {
  return e instanceof OfflineError || e instanceof RequestTimeoutError;
}
function dispatchUnauthorized(): void {
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
}

export async function refreshMe(): Promise<void> {
  try { setMeFromServer(await api.auth.me({ timeoutMs: ME_TIMEOUT_MS })); }
  catch (e) {
    // 401 — протухшая сессия, а не офлайн: глотать нельзя, иначе экран залипнет в
    // прежнем состоянии. Уводим на логин (транспорт уже послал cc-unauthorized;
    // повтор безвреден — обработчик в App идемпотентен).
    if (isAuthError(e)) { dispatchUnauthorized(); return; }
    // Сетевой сбой — одна повторная попытка после короткой паузы (сервер мог моргнуть);
    // прежнее состояние остаётся, если и она не вытянула.
    if (isNetworkFailure(e)) {
      await new Promise<void>(r => setTimeout(r, ME_RETRY_DELAY_MS));
      try { setMeFromServer(await api.auth.me({ timeoutMs: ME_TIMEOUT_MS })); }
      catch (e2) {
        if (isAuthError(e2)) dispatchUnauthorized();
        // повтор тоже упал по сети — оставляем прежнее состояние
      }
      return;
    }
    // прочая HTTP-ошибка (5xx и т.п.) — прежнее состояние остаётся
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

// Дефолт-персона контекста: в проекте — его руководитель, а если руководителя ещё нет —
// личная дефолт-персона (проект без командных механик ведёт личный ассистент); вне проекта —
// сразу личная. null — дефолта нет вовсе (осиротевший юзер без ассистента; чинит серверный
// провижн на рубеже 2.4).
export function getContextDefaultPersonaId(project?: Pick<Project, 'defaultPersonaId'> | null): string | null {
  return project?.defaultPersonaId ?? _me.defaultPersonaId;
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

// Единый хелпер создания чата человеком (инвариант «чат только с персоной»):
// чат создаётся от лица дефолт-персоны контекста. При отсутствии дефолта гейта на
// фронте нет (план §4, п.4.3, парная правка с серверным рубежом 2.4): идём на тот же
// api.sessions.create()/api.chats.create() без personaId — сервер сам провижнит
// ассистента и продолжит создание с ним (400 остаётся только когда провижн вернул
// null). OnboardingRequiredError — только на случай, когда и сервер не смог.
export async function createChatWithContextPersona(
  project?: Pick<Project, 'id' | 'defaultPersonaId'> | null,
  opts?: { mode?: string; name?: string },
): Promise<Session> {
  const defaultId = getContextDefaultPersonaId(project);
  if (defaultId) {
    return api.personas.createChat(defaultId, {
      mode: opts?.mode ?? 'auto',
      name: opts?.name,
      projectId: project?.id,
    });
  }
  try {
    return project
      ? await api.sessions.create(project.id, opts?.mode ?? 'auto', undefined, opts?.name)
      : await api.chats.create(opts?.mode ?? 'auto', undefined, opts?.name);
  } catch (e) {
    if ((e as Error & { status?: number }).status === 400) {
      throw new OnboardingRequiredError(project ? 'project' : 'user');
    }
    throw e;
  }
}
