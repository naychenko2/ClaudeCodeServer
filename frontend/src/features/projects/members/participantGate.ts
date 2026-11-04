// Режим участника общего проекта: чистая логика «что можно в композере» и «чей это проект».
// Без React — проверяется юнит-тестами (S1-F6). Право хода решает бэк (RunTurn у владельца
// и редактора, S2a-10a; ответы на разрешения/планы — только Configure владельца); фронт лишь
// не обещает того, что бэк откажет.

import type { Project, Session, Task } from '../../../types';

// Участник — не владелец общего проекта. myRole авторитетна, когда она есть
export function isParticipant(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole === 'editor' || project?.myRole === 'reader';
}

export type ParticipantComposerGate =
  | { blocked: false }
  | { blocked: true; role: 'reader'; ownerName: string };

// Имя владельца для текстов: displayName, запасной username; без обоих — «владелец»
export function ownerNameOf(project: Pick<Project, 'ownerDisplayName' | 'ownerUsername'> | null | undefined): string {
  return project?.ownerDisplayName?.trim() || project?.ownerUsername?.trim() || 'владелец';
}

// Можно ли участнику запускать ход из композера: редактор может (право RunTurn, S2a-10a),
// читатель — нет
export function participantComposerGate(project: Project | null | undefined): ParticipantComposerGate {
  if (project?.myRole !== 'reader') return { blocked: false };
  return { blocked: true, role: 'reader', ownerName: ownerNameOf(project) };
}

// Текст плашки над полем: читателю — про смену роли
export function participantComposerText(gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return `Вы читатель — писать в чаты нельзя. Попросите ${gate.ownerName} сделать вас редактором.`;
}

// Подсказка кнопки отправки
export function participantSendTitle(_gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return 'Не отправится: вы читатель проекта';
}

// Действия чата (QA-P1, бэк f777c8460; QA-P1F): переименовать, архивировать и теги —
// право записи чата, владелец и редактор (любой видимый чат, зеркало DenyContentWrite);
// удалить — автор чата и владелец проекта (зеркало ChatAuthorGate/ChatWriteRightsEndpointTests);
// читателю — ничего.
// Обычный проект (без myRole) — всё разрешено, как раньше. null — можно; строка — причина
// для подсказки выключенного пункта и сублейбла меню.
export const CHAT_WRITE_DENIED = 'Только владелец и редактор проекта';
export const CHAT_DELETE_DENIED = 'Только автор чата и владелец проекта';

// Переименовать/архивировать/AI-переименовать/теги: отказан только читателю
export function chatWriteDeniedReason(project: Pick<Project, 'myRole'> | null | undefined): string | null {
  return project?.myRole === 'reader' ? CHAT_WRITE_DENIED : null;
}

// Удалить: автор чата и владелец проекта. Свой чат редактора опознаётся по createdByUserId
// (поле с бэка; у старых записей null — чат чужой, fail-closed); мой id — из stored-пары
export function chatDeleteDeniedReason(
  project: Pick<Project, 'myRole'> | null | undefined,
  chat: Pick<Session, 'createdByUserId'> | null | undefined,
  myUserId: string | null | undefined,
): string | null {
  const role = project?.myRole;
  if (!role || role === 'owner') return null;
  if (role === 'reader') return CHAT_DELETE_DENIED;
  return chat?.createdByUserId && chat.createdByUserId === myUserId ? null : CHAT_DELETE_DENIED;
}

// Настройки чата — право владельца (QA-P1F, зеркало бэкового OwnerSettingsGate): срок
// жизни, мьют уведомлений, «не сохранять решения» — бэк отвечает 403 owner_settings и
// редактору, и читателю. «Теги» — правка содержимого чата: в PUT сессии редактору
// открыты name/tags, значит теги ему можно; читателю бэк отказывает весь Update — тот
// же отказ, что у переименования. Обычный проект (без myRole) и чат вне проекта — без
// изменений, как раньше.
export const OWNER_SETTINGS_DENIED = 'Только владелец проекта';

