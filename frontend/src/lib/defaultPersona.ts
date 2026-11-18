// Персоны по умолчанию: модульный стор ответа /api/auth/me (дефолт-персона,
// признак онбординга) + единый хелпер создания чата человеком. Паттерн стора — как lib/personas.ts: модульное состояние, подписки,
// useSyncExternalStore. Инвалидация — по broadcast personas_changed (action='default').

import { useSyncExternalStore } from 'react';
import type { Me, Project, Session } from '../types';
import { api } from './api';
import { OfflineError, RequestTimeoutError, readStoredUserId } from './offline';
import { onMessage } from './signalr';
import { isSharedProject } from '../features/projects/members/messageAuthor';

interface MeState {
  loaded: boolean;
  // id текущего пользователя (== Project.ownerId бэка) — нужен гейтам «только владелец»
  // (например, смена фона проекта), без прокидывания auth пропсом через всё дерево
  userId: string | null;
  // логин текущего пользователя — сравнение с owner записей истории («вы»)
  username: string | null;
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
  // Детский режим (Me.isChild): приложение рисует только детскую оболочку
  isChild: boolean;
}

let _me: MeState = {
  loaded: false, userId: null, username: null, defaultPersonaId: null, role: '',
  needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null, isChild: false,
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
    username: me.username ?? null,
    defaultPersonaId: me.defaultPersonaId ?? null,
    role: me.role ?? '',
    needsOnboarding: !!me.needsOnboarding,
    onboardingSessionId: me.onboardingSessionId ?? null,
    executionEnvironment: me.executionEnvironment ?? null,
    isChild: !!me.isChild,
  };
  emit();
}

