// Режим «Командная реализация»: подписи стадий и тоны
// бейджа/маркера. Тексты — дословно из docs/architecture/team-implement-mode.md («Тексты»)
// и макета docs/mockups/team-implement-mode.html (короткие формы маркера).

import type { SessionTeamImplement, TeamEscalationKind, TeamImplementBudget, TeamImplementStage, TeamPlan } from '../types';
import { MODE_META, type Mode } from './modes';

// Тон по тому, кто должен действовать: work — команда работает (accent),
// wait — практика стоит и ждёт человека (warning), idle — ждёт задачу (muted)
export type TeamImplementTone = 'work' | 'wait' | 'idle';

export function teamImplementTone(stage: TeamImplementStage): TeamImplementTone {
  switch (stage) {
    case 'interview':
    case 'confirming':
    case 'awaitingDecision':
      return 'wait';
    case 'idle':
      return 'idle';
    default:
      return 'work';
  }
}

// «Волна N из M»: M — плановое число волн итерации (plannedWaves из карточки плана),
// а НЕ потолок бюджета: тот про расход и при плане в 2 волны врал бы «из 4».
// 0 — план ещё не запускался, показываем просто «волна N».
function waveText(waveNumber: number, plannedWaves?: number): string {
  return plannedWaves && plannedWaves > 0 ? `волна ${waveNumber} из ${plannedWaves}` : `волна ${waveNumber}`;
}

// Полная подпись стадии для бейджа в композере (и тултипа маркера)
export function teamImplementStageLabel(stage: TeamImplementStage, waveNumber: number, plannedWaves?: number): string {
  switch (stage) {
    case 'interview': return 'интервью';
    case 'planning': return 'планирование';
    case 'confirming': return 'ждёт подтверждения';
    case 'wave': return waveText(waveNumber, plannedWaves);
    case 'awaitingDecision': return 'нужно решение';
    case 'checking': return 'проверка';
    case 'idle': return 'ждёт задачу';
  }
}

// Короткая форма для маркера в узкой строке списка чатов
export function teamImplementStageShort(stage: TeamImplementStage, waveNumber: number, plannedWaves?: number): string {
  switch (stage) {
    case 'interview': return 'вопросы';
    case 'planning': return 'планирует';
    case 'confirming': return 'согласование';
    case 'wave': return plannedWaves && plannedWaves > 0 ? `волна ${waveNumber}/${plannedWaves}` : `волна ${waveNumber}`;
    case 'awaitingDecision': return 'решение';
    case 'checking': return 'проверка';
    case 'idle': return 'ожидает';
  }
}

// Полная строка бейджа: «Командная реализация · <стадия>»
export function teamImplementBadgeText(stage: TeamImplementStage, waveNumber: number, plannedWaves?: number): string {
  return `Командная реализация · ${teamImplementStageLabel(stage, waveNumber, plannedWaves)}`;
}

// Описание режима — тултип бейджа и текст поповера
export const TEAM_IMPLEMENT_DESCRIPTION =
  'Чат работает как штаб: задачи ставятся исполнителям, их чаты видны под этим в списке. ' +
  'Напишите, что ещё нужно сделать — команда возьмёт в работу';

// Тултип чипа «Авто» (текст тумблера + подпись из плана, дословно из спеки)
export const TEAM_IMPLEMENT_AUTO_TITLE =
  'Авто-волны — не спрашивать после каждой волны. ' +
  'План согласуете один раз. Дальше команда работает сама, пока хватает бюджета';

// Подпись свёрнутой карточки запущенного плана. «Идёт волна N из M» — только когда
// волна реально идёт (waveNumber > 0: на стадии idle бэкенд обнуляет номер) И карточка —
// текущий план режима: у старой версии (v1 после перепланирования) ход волн относится
// к чужому плану, и карточка вечно врала бы «идёт волна». Иначе — просто «План запущен».
export function teamPlanRunLabel(
  plan: TeamPlan,
  ctx: { waveNumber: number; planCardId: string | null } | null,
): string {
  const live = !!ctx && ctx.waveNumber > 0 && ctx.planCardId === plan.id;
  if (!live || plan.waveCount <= 1) return 'План запущен';
  return `План запущен — идёт волна ${Math.min(ctx.waveNumber, plan.waveCount)} из ${plan.waveCount}`;
}

