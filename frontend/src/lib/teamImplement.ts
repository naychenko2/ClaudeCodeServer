// Режим «Командная реализация»: подписи стадий и тоны
// бейджа/маркера. Тексты — дословно из docs/architecture/team-implement-mode.md («Тексты»)
// и макета docs/mockups/team-implement-mode.html (короткие формы маркера).

import type { ChatItem, SessionTeamImplement, TeamEscalationKind, TeamImplementBudget, TeamImplementStage, TeamPlan, TeamWaveLiveness, TeamWavePulse, TeamWaveTask, TeamWaveTaskStatus } from '../types';
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

// === Пульс волны (Э2 КР-наблюдаемости) ===
// alive   — живая работа (accent)
// quiet   — тишина дольше порога, долгая задача (сборка/тесты) — это нормально (warning)
// stalled — тишина дольше второго порога, похоже на зависшее (danger)
// dead    — процесс штаба остановлен при активной волне (danger)
export type TeamPulseTone = 'work' | 'warning' | 'danger';

export function teamPulseTone(liveness: TeamWaveLiveness): TeamPulseTone {
  switch (liveness) {
    case 'alive': return 'work';
    case 'quiet': return 'warning';
    case 'stalled':
    case 'dead': return 'danger';
    // Незнакомый wire-токен с бэка — в danger: тихий default сломал бы инвариант
    // безопасности, лучше лишний раз предупредить о проблемном состоянии
    default: return 'danger';
  }
}

// Сколько минут прошло с момента активности штаба (по серверным секундам тишины).
// Сервер даёт секунды — на фронте считать не надо: пересчёт часов клиента даёт
// расхождение в минуты при долгом простое, и бейдж перестаёт совпадать с сервером
function minutesFromSeconds(s: number): number {
  return Math.max(0, Math.floor(s / 60));
}

// Короткая подпись активности штаба для бейджа: «активность N мин назад» при alive,
// «тихо N мин» при quiet, «похоже, зависло» при stalled, «процесс остановлен» при dead.
// Считает по server-given quietSeconds — иначе клиентские часы расходились бы с бэком
export function teamPulseActivityLabel(pulse: TeamWavePulse): string {
  const mins = minutesFromSeconds(pulse.quietSeconds);
  switch (pulse.liveness) {
    case 'alive': return `активность ${pluralMinutesShort(mins)} назад`;
    case 'quiet': return `тихо ${pluralMinutesShort(mins)}`;
    case 'stalled': return `похоже, зависло · ${pluralMinutesShort(mins)} тишины`;
    case 'dead': return 'процесс остановлен';
  }
}

// Короткий текст с числом минут в именительном: «4 мин», «1 мин», «25 мин».
// У «active N min ago» это «N мин назад» — та же форма, что в остальных метриках
// («уже 2 мин» у плашки планирования). Своя копия из pluralRu, чтобы не таскать тяжёлую
// функцию наружу и не плодить кейсы в одном общем склонении
function pluralMinutesShort(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  const word = (m10 === 1 && m100 !== 11) ? 'минуту'
    : (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) ? 'минуты'
      : 'минут';
  return `${n} ${word}`;
}

// «Что это значит» в поповере: одна-две строки на состояние. Тон продукта — не «stalled»,
// а «похоже, зависло». Объясняем, что долгая тишина — не всегда авария (сборка/тесты)
export function teamPulseMeaning(liveness: TeamWaveLiveness): string {
  switch (liveness) {
    case 'alive': return 'Штаб работает: исполнители отвечают в обычном ритме';
    case 'quiet': return 'Тихо, но в пределах нормы: долгая задача — это нормально (сборка, тесты)';
    case 'stalled': return 'Похоже, зависло: тишина дольше обычного. Перезапуск будет этапом 3';
    case 'dead': return 'Процесс штаба остановлен. Исполнители не получат следующих задач, пока штаб не вернётся';
  }
}

// Стадии, на которых пульс волны может приходить: волна исполнителей и финальная
// проверка — обе «дышат» (задачи крутятся). На остальных стадиях пульса нет, и
// бейдж показывает только подпись стадии — единая точка, чтобы не дублировать
// условие в гардах и стилях
export function teamPulseStage(stage: TeamImplementStage): boolean {
  return stage === 'wave' || stage === 'checking';
}

// Полная подпись бейджа при живом пульсе на стадии волны/проверки.
// «КР · волна 1 · 2/5 · активность 4 мин назад» — plannedWaves=0 даёт просто «волна 1»
// (план ещё не запускался, как в teamImplementStageLabel). На стадии проверки номер
// волны не нужен — пишем «проверка», тон активности тот же по liveness. state не
// используется напрямую — оставлен в сигнатуре как единая точка для будущих полей
export function teamPulseBadgeText(_state: SessionTeamImplement, pulse: TeamWavePulse): string {
  const stage = pulse.stage === 'checking'
    ? teamImplementStageLabel('checking', 0, 0)
    : teamImplementStageLabel('wave', pulse.waveNumber, pulse.plannedWaves);
  return `${teamMechanicShort} · ${stage} · ${pulse.tasksActive}/${pulse.tasksTotal} · ${teamPulseActivityLabel(pulse)}`;
}