export function ownerSettingsDeniedReason(project: Pick<Project, 'myRole'> | null | undefined): string | null {
  return isParticipant(project) ? OWNER_SETTINGS_DENIED : null;
}

// Единый диспетчер для петель шапки (ряд, «⋯», runAction): rename/archive/tags — гейт
// записи, delete — авторский, срок/мьют/досье — настройки владельца. Пункты, которые
// ничего не пишут в чате («Закрепить», «На стену»), правило не трогает
export function chatActionDeniedReason(
  key: string,
  project: Pick<Project, 'myRole'> | null | undefined,
  chat: Pick<Session, 'createdByUserId'> | null | undefined,
  myUserId: string | null | undefined,
): string | null {
  if (key === 'rename' || key === 'archive' || key === 'tags') return chatWriteDeniedReason(project);
  if (key === 'delete') return chatDeleteDeniedReason(project, chat, myUserId);
  if (key === 'expiry' || key === 'notify' || key === 'dossier') return ownerSettingsDeniedReason(project);
  return null;
}

// Сообщение об отзыве доступа на списке проектов (QA-1): базовый текст фиксирован; строка
// с именем владельца добавляется только когда сервер его принёс (у проекта без владельца
// ownerDisplayName — null). Формулировка та же, что у плашки в воркспейсе.
export const ACCESS_REVOKED_TITLE = 'Доступ к проекту отозван';
export function accessRevokedOwnerLine(ownerDisplayName?: string | null): string | null {
  const name = ownerDisplayName?.trim();
  return name ? `${name} закрыл вам доступ к проекту.` : null;
}

// Серверные коды отказа (S2a-10b) и их человеческие тексты на клиенте. Код приходит
// из трёх мест: REST — поле body.code (lib/offline прикрепляет тело ошибки целиком к
// err.body); хаб — префикс сообщения «not_chat_author: <текст>» (S2a-10c: у Interrupt и
// AnswerQuestion тела нет, только текст HubException); и та же строка, обёрнутая клиентом
// SignalR: «An unexpected error occurred invoking '<метод>' on the server. HubException:
// <код>: <текст>» — @microsoft/signalr собирает Error из error-строки хаба, поэтому код
// там НЕ префикс (R-S2a-10c). Тексты равны серверным (SessionHub/OwnerSettingsGate):
// одному коду — одна строка. Незнакомый код и отказ без кода — null: вызывающий держит
// прежний свой текст.
const DENIAL_TEXTS = {
  not_chat_author: 'Только автор хода или владелец проекта',
  owner_settings: 'Настройки чата выбирает владелец',
  not_sender: 'Это сообщение отправил другой участник',
} as const;

export type DenialCode = keyof typeof DENIAL_TEXTS;

export function denialCodeOf(e: unknown): DenialCode | null {
  const rest = e as { status?: number; body?: { code?: string } } | null;
  if (rest?.status === 403 && rest.body?.code && rest.body.code in DENIAL_TEXTS) return rest.body.code as DenialCode;
  const msg = e instanceof Error ? e.message : typeof e === 'string' ? e : '';
  // Две формы хба-текста: голый префикс «<код>: …» (S2a-10c) и обёртка клиента SignalR
  // «… invoking '<метод>' on the server. HubException: <код>: …» — в ней код не префикс,
  // маркер один и тот же: «HubException: <код>:». Обе несёт один код, ищем обе.
  for (const code of Object.keys(DENIAL_TEXTS) as DenialCode[]) {
    if (msg.startsWith(`${code}:`) || msg.includes(`HubException: ${code}:`)) return code;
  }
  return null;
}

// Текст отказа для человека; null — кода нет (вызывающий держит свой прежний текст)
export function denialTextOf(e: unknown): string | null {
  const code = denialCodeOf(e);
  return code ? DENIAL_TEXTS[code] : null;
}

// Бэк мог всё же отказать (устаревшая роль на клиенте): 403 с code not_chat_author.
// Разбор — общий с остальными кодами; смысл для вызывающих не изменился
export function isNotChatAuthorError(e: unknown): boolean {
  return denialCodeOf(e) === 'not_chat_author';
}