// Правило «Координатор не пишет код» — строка состояния в поповере бейджа
export const TEAM_IMPLEMENT_NO_CODE_ON = 'Координатор не пишет код — любая работа идёт задачей исполнителю';
export const TEAM_IMPLEMENT_NO_CODE_OFF = 'Координатор может править файлы сам, минуя задачи';

// === Режим прав чата под правилом «координатор не пишет код» ===
// Гард проверяет shell-команду в момент permission-запроса, а CLI спрашивает разрешение
// не в любом режиме прав: в «Авто-правках» и «Без ограничений» запись через терминал
// одобряется на стороне CLI, минуя сервер. Поэтому при включении режима бэкенд сам
// переводит чат в «Авто» (SessionManager.SetTeamImplementAsync) — чужую настройку меняем
// молча, значит обязаны о ней сказать.
const TEAM_IMPLEMENT_MODE: Mode = 'auto';

// Режимы, из которых чат будет переключён (зеркало условия на бэке)
export function teamImplementSwitchesMode(mode: Mode): boolean {
  return mode === 'acceptEdits' || mode === 'bypass';
}

// Предупреждение в настройках механики — ДО включения, пока настройка ещё своя
export function teamImplementModeWarning(mode: Mode): string {
  return `Режим прав чата переключится с «${MODE_META[mode].label}» на «${MODE_META[TEAM_IMPLEMENT_MODE].label}»`
    + ' — иначе координатор сможет писать файлы в обход правила';
}

// Сноска в поповере бейджа — ПОСЛЕ включения, чтобы вернувшийся через день понял,
// откуда взялись подтверждения. Признака «переключение случилось» на проводе нет,
// поэтому говорим о состоянии, а не о событии: строка верна и когда режим переключили
// мы, и когда человек сам выбрал «Авто» заранее (в обоих случаях он им и удерживается)
export const TEAM_IMPLEMENT_MODE_HELD =
  `Права чата держатся на «${MODE_META[TEAM_IMPLEMENT_MODE].label}» — иначе координатор обойдёт правило через терминал`;

export function teamImplementModeHeld(mode: Mode): boolean {
  return mode === TEAM_IMPLEMENT_MODE;
}

// === Э8: план-режим на стадиях интервью/планирования ===
// Штаб думает — селектор режима в композере заблокирован на «План» до согласования плана.
// Live-состояние (после первого события team_implement) уже несёт готовый modeLocked;
// REST-гидратация до первого события считает его сама из savedMode (SessionTeamImplement.SavedMode)
export function teamImplementModeLocked(state: SessionTeamImplement): boolean {
  return state.modeLocked ?? state.savedMode != null;
}

// Тултип заблокированного селектора режима (текст — «Тексты Э8» продуктового плана).
// Тот же текст возвращает бэкенд в ошибке PUT /chats/{id}/mode, если запрос всё же ушёл
export const TEAM_IMPLEMENT_MODE_LOCKED_TOOLTIP = 'Штаб планирует. Режим вернётся после согласования плана';

// Подтверждение выключения режима
export const TEAM_IMPLEMENT_DISABLE_TITLE = 'Выключить командную реализацию?';
export const TEAM_IMPLEMENT_DISABLE_TEXT =
  'Текущие исполнители доработают свои задачи, новые волны не стартуют. ' +
  'Чат станет обычным разговором — включить режим обратно можно в любой момент.';

// Подтверждение остановки прогона (кнопка «Остановить» в поповере бейджа).
// Текст — из таблицы «Эскалация и остановки»: пользователь нажал «Остановить»
export const TEAM_IMPLEMENT_STOP_TITLE = 'Остановить практику?';
export const TEAM_IMPLEMENT_STOP_TEXT =
  'Текущие исполнители дорабатывают, новые не стартуют. ' +
  'Карточка остановки появится в ленте — по ней команду можно продолжить или завершить итерацию.';