// Короткая форма для узкой ширины/мобилы: «КР · 2/5 · 4 мин».
// Сохраняем главное — сколько задач идёт и тон активности, без номера волны и
// приставки «волна» — на 320px строка должна умещаться целиком. На проверке формат
// тот же, что и на волне (счётчик задач/тон — основная информация)
export function teamPulseBadgeShort(pulse: TeamWavePulse): string {
  const mins = minutesFromSeconds(pulse.quietSeconds);
  switch (pulse.liveness) {
    case 'alive': return `${teamMechanicShort} · ${pulse.tasksActive}/${pulse.tasksTotal} · ${pluralMinutesShort(mins)} назад`;
    case 'quiet': return `${teamMechanicShort} · ${pulse.tasksActive}/${pulse.tasksTotal} · тихо ${pluralMinutesShort(mins)}`;
    case 'stalled': return `${teamMechanicShort} · ${pulse.tasksActive}/${pulse.tasksTotal} · похоже, зависло`;
    case 'dead': return `${teamMechanicShort} · ${pulse.tasksActive}/${pulse.tasksTotal} · процесс остановлен`;
  }
}

// Имя механики «КР» — единая точка, чтобы не дублировать teamMechanic('implementMode').shortName
// и не зависеть от модуля team/TeamMechanicBadge внутри lib/. Должно совпадать с маркером
// композера и маркером списка чатов
export const TEAM_IMPLEMENT_SHORT_NAME = 'КР';
const teamMechanicShort = TEAM_IMPLEMENT_SHORT_NAME;

// === Список задач в поповере бейджа ===
// Текст статуса задачи волны — для строки поповера. Не путать со стадиямией режима
// (teamImplementTone) и общим тоном задачи (TaskStatus) — здесь конкретный «в работе»
// / «готово» / «провалилась» / «отменена», как их видит владелец штаба
export function teamWaveTaskStatusLabel(status: TeamWaveTaskStatus): string {
  switch (status) {
    case 'todo': return 'в очереди';
    case 'inProgress': return 'в работе';
    case 'done': return 'готово';
  }
}

// Сколько минут задача в работе — от startedAt до now (мс). Нет startedAt — «ещё не стартовала».
// Серверных секунд тут нет, считаем по Date.now(): время старта задачи стабильнее, чем
// пульс штаба, и дрейф часов на ±секунду не искажает «N мин в работе»
export function teamWaveTaskRunningLabel(task: TeamWaveTask, now: number): string {
  if (!task.startedAt) return 'ещё не стартовала';
  const start = Date.parse(task.startedAt);
  if (!Number.isFinite(start)) return '';
  const mins = Math.max(0, Math.floor((now - start) / 60000));
  return mins < 1 ? 'меньше минуты в работе' : `${pluralMinutesShort(mins)} в работе`;
}

// Признак «задача прямо сейчас работает» — для сортировки и подсветки. Отдельная
// функция, чтобы не дублировать switch по статусам в двух местах поповера
export function teamWaveTaskActive(task: TeamWaveTask): boolean {
  return task.status === 'inProgress';
}

// Безопасный счёт: сколько задач реально в работе (на случай, если бэк пришлёт
// статусы, которых мы ещё не знаем). Используется и в подсчёте «2/5» строки бейджа,
// и в сортировке списка
export function countTeamWaveActive(tasks: TeamWaveTask[]): number {
  return tasks.reduce((n, t) => n + (t.status === 'inProgress' ? 1 : 0), 0);
}

// Сортировка задач для поповера: сначала в работе, потом в очереди, потом завершённые
// (готовые/провалившиеся/отменённые). Внутри группы — по updatedAt DESC
export function teamWaveTasksSorted(tasks: TeamWaveTask[]): TeamWaveTask[] {
  const rank = (s: TeamWaveTaskStatus): number => {
    switch (s) {
      case 'inProgress': return 0;
      case 'todo': return 1;
      default: return 2;
    }
  };
  const ts = (s: string) => Date.parse(s) || 0;
  return [...tasks].sort((a, b) => {
    const r = rank(a.status) - rank(b.status);
    if (r !== 0) return r;
    return ts(b.updatedAt) - ts(a.updatedAt);
  });
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

// Details карточки — markdown (его пишет модель), рендерится MarkdownContent. Правим
// только состав под-задач: бэкенд шлёт его строками «— Заголовок (волна N)», а длинное
// тире markdown маркером списка не считает — приводим ведущие «—»/«–» к «- ». Блок
// списка отбиваем пустыми строками: без них строка под последним пунктом становится
// его ленивым продолжением (CommonMark) и уезжает внутрь пункта. Остальной текст —
// как есть, тире внутри строки не трогаем
const DETAILS_ITEM = /^[ \t]*[-*+][ \t]+/;

export function teamEscalationDetailsMarkdown(details: string): string {
  if (!details) return '';
  const lines = details.split('\n').map(line => line.replace(/^([ \t]*)[—–][ \t]+/, '$1- '));
  const out: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const prev = lines[i - 1];
    const blank = (s: string) => s.trim().length === 0;
    if (prev !== undefined && !blank(prev) && !blank(lines[i])
      && DETAILS_ITEM.test(prev) !== DETAILS_ITEM.test(lines[i])) out.push('');
    out.push(lines[i]);
  }
  return out.join('\n').trim();
}