// Создавать чаты в проекте читателю нельзя: входы создания скрыты
export function canCreateChats(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole !== 'reader';
}

// Задачи общего проекта (разрез CONSULT-TASK-RIGHTS, F2-Q3): правят владелец и редактор,
// читатель только смотрит. Фронт не обещает того, чего он не делает: входы создания
// скрыты, контролы записи выключены с этой подсказкой, перетаскивание на доске не
// активируется вовсе. Вне проекта (хаб) и в личном проекте (myRole нет) — как раньше.
export const TASK_WRITE_DENIED = 'Менять задачи может редактор';

export function canWriteTasks(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole !== 'reader';
}

// null — можно; строка — текст для подсказки выключенного контрола (тот же текст, что
// в панели «Задачи»: одна строка на весь фронт задач)
export function taskWriteDeniedReason(project: Pick<Project, 'myRole'> | null | undefined): string | null {
  return canWriteTasks(project) ? null : TASK_WRITE_DENIED;
}

// Удаление задачи (доработка F2-Q3, ревью 11716b9b): зеркало бэкового TaskDeleteDenied
// (TasksController.cs:243-248) — разрешено автору или владельцу задачи. Порядок тот же:
// сначала роль — читателю отказ до авторства (бэковый 403 project_scope); затем авторство —
// редактору на чужой задаче (бэковый 403 not_task_author, фронт не обещает корзину).
// Автор неизвестен (CreatedByUserId == null — старые и серверные записи) автором не
// считается: тот же fail-closed, что у chatDeleteDeniedReason. Вне проекта и в своём
// проекте (myRole нет) — можно, как раньше.
export function canDeleteTask(
  project: Pick<Project, 'myRole'> | null | undefined,
  task: Pick<Task, 'createdByUserId'> | null | undefined,
  myUserId: string | null | undefined,
): boolean {
  const role = project?.myRole;
  if (!role || role === 'owner') return true;
  if (role === 'reader') return false;
  return !!task?.createdByUserId && task.createdByUserId === myUserId;
}

// Гейт «запустить ход / ответить на вопрос» для входов вне композера (чипы-подсказки,
// «Повторить», «Зафиксировать», «Возобновить», сброс режимов). RunTurn есть у редактора,
// читателю бэк откажет — запрос не шлём вовсе. null — можно; строка — текст отказа для тоста и подсказки кнопки
export function turnDeniedReason(project: Project | null | undefined): string | null {
  const gate = participantComposerGate(project);
  return gate.blocked ? participantSendTitle(gate).replace(/^Не отправится: /, 'Недоступно: ') : null;
}

// Решения владельца (разрешить/запретить инструмент, ответ на план, эскалацию команды):
// на сервере это Configure, редактору закрыто так же, как читателю. Читателю — причина про
// чтение, редактору — про владельца. null — можно (владелец и обычный проект)
export const OWNER_DECISION = 'Недоступно: решает владелец проекта';
export function ownerDecisionDeniedReason(project: Project | null | undefined): string | null {
  return turnDeniedReason(project) ?? (isParticipant(project) ? OWNER_DECISION : null);
}

// Оборачивает действие гейтом: у участника вместо вызова — onDenied(текст), результат undefined.
// Проект читается в момент вызова (getProject), чтобы обёртка могла быть стабильной по ссылке
export function guardTurnAction<A extends unknown[], R>(
  getProject: () => Project | null | undefined,
  action: (...args: A) => R,
  onDenied: (reason: string) => void,
  reasonOf: (project: Project | null | undefined) => string | null = turnDeniedReason,
): (...args: A) => R | undefined {
  return (...args: A) => {
    const reason = reasonOf(getProject());
    if (reason) { onDenied(reason); return undefined; }
    return action(...args);
  };
}

// Блокировка отправки композера: проект есть и (ход недоступен по исполнению ИЛИ участник).
// Единственное место решения — из него же берётся disabled кнопки отправки и сброс режимов
export function composerExecBlocked(
  project: Project | null | undefined,
  execAvailable: boolean,
  gate: ParticipantComposerGate,
): boolean {
  return !!project && (!execAvailable || gate.blocked);
}