// Состояние «уже остановлено»: кнопки нет, продолжение — по карточке в ленте
export const TEAM_IMPLEMENT_STOPPED_HINT =
  'Практика остановлена: новые волны не стартуют. Продолжить — по карточке остановки в ленте';

// Расход бюджета итерации для поповера бейджа: задачи и волны — то, по чему человек
// понимает, близко ли исчерпание. Остальные потолки (запуски, перевыдачи, пробуждения)
// в строку не выносим — она должна читаться с одного взгляда
export function teamBudgetLine(budget: TeamImplementBudget): string | null {
  if (!budget || budget.maxTasks <= 0) return null;
  return `Бюджет итерации: задачи ${budget.tasksUsed} из ${budget.maxTasks} · волны ${budget.wavesUsed} из ${budget.maxWaves}`;
}

// Бюджет на исходе: подсвечиваем строку, пока остановка ещё не случилась
export function teamBudgetTight(budget: TeamImplementBudget): boolean {
  if (!budget || budget.maxTasks <= 0) return false;
  const left = (used: number, max: number) => (max > 0 ? max - used : Number.POSITIVE_INFINITY);
  return left(budget.tasksUsed, budget.maxTasks) <= 2
    || left(budget.wavesUsed, budget.maxWaves) <= 1
    || left(budget.runsUsed, budget.maxRuns) <= 3;
}

// Тон карточки остановки: warning — проблема ждёт человека (8 триггеров таблицы),
// success — гейт «волна закрыта, запускать следующую?», muted — пауза по команде человека,
// work — штатный ход событий, команда работает прямо сейчас (accent, как у бейджа режима).
// Неизвестный с бэка триггер попадает в warning: лучше лишний раз показать «посмотри»,
// чем нарисовать нейтральную карточку поверх настоящей проблемы
export type TeamEscalationTone = 'warning' | 'success' | 'muted' | 'work';

export function teamEscalationTone(kind: TeamEscalationKind): TeamEscalationTone {
  switch (kind) {
    case 'waveGate': return 'success';
    // Пауза, а не проблема (Э8): планировщику нужны уточнения, а не что-то сломалось —
    // тот же спокойный тон, что у паузы по команде человека
    case 'stopped':
    case 'needsClarification':
      return 'muted';
    case 'waveAdded': return 'work';
    default: return 'warning';
  }
}

// Информационная карточка: практика НЕ остановлена, работа идёт (зеркало
// TeamEscalationKindExtensions.IsInformational на бэке). В ленте такая карточка не
// должна читаться как «команда встала и ждёт тебя»: кнопка — не главное действие
export function teamEscalationInformational(kind: TeamEscalationKind): boolean {
  return kind === 'waveAdded';
}

// Строка details карточки: бэкенд шлёт многострочный текст, где состав под-задач идёт
// строками «— Заголовок (волна N)». Одной простынёй такой состав не читается, поэтому
// режем на абзацы и пункты — рендер рисует пункты списком
export interface TeamEscalationDetailsLine {
  kind: 'text' | 'item';
  text: string;
}

export function teamEscalationDetailsLines(details: string): TeamEscalationDetailsLine[] {
  return details
    .split('\n')
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .map(line => /^[—–-]\s+/.test(line)
      ? { kind: 'item' as const, text: line.replace(/^[—–-]\s+/, '') }
      : { kind: 'text' as const, text: line });
}

// Комментарий к решению нужен там, где кнопки не заменяют слов человека:
// блокер («Ответить» — что делать исполнителю) и продуктовая развилка (свой вариант)
export function teamEscalationNeedsComment(kind: TeamEscalationKind): boolean {
  return kind === 'blocker' || kind === 'productDecision';
}

// Подписи поля ответа. У блокера — ответ исполнителю, у развилки — свой вариант
export const TEAM_ESCALATION_REPLY_PLACEHOLDER = 'Что делать? Ответ уйдёт координатору обычным сообщением';
export const TEAM_ESCALATION_REPLY_HINT_DECISION = 'Ни один вариант не подходит? Напишите свой — решение уйдёт координатору';