// Комментарий к решению нужен там, где кнопки не заменяют слов человека:
// блокер («Ответить» — что делать исполнителю) и продуктовая развилка (свой вариант)
export function teamEscalationNeedsComment(kind: TeamEscalationKind): boolean {
  return kind === 'blocker' || kind === 'productDecision';
}

// === Индикатор паузы планирования ===
// Между концом интервью и карточкой плана лента молчит минутами (потолок планировщика
// 300с), и тишина читается как «всё встало» (прод 2026-08-04). На время стадии planning
// в ленте живёт спокойная плашка; исчезает она с появлением карточки плана или отказа.

// Тексты плашки: смысл — «это нормально и займёт время», планирование большой фичи
// идёт минутами, а не секундами
export const TEAM_PLANNING_TITLE = 'Команда готовит план…';
export const TEAM_PLANNING_TEXT = 'Изучает задачу и собирает план — это может занять несколько минут';

// Видна ли плашка планирования. Стадия planning приходит событием team_implement и из
// REST-гидратации, момент входа фронт знает. Двух сообщений об одном быть не должно:
// карточка отказа (или любая другая открытая карточка остановки) означает, что план
// прямо сейчас НЕ готовится — практика ждёт человека, поэтому плашка гаснет. «Остановить»
// на стадии планирования свою карточку не шлёт (Stopped приходит без неё) — отсюда
// отдельная проверка stopped.
//
// live — состояние из события team_planning (см. ChatState.teamPlanning): точнее стадии
// режима, потому что не зависит от записи файла плана на диск между «планировщик закончил»
// и переключением стадии на confirming/planning-заново. undefined — событий не было в этой
// сессии (например, чат открыли посреди планирования) — тогда решает только стадия, как
// раньше; null — событие уже сказало «планировщик закончил», плашка гаснет НЕМЕДЛЕННО, не
// дожидаясь стадии (иначе между отказом и карточкой team_escalation плашка соврала бы,
// что штаб всё ещё думает)
export function teamPlanningIndicatorVisible(
  state: SessionTeamImplement | null | undefined,
  items: ChatItem[],
  live?: { startedAt: number } | null,
): boolean {
  if (!state || state.stopped) return false;
  if (live === null) return false;
  if (state.stage !== 'planning' && !live) return false;
  return !items.some(it => it.kind === 'team_escalation' && !it.escalation.resolved);
}

// Склонение числительного (ру) — своя копия, как и в остальных местах проекта
// (TeamPlanView, ProductHistory, BackupWidget…): маленькая и специфичная, шарить не стоит.
function pluralRu(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

// Время планирования коротко: секунды до минуты, дальше — целые минуты (та же грубость,
// что у teamPlanningElapsedLabel — точность до секунды тут не нужна)
function teamPlanningElapsedShort(elapsedMs: number): string {
  const totalSec = Math.round(elapsedMs / 1000);
  if (totalSec < 60) return `за ${totalSec} с`;
  return `за ${Math.round(totalSec / 60)} мин`;
}

// Итоговая строка успешного планирования — «строчка в потоке», не карточка: план целиком
// (под-задачи, исполнители) уже покажет карточка team_plan следом, эта строка — только
// закрытие плашки «идёт работа» фактом и временем, которого у карточки плана нет
export function teamPlanningDoneText(subtaskCount: number, waveCount: number, elapsedMs: number): string {
  const parts = [`${subtaskCount} ${pluralRu(subtaskCount, 'под-задача', 'под-задачи', 'под-задач')}`];
  if (waveCount > 0) parts.push(`${waveCount} ${pluralRu(waveCount, 'волна', 'волны', 'волн')}`);
  parts.push(teamPlanningElapsedShort(elapsedMs));
  return `План собран — ${parts.join(' · ')}`;
}

// Признак течения времени на плашке — тот же приём, что «работает N мин» у фаз
// workflow-карточки: отсчёт от момента, когда клиент УВИДЕЛ стадию (таймстампа начала
// стадии на проводе нет), поэтому после перезагрузки посреди планирования счёт идёт
// от нуля. Первую минуту не считаем: «меньше минуты» спокойнее мелькающих секунд.
export function teamPlanningElapsedLabel(startedAt: number, now: number): string {
  const mins = Math.floor(Math.max(0, now - startedAt) / 60000);
  return mins < 1 ? 'меньше минуты' : `уже ${mins} мин`;
}

// Подписи поля ответа. У блокера — ответ исполнителю, у развилки — свой вариант
export const TEAM_ESCALATION_REPLY_PLACEHOLDER = 'Что делать? Ответ уйдёт координатору обычным сообщением';
export const TEAM_ESCALATION_REPLY_HINT_DECISION = 'Ни один вариант не подходит? Напишите свой — решение уйдёт координатору';