// Сброс при разлогине — данные не должны протечь следующему аккаунту
export function clearMe(): void {
  _me = {
    loaded: false, userId: null, username: null, defaultPersonaId: null, role: '',
    needsOnboarding: false, onboardingSessionId: null, executionEnvironment: null, isChild: false,
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

// «Одна вкладка — один владелец»: хранилище (readStoredUserId) называет того же
// человека, чьи данные показывает вкладка (снимок /auth/me). Сравнивать нечем, пока
// одно из значений неизвестно (/me ещё не пришёл, id не записан) — это не расхождение.
export function isTabOwnerConsistent(): boolean {
  const stored = readStoredUserId();
  const mine = _me.userId;
  if (!stored || !mine) return true;
  return stored === mine;
}

export function useMe(): MeState {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _me,
    () => _me,
  );
}

// Контекст проекта для резолвера дефолт-персоны и хелпера создания чата (W3-F2a, доработка 2):
// ownerId — ОБЯЗАТЕЛЬНОЕ поле (значение null — проекта без владельца, старые данные).
// Литерал без него не компилируется: tsc ловит любой будущий вызов, забывший его передать —
// та была причина дефекта в WorkspacePage, где контекст собирают литералом.
// Вызывающий с полным Project передаёт его как есть: хелпер сам нормализует Project в
// контекст (у Project поле ownerId остаётся необязательным — старые данные).
// myRole (W4-FB1) — признак общего проекта (isSharedProject): в общем проекте личный фолбэк не
// работает. Поле необязательное: в личном проекте и у старого бэка его нет — отсутствие
// читается как «не общий», поведение прежнее.
export type ProjectChatContext = Pick<Project, 'id' | 'defaultPersonaId' | 'myRole'> & { ownerId: string | null };

// Дефолт-персона контекста: в проекте — его руководитель; вне проекта — сразу личная.
// null — дефолта нет вовсе (осиротевший юзер без ассистента; чинит серверный
// провижн на рубеже 2.4).
// Исключение (W3-F2a, дефект D1; расширено W4-FB1, дефект 8041ed7c; закрыто и для владельца
// доработкой W4-B1a 2 по финалу ed64271c): в общем проекте (isSharedProject — по myRole)
// личная персона НЕ подставляется никому, включая владельца: личная (глобальная/сферная)
// персона собеседником общего чата быть не может — бэк отказывает 400
// global_persona_in_shared (SessionCoreContracts.cs:80). Без руководителя запрос идёт без
// personaId и бэк отказывает 400 no_project_persona тоже всем, включая владельца: собеседника
// в осиротевшем общем проекте нет (провижн — право личного проекта), текст отказа доходит
// до вызывающего как тост.
// Старый признак участия (бэк без myRole): не-владелец по ownerId — как раньше (W3-F2a).
// Личный проект (myRole нет) и проект без ownerId (null — старые данные) — личный фолбэк.
export function getContextDefaultPersonaId(
  project?: Pick<ProjectChatContext, 'defaultPersonaId' | 'ownerId' | 'myRole'> | null,
): string | null {
  if (project?.defaultPersonaId) return project.defaultPersonaId;
  // Общий проект — личная персона не кандидат, даже владельцу (W4-FB1).
  if (isSharedProject(project)) return null;
  if (project && project.ownerId && _me.userId && project.ownerId !== _me.userId) return null;
  return _me.defaultPersonaId;
}

// Ошибка-сигнал «нужен онбординг»: хелпер не может создать чат без дефолт-персоны.
// Вызывающий ловит её и ведёт пользователя в соответствующий гейт (сам гейт обычно
// уже на экране — условие needsOnboarding/defaultPersonaId==null держит его App/WorkspacePage).
export class OnboardingRequiredError extends Error {
  readonly scope: 'user' | 'project';
  constructor(scope: 'user' | 'project') {
    super(scope !== 'user'
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
// Исключение (W3-B4b/W3-F2a): 400 с кодом no_project_persona — в общем проекте нет
// руководителя и провижна участника нет; это не онбординг (участник сам не починит —
// назначает только владелец), поэтому текст сервера уходит вызывающему как есть,
// а не оборачивается в OnboardingRequiredError.
// opts.private (W4-FB1): приватный чат общего проекта (контракт W4-B1a/W4-B1b). Флаг
// доходит до тела запроса в обеих проектных ветках; чат вне проекта его не получает —
// там приватности нет. Без флага тела запросов прежние.
export async function createChatWithContextPersona(
  project?: ProjectChatContext | Project | null,
  opts?: { mode?: string; name?: string; private?: boolean },
): Promise<Session> {
  // Полный Project нормализуем в контекст: его ownerId всё ещё необязателен (старые
  // данные) — отсутствующий становится null, резолвер читает это как «проект без владельца».
  // myRole прокидывается как есть: где он есть — проект общий, личный фолбэк не работает (W4-FB1).
  const ctx = project
    ? { defaultPersonaId: project.defaultPersonaId ?? null, ownerId: project.ownerId ?? null, myRole: project.myRole }
    : null;
  const defaultId = getContextDefaultPersonaId(ctx);
  if (defaultId) {
    return api.personas.createChat(defaultId, {
      mode: opts?.mode,
      name: opts?.name,
      projectId: project?.id,
      ...(opts?.private ? { private: true } : {}),
    });
  }
  try {
    return project
      ? await api.sessions.create(project.id, opts?.mode, undefined, opts?.name, undefined, undefined, undefined, opts?.private)
      : await api.chats.create(opts?.mode, undefined, opts?.name);
  } catch (e) {
    const err = e as Error & { status?: number; body?: { code?: string; error?: string } };
    if (err.status === 400) {
      // Различаем по коду из тела ответа (как chatFieldsErrorText, api.ts:1071), не по тексту.
      if (err.body?.code === 'no_project_persona') {
        // Текст — серверный (единственная формулировка отказа); если тело пришло без
        // error, оставляем message транспорта (он и есть текст тела или statusText).
        throw new Error(err.body.error ?? err.message);
      }
      throw new OnboardingRequiredError(project ? 'project' : 'user');
    }
    throw e;
  }
}
