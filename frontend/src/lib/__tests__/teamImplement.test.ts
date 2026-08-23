// Подписи бейджа режима «Командная реализация»: «волна N из M» берёт M из планового
// числа волн (plannedWaves), а не из потолка бюджета — иначе план в 2 волны показывался
// бы как «волна 1 из 4» (потолок maxWaves).
import { describe, it, expect } from 'vitest';
import type { ChatItem, ServerMessage, SessionTeamImplement, TeamEscalationKind, TeamPlan, TeamWaveLiveness, TeamWavePulse, TeamWaveTask } from '../../types';
import {
  teamImplementBadgeText, teamImplementStageShort, teamImplementTone,
  teamEscalationTone, teamEscalationInformational, teamEscalationDetailsMarkdown,
  teamImplementSwitchesMode, teamImplementModeHeld, teamImplementModeWarning,
  teamPlanRunLabel, TEAM_IMPLEMENT_MODE_HELD, TEAM_IMPLEMENT_AUTO_TITLE,
  teamPlanningIndicatorVisible, teamPlanningElapsedLabel, teamPlanningDoneText,
  TEAM_PLANNING_TITLE, TEAM_PLANNING_TEXT,
  teamPulseTone, teamPulseActivityLabel, teamPulseMeaning, teamPulseBadgeText,
  teamPulseBadgeShort, teamWaveTaskStatusLabel, teamWaveTaskRunningLabel,
  teamWaveTasksSorted, TEAM_IMPLEMENT_SHORT_NAME,
} from '../teamImplement';
import { applyServerMessage, initialChatState, type ChatState } from '../chatReducer';

// Включение режима меняет чужую настройку — режим прав чата (SessionManager.
// SetTeamImplementAsync переводит acceptEdits/bypass в auto). Предупреждение показываем
// только там, где переключать есть что, и подписи берём из MODE_META, а не свои
describe('предупреждение о смене режима прав чата', () => {
  it('переключение будет только из «Авто-правок» и «Без ограничений»', () => {
    expect(teamImplementSwitchesMode('acceptEdits')).toBe(true);
    expect(teamImplementSwitchesMode('bypass')).toBe(true);
  });

  it('в спрашивающих режимах переключать нечего — строки нет', () => {
    for (const m of ['default', 'plan', 'auto', 'dontAsk'] as const)
      expect(teamImplementSwitchesMode(m)).toBe(false);
  });

  it('текст называет режимы их подписями из интерфейса', () => {
    expect(teamImplementModeWarning('acceptEdits'))
      .toBe('Режим прав чата переключится с «Авто-правки» на «Авто»'
        + ' — иначе координатор сможет писать файлы в обход правила');
    expect(teamImplementModeWarning('bypass')).toContain('с «Без ограничений» на «Авто»');
  });

  it('сноска в поповере — только пока чат в удерживаемом режиме', () => {
    expect(teamImplementModeHeld('auto')).toBe(true);
    expect(teamImplementModeHeld('acceptEdits')).toBe(false);
    expect(teamImplementModeHeld('default')).toBe(false);
    expect(TEAM_IMPLEMENT_MODE_HELD).toContain('«Авто»');
  });
});

describe('подписи бейджа командной реализации', () => {
  it('план в две волны показывает «волна 1 из 2»', () => {
    expect(teamImplementBadgeText('wave', 1, 2)).toBe('Командная реализация · волна 1 из 2');
    expect(teamImplementStageShort('wave', 1, 2)).toBe('волна 1/2');
  });

  it('до запуска плана (plannedWaves = 0) число волн не выдумывается', () => {
    expect(teamImplementBadgeText('wave', 1, 0)).toBe('Командная реализация · волна 1');
    expect(teamImplementStageShort('wave', 1, 0)).toBe('волна 1');
  });

  it('остальные стадии подписаны текстами продуктового плана', () => {
    expect(teamImplementBadgeText('planning', 0, 0)).toBe('Командная реализация · планирование');
    expect(teamImplementBadgeText('awaitingDecision', 2, 3)).toBe('Командная реализация · нужно решение');
    expect(teamImplementBadgeText('idle', 2, 3)).toBe('Командная реализация · ждёт задачу');
  });

  // Интервью и проверка — стадии непрерывного контура (Э8/Э6): бейдж обязан их
  // показывать дословно, пока штаб спрашивает человека и пока идёт проверка волны
  it('стадии interview и checking подписаны и имеют короткие формы маркера', () => {
    expect(teamImplementBadgeText('interview', 0, 0)).toBe('Командная реализация · интервью');
    expect(teamImplementStageShort('interview', 0, 0)).toBe('вопросы');
    expect(teamImplementBadgeText('checking', 1, 2)).toBe('Командная реализация · проверка');
    expect(teamImplementStageShort('checking', 1, 2)).toBe('проверка');
  });

  it('тон стадий: интервью ждёт человека, проверка — работа команды', () => {
    expect(teamImplementTone('interview')).toBe('wait');
    expect(teamImplementTone('checking')).toBe('work');
  });

  // Ожидание вводной — не работа и не «стоит и ждёт решения»: тон muted (в бейдже
  // из-за него же гаснет пульс точки), в узкой строке списка чатов — «ожидает»
  it('стадия idle — muted-тон и короткая форма для маркера чата', () => {
    expect(teamImplementTone('idle')).toBe('idle');
    expect(teamImplementStageShort('idle', 0, 0)).toBe('ожидает');
    expect(teamImplementTone('wave')).toBe('work');
    expect(teamImplementTone('awaitingDecision')).toBe('wait');
  });
});

describe('карточка эскалации: тон и разбор деталей', () => {
  // Добавочная волна (Э5) — не проблема и не запрос решения: работа уже идёт,
  // поэтому карточка не должна выглядеть как warning «команда встала и ждёт тебя»
  it('waveAdded — штатный ход событий, информационная карточка', () => {
    expect(teamEscalationTone('waveAdded')).toBe('work');
    expect(teamEscalationInformational('waveAdded')).toBe(true);
  });

  it('остальные виды остаются как были, решения ждут только они', () => {
    expect(teamEscalationTone('blocker')).toBe('warning');
    expect(teamEscalationTone('waveGate')).toBe('success');
    expect(teamEscalationTone('stopped')).toBe('muted');
    expect(teamEscalationInformational('waveGate')).toBe(false);
  });

  // «Нужны уточнения» (Э8) — пауза, а не проблема: планировщику нужны ответы человека,
  // поэтому тон спокойный, как у остановки по команде, а не warning «что-то сломалось».
  // Карточка при этом ждёт решения — информационной она не является
  it('needsClarification — спокойный тон паузы, но карточка ждёт решения', () => {
    expect(teamEscalationTone('needsClarification')).toBe('muted');
    expect(teamEscalationInformational('needsClarification')).toBe(false);
  });

  // Бэкенд заводит новые виды раньше, чем фронт про них узнаёт: незнакомый токен
  // обязан отрисоваться дефолтной карточкой, а не уронить ленту исключением
  it('незнакомый с бэка kind не роняет карточку — дефолтный warning', () => {
    const unknown = 'somethingNewFromBackend' as TeamEscalationKind;
    expect(teamEscalationTone(unknown)).toBe('warning');
    expect(teamEscalationInformational(unknown)).toBe(false);
  });

  // Состав под-задач приходит строками «— Заголовок (волна N)»: длинное тире markdown
  // маркером списка не считает, поэтому перед рендером приводим его к «- ». Блок списка
  // отбивается пустыми строками — иначе строка под последним пунктом уезжает внутрь него
  it('состав под-задач становится markdown-списком, отбитым от абзацев', () => {
    const details = [
      'Под-задач: 2 · волн: 1 · исполнителей: 2.',
      '— Экспорт в XLSX (волна 1)',
      '— Кнопка выгрузки (волна 1)',
      'Работа уже идёт: авто-волны включены, и подтверждения она не ждёт.',
    ].join('\n');
    expect(teamEscalationDetailsMarkdown(details)).toBe([
      'Под-задач: 2 · волн: 1 · исполнителей: 2.',
      '',
      '- Экспорт в XLSX (волна 1)',
      '- Кнопка выгрузки (волна 1)',
      '',
      'Работа уже идёт: авто-волны включены, и подтверждения она не ждёт.',
    ].join('\n'));
  });

  it('уже дефисный список не ломается, короткое тире — тоже список', () => {
    expect(teamEscalationDetailsMarkdown('- Экспорт в XLSX (волна 1)')).toBe('- Экспорт в XLSX (волна 1)');
    expect(teamEscalationDetailsMarkdown('– Кнопка выгрузки (волна 2)')).toBe('- Кнопка выгрузки (волна 2)');
  });

  // Тире внутри строки — это тире текста, а не маркер: трогать его нельзя
  it('обычный абзац не меняется, тире внутри строки остаётся тире', () => {
    const text = 'Под-задача «Экспорт» провалилась дважды — перевыдача не помогла.';
    expect(teamEscalationDetailsMarkdown(text)).toBe(text);
  });

  it('готовые абзацы через пустую строку и пустой details', () => {
    expect(teamEscalationDetailsMarkdown('Волна 2 не стартовала целиком.\n\nБюджет: задачи 6 из 6'))
      .toBe('Волна 2 не стартовала целиком.\n\nБюджет: задачи 6 из 6');
    expect(teamEscalationDetailsMarkdown('')).toBe('');
  });
});

// Свёрнутая карточка запущенного плана: «идёт волна N из M» — только у текущего
// плана и только пока волна реально идёт. После завершения итерации (idle,
// waveNumber=0) и у старой версии плана (v1 после перепланирования) карточка
// обязана показывать честное «План запущен», а не вечную «волну 1 из 2»
describe('подпись свёрнутой карточки запущенного плана', () => {
  const plan = (id: string, waveCount: number) =>
    ({ id, waveCount }) as TeamPlan;

  it('идущая волна текущего плана подписана номером и потолком самого плана', () => {
    expect(teamPlanRunLabel(plan('p1', 2), { waveNumber: 1, planCardId: 'p1' }))
      .toBe('План запущен — идёт волна 1 из 2');
  });

  it('после завершения итерации (idle, waveNumber=0) волна не «идёт»', () => {
    expect(teamPlanRunLabel(plan('p1', 2), { waveNumber: 0, planCardId: 'p1' }))
      .toBe('План запущен');
  });

  it('старая версия плана не берёт ход волн чужого (текущего) плана', () => {
    expect(teamPlanRunLabel(plan('p1', 2), { waveNumber: 1, planCardId: 'p2' }))
      .toBe('План запущен');
  });

  it('план в одну волну и режим выключен (ctx=null) — без номера волны', () => {
    expect(teamPlanRunLabel(plan('p1', 1), { waveNumber: 1, planCardId: 'p1' }))
      .toBe('План запущен');
    expect(teamPlanRunLabel(plan('p1', 3), null)).toBe('План запущен');
  });
});

describe('тексты по спеке', () => {
  // Пунктуация тултипа чипа «Авто» — дословно из team-implement-mode.md:
  // «План согласуете один раз. Дальше команда работает сама…» (два предложения)
  it('тултип чипа «Авто» не склеивает два предложения запятой', () => {
    expect(TEAM_IMPLEMENT_AUTO_TITLE).toContain('один раз. Дальше команда работает сама');
  });
});

// Плашка «Команда готовит план…» на стадии планирования: появляется с входом
// в стадию и гаснет с карточкой плана или отказа (двух сообщений об одном быть
// не должно). Фикстуры повторяют форму событий team_implement/team_escalation.
describe('индикатор паузы планирования', () => {
  const state = (over: Partial<SessionTeamImplement> = {}): SessionTeamImplement => ({
    stage: 'planning', waveNumber: 0, plannedWaves: 0, autoWaves: true, stopped: false,
    executorPersonaIds: [], coordinatorNoCode: true, planVersion: 0,
    budget: {
      tasksUsed: 0, wavesUsed: 0, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 6, maxWaves: 4, maxRuns: 20, maxRetries: 3, maxWakeups: 3,
    },
    ...over,
  });

  const escalation = (resolved: boolean): ChatItem => ({
    kind: 'team_escalation', escalationId: 'e1',
    escalation: {
      id: 'e1', kind: 'productDecision', title: 'План не построился', details: '',
      actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
      taskId: null, wave: 0, resolved, chosenActionId: null,
    },
  });

  it('виден на стадии планирования', () => {
    expect(teamPlanningIndicatorVisible(state(), [])).toBe(true);
  });

  it('вне планирования не показывается — interview трогать нельзя, остальные стадии не молчат', () => {
    for (const stage of ['interview', 'confirming', 'wave', 'awaitingDecision', 'checking', 'idle'] as const)
      expect(teamPlanningIndicatorVisible(state({ stage }), [])).toBe(false);
  });

  it('остановленная практика не «готовит план»', () => {
    expect(teamPlanningIndicatorVisible(state({ stopped: true }), [])).toBe(false);
  });

  it('открытая карточка отказа гасит плашку — двух сообщений об одном быть не должно', () => {
    expect(teamPlanningIndicatorVisible(state(), [escalation(false)])).toBe(false);
  });

  it('погашенная карточка (повторное планирование) плашку возвращает', () => {
    expect(teamPlanningIndicatorVisible(state(), [escalation(true)])).toBe(true);
  });

  it('режим выключен (null) — плашки нет', () => {
    expect(teamPlanningIndicatorVisible(null, [])).toBe(false);
    expect(teamPlanningIndicatorVisible(undefined, [])).toBe(false);
  });

  it('течение времени: первую минуту «меньше минуты», дальше — полные минуты', () => {
    const t0 = 1_000_000;
    expect(teamPlanningElapsedLabel(t0, t0)).toBe('меньше минуты');
    expect(teamPlanningElapsedLabel(t0, t0 + 59_000)).toBe('меньше минуты');
    expect(teamPlanningElapsedLabel(t0, t0 + 60_000)).toBe('уже 1 мин');
    expect(teamPlanningElapsedLabel(t0, t0 + 150_000)).toBe('уже 2 мин');
    expect(teamPlanningElapsedLabel(t0, t0 + 4 * 60_000 + 30_000)).toBe('уже 4 мин');
  });

  it('отрицательная разница (рассинхрон часов) не роняет подпись', () => {
    expect(teamPlanningElapsedLabel(2_000, 1_000)).toBe('меньше минуты');
  });

  it('тексты объясняют, что пауза нормальна и займёт время', () => {
    expect(TEAM_PLANNING_TITLE).toBe('Команда готовит план…');
    expect(TEAM_PLANNING_TEXT).toContain('может занять несколько минут');
  });

  // Событие team_planning (live) точнее стадии: не ждёт записи файла плана на диск
  // между «планировщик закончил» и переключением стадии режима
  it('live=null — событие уже сказало «закончил», плашка гаснет даже если стадия ещё planning', () => {
    expect(teamPlanningIndicatorVisible(state(), [], null)).toBe(false);
  });

  it('live={startedAt} — плашка видна даже если стадия ещё не долетела (planning)', () => {
    expect(teamPlanningIndicatorVisible(state(), [], { startedAt: 1_000 })).toBe(true);
  });

  it('live не пришёл (undefined) — решает только стадия, как раньше', () => {
    expect(teamPlanningIndicatorVisible(state(), [], undefined)).toBe(true);
    expect(teamPlanningIndicatorVisible(state({ stage: 'confirming' }), [], undefined)).toBe(false);
  });

  it('остановка и отказ сильнее live: stopped и открытая карточка всё равно гасят', () => {
    expect(teamPlanningIndicatorVisible(state({ stopped: true }), [], { startedAt: 1_000 })).toBe(false);
    expect(teamPlanningIndicatorVisible(state(), [escalation(false)], { startedAt: 1_000 })).toBe(false);
  });
});

// Строка-итог успешного планирования («видно, что он сделал») — короткая, без состава плана
// (тот несёт карточка team_plan следом), только счётчики и грубое время
describe('teamPlanningDoneText', () => {
  it('склоняет под-задачи и волны, время — в секундах до минуты', () => {
    expect(teamPlanningDoneText(1, 1, 5_000)).toBe('План собран — 1 под-задача · 1 волна · за 5 с');
    expect(teamPlanningDoneText(5, 2, 46_000)).toBe('План собран — 5 под-задач · 2 волны · за 46 с');
    expect(teamPlanningDoneText(3, 3, 3_000)).toBe('План собран — 3 под-задачи · 3 волны · за 3 с');
  });

  it('от минуты и дальше — целые минуты', () => {
    expect(teamPlanningDoneText(2, 1, 90_000)).toBe('План собран — 2 под-задачи · 1 волна · за 2 мин');
  });

  it('waveCount=0 — волну не упоминаем (план ещё не считал волны)', () => {
    expect(teamPlanningDoneText(1, 0, 1_000)).toBe('План собран — 1 под-задача · за 1 с');
  });
});

// Симуляция потока событий: те же wire-сообщения, что шлёт бэкенд, прогоняются
// через редьюсер; плашка обязана появиться на входе в планирование и уйти при
// карточке плана (стадия confirming) или карточке отказа (стадия остаётся planning)
describe('индикатор планирования: поток событий через редьюсер', () => {
  const teamImplementMsg = (over: Partial<Extract<ServerMessage, { type: 'team_implement' }>> = {}) =>
    ({
      type: 'team_implement', active: true, stage: 'planning', waveNumber: 0,
      autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
      executorPersonaIds: ['p-1'], budget: null, planCardId: null, modeLocked: true,
      ...over,
    }) as unknown as ServerMessage;

  const teamPlanMsg: ServerMessage = {
    type: 'team_plan', planId: 'plan-1', resolved: false, approved: null,
    plan: {
      id: 'plan-1', request: 'сделать фичу', summary: 'делаем фичу', createdAt: '2026-08-04T12:00:00Z',
      waveCount: 1, executorCount: 1, subtasks: [], version: 1, assumptions: [], changes: [],
    },
  };

  const visible = (s: ChatState) => teamPlanningIndicatorVisible(
    s.teamImplement && s.teamImplement.active ? s.teamImplement : null, s.items);

  it('планирование → карточка плана: плашка появляется и уходит', () => {
    let s = applyServerMessage(initialChatState(), teamImplementMsg());
    expect(visible(s)).toBe(true);
    // Бэкенд сначала двигает стадию в confirming, затем публикует карточку плана
    s = applyServerMessage(s, teamImplementMsg({ stage: 'confirming', planCardId: 'plan-1', modeLocked: false }));
    expect(visible(s)).toBe(false);
    s = applyServerMessage(s, teamPlanMsg);
    expect(visible(s)).toBe(false);
    expect(s.items.some(i => i.kind === 'team_plan')).toBe(true);
  });

  it('планирование → отказ планировщика: плашку гасит карточка, стадия остаётся planning', () => {
    let s = applyServerMessage(initialChatState(), teamImplementMsg());
    expect(visible(s)).toBe(true);
    s = applyServerMessage(s, {
      type: 'team_escalation', escalationId: 'e1', kind: 'productDecision',
      title: 'План не построился: планировщик не уложился во время', details: '',
      actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
      taskId: null, wave: 0, resolved: false, chosenActionId: null,
    });
    // Стадия не менялась — планирование по-прежнему, но практика ждёт человека
    expect(s.teamImplement?.stage).toBe('planning');
    expect(visible(s)).toBe(false);
  });

  it('повторное планирование после отказа: карточка погашена, плашка снова видна', () => {
    let s = applyServerMessage(initialChatState(), teamImplementMsg());
    s = applyServerMessage(s, {
      type: 'team_escalation', escalationId: 'e1', kind: 'productDecision',
      title: 'План не построился', details: '',
      actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
      taskId: null, wave: 0, resolved: false, chosenActionId: null,
    });
    // Ответ человека переиздаёт карточку погашенной и возвращает стадию planning
    s = applyServerMessage(s, {
      type: 'team_escalation', escalationId: 'e1', kind: 'productDecision',
      title: 'План не построился', details: '',
      actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
      taskId: null, wave: 0, resolved: true, chosenActionId: 'retryPlan',
    });
    s = applyServerMessage(s, teamImplementMsg());
    expect(visible(s)).toBe(true);
  });
});

// === Э2: пульс волны (liveness → тон/текст, эфемерность, форматирование активности) ===
// Маппинг liveness на тон — единственная точка для бейджа/поповера. Тест пишет
// каждое значение как комментарий «почему именно этот тон»: alive — живой ритм,
// quiet — спокойное предупреждение, stalled/dead — нужна реакция
describe('пульс волны: тон и пояснения', () => {
  const LIVENESS_TONE: Record<TeamWaveLiveness, 'work' | 'warning' | 'danger'> = {
    alive: 'work',
    quiet: 'warning',
    stalled: 'danger',
    dead: 'danger',
  };
  for (const liveness of ['alive', 'quiet', 'stalled', 'dead'] as const) {
    it(`liveness=${liveness} → тон ${LIVENESS_TONE[liveness]}`, () => {
      expect(teamPulseTone(liveness)).toBe(LIVENESS_TONE[liveness]);
    });
  }

  it('незнакомый liveness с бэка падает в danger — лучше лишний раз предупредить', () => {
    // Известная договорённость на бэке: «похоже, зависло» — опасный класс.
    // Тихий default сломал бы инвариант безопасности
    expect(teamPulseTone('unknown_liveness' as TeamWaveLiveness)).toBe('danger');
  });

  // Тон продукта — «не stalled, а похоже, зависло»: штатный сценарий (сборка, тесты)
  // действительно занимает время, и тревожный warning тут был бы ложной тревогой.
  // На stalled/dead тон сдвигается к опасному — там бездействие штаба уже мешает
  it('«Что это значит» для каждого liveness — без сырых wire-токенов', () => {
    expect(teamPulseMeaning('alive')).toBe('Штаб работает: исполнители отвечают в обычном ритме');
    expect(teamPulseMeaning('quiet')).toContain('сборка');
    expect(teamPulseMeaning('stalled')).toMatch(/похоже, зависло/i);
    expect(teamPulseMeaning('dead')).toContain('остановлен');
    // Никаких 'alive' / 'stalled' / 'dead' в тексте — продуктовый язык
    for (const liveness of ['alive', 'quiet', 'stalled', 'dead'] as const) {
      expect(teamPulseMeaning(liveness)).not.toMatch(/\b(alive|quiet|stalled|dead)\b/i);
    }
  });
});

// Подпись активности в бейдже и поповере строится по серверным секундам тишины:
// считать по Date.now() на клиенте нельзя — дрейф часов расходился бы с бэком,
// и «N мин назад» перестало бы совпадать с реальной тишиной на сервере
describe('пульс волны: подпись активности по серверным quietSeconds', () => {
  const pulse = (liveness: TeamWaveLiveness, quietSeconds: number): TeamWavePulse => ({
    stage: 'wave', waveNumber: 1, plannedWaves: 2,
    tasksActive: 2, tasksTotal: 5,
    lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds, liveness,
  });

  it('alive — «активность N мин назад»', () => {
    expect(teamPulseActivityLabel(pulse('alive', 240))).toBe('активность 4 минуты назад');
    expect(teamPulseActivityLabel(pulse('alive', 60))).toBe('активность 1 минуту назад');
    expect(teamPulseActivityLabel(pulse('alive', 0))).toBe('активность 0 минут назад');
  });

  it('quiet — «тихо N мин» (долгая задача — норма)', () => {
    expect(teamPulseActivityLabel(pulse('quiet', 900))).toBe('тихо 15 минут');
    expect(teamPulseActivityLabel(pulse('quiet', 60))).toBe('тихо 1 минуту');
  });

  it('stalled — «похоже, зависло · N мин тишины»', () => {
    expect(teamPulseActivityLabel(pulse('stalled', 2100))).toBe('похоже, зависло · 35 минут тишины');
  });

  it('dead — без чисел, «процесс остановлен»', () => {
    // dead значит «штаб-процесс мёртв», секунды тишины тут уже не информативны
    expect(teamPulseActivityLabel(pulse('dead', 9999))).toBe('процесс остановлен');
  });

  // Округление вниз до минуты: 119с → 1 мин, 121с → 2 мин. Иначе подпись прыгала бы
  // каждую секунду, и человек не мог бы увидеть стабильную «4 мин назад»
  it('quietSeconds округляются вниз до минуты', () => {
    expect(teamPulseActivityLabel(pulse('alive', 119))).toBe('активность 1 минуту назад');
    expect(teamPulseActivityLabel(pulse('alive', 121))).toBe('активность 2 минуты назад');
  });

  // Склонение «минуту/минуты/минут» — единая форма, чтобы «1 минуту», «2 минуты»,
  // «25 минут» шли без опечаток
  it('склонение числительного по русскому', () => {
    expect(teamPulseActivityLabel(pulse('alive', 21 * 60))).toBe('активность 21 минуту назад');
    expect(teamPulseActivityLabel(pulse('alive', 22 * 60))).toBe('активность 22 минуты назад');
    expect(teamPulseActivityLabel(pulse('alive', 25 * 60))).toBe('активность 25 минут назад');
    expect(teamPulseActivityLabel(pulse('alive', 11 * 60))).toBe('активность 11 минут назад');
  });
});

// Полная подпись бейджа: «КР · волна 1 · 2/5 · активность 4 мин назад» — plannedWaves=0
// даёт просто «волна 1» (план ещё не запускался), как в teamImplementStageLabel
describe('пульс волны: подпись бейджа — полная и короткая формы', () => {
  const state = {
    stage: 'wave' as const, waveNumber: 1, plannedWaves: 2, autoWaves: true, stopped: false,
    executorPersonaIds: [], coordinatorNoCode: true, planVersion: 0,
    budget: {
      tasksUsed: 0, wavesUsed: 0, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 6, maxWaves: 4, maxRuns: 20, maxRetries: 3, maxWakeups: 3,
    },
  };
  const pulse = (liveness: TeamWaveLiveness, quietSeconds = 240): TeamWavePulse => ({
    stage: 'wave', waveNumber: 1, plannedWaves: 2,
    tasksActive: 2, tasksTotal: 5,
    lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds, liveness,
  });

  it('полная форма содержит волну, прогресс и активность', () => {
    expect(teamPulseBadgeText(state, pulse('alive', 240)))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · волна 1 из 2 · 2/5 · активность 4 минуты назад`);
  });

  it('plannedWaves=0 → просто «волна 1», без «из N»', () => {
    const s0 = { ...state, plannedWaves: 0 };
    const p0 = { ...pulse('alive', 240), plannedWaves: 0 };
    expect(teamPulseBadgeText(s0, p0))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · волна 1 · 2/5 · активность 4 минуты назад`);
  });

  it('dead: «процесс остановлен» вместо «активности» в полной форме', () => {
    expect(teamPulseBadgeText(state, pulse('dead', 9999)))
      .toContain('процесс остановлен');
  });

  // Короткая форма для узкой ширины: без «волна N из M», чтобы строка уместилась
  // на 320px. Главное — прогресс и активность, без номера волны и приставки
  it('короткая форма для узкой ширины', () => {
    expect(teamPulseBadgeShort(pulse('alive', 240)))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · 2/5 · 4 минуты назад`);
    expect(teamPulseBadgeShort(pulse('quiet', 900)))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · 2/5 · тихо 15 минут`);
    expect(teamPulseBadgeShort(pulse('stalled', 2100)))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · 2/5 · похоже, зависло`);
    expect(teamPulseBadgeShort(pulse('dead', 9999)))
      .toBe(`${TEAM_IMPLEMENT_SHORT_NAME} · 2/5 · процесс остановлен`);
  });
});

// Список задач поповера: текст статусов, время в работе, сортировка
describe('пульс волны: список задач в поповере', () => {
  it('статусы подписаны текстом продуктового плана, без wire-токенов', () => {
    expect(teamWaveTaskStatusLabel('todo')).toBe('в очереди');
    expect(teamWaveTaskStatusLabel('inProgress')).toBe('в работе');
    expect(teamWaveTaskStatusLabel('done')).toBe('готово');
    for (const s of ['todo', 'inProgress', 'done'] as const) {
      expect(teamWaveTaskStatusLabel(s)).not.toMatch(/\b(todo|inProgress|done)\b/);
    }
  });

  // Время в работе считается от startedAt, не от пульса — время старта задачи
  // стабильнее, чем серверные секунды тишины. «N мин в работе» — отдельный счётчик,
  // чтобы совпадал с задачами в диспетчерской
  it('время в работе — от startedAt до now', () => {
    const t0 = Date.parse('2026-08-23T11:00:00Z');
    expect(teamWaveTaskRunningLabel(
      { id: 't1', title: '', executorPersonaId: null, status: 'inProgress', updatedAt: '', startedAt: '2026-08-23T11:00:00Z' },
      t0 + 240_000,
    )).toBe('4 минуты в работе');
    expect(teamWaveTaskRunningLabel(
      { id: 't1', title: '', executorPersonaId: null, status: 'inProgress', updatedAt: '', startedAt: '2026-08-23T11:00:00Z' },
      t0 + 30_000,
    )).toBe('меньше минуты в работе');
    expect(teamWaveTaskRunningLabel(
      { id: 't1', title: '', executorPersonaId: null, status: 'todo', updatedAt: '', startedAt: null },
      t0,
    )).toBe('ещё не стартовала');
  });

  // Сортировка: в работе → в очереди → завершённые. Внутри группы — по updatedAt DESC.
  // Регрессия: до сортировки поповер показывал задачи в порядке id — выглядело случайно
  it('сортировка: в работе первые, внутри группы — свежие выше', () => {
    const older: TeamWaveTask = { id: 't1', title: 'old done', executorPersonaId: null, status: 'done', updatedAt: '2026-08-23T10:00:00Z', startedAt: null };
    const newer: TeamWaveTask = { id: 't2', title: 'new done', executorPersonaId: null, status: 'done', updatedAt: '2026-08-23T11:00:00Z', startedAt: null };
    const working: TeamWaveTask = { id: 't3', title: 'work', executorPersonaId: null, status: 'inProgress', updatedAt: '2026-08-23T09:00:00Z', startedAt: null };
    const queued: TeamWaveTask = { id: 't4', title: 'queue', executorPersonaId: null, status: 'todo', updatedAt: '2026-08-23T12:00:00Z', startedAt: null };
    const sorted = teamWaveTasksSorted([older, queued, working, newer]);
    expect(sorted.map(t => t.id)).toEqual(['t3', 't4', 't2', 't1']);
  });
});

// Эфемерность: пульс НЕ создаёт ChatItem и НЕ персистируется (живёт только в
// ChatState.teamWavePulse). Регрессия: до фичи событие могло ошибочно квалифицироваться
// как персистируемое — счётчик длины ленты врал бы, и перезагрузка истории «обгоняла»
// живую ленту
describe('пульс волны: эфемерность — событие не создаёт ChatItem и не пишется в историю', () => {
  const waveState = (over: Partial<Extract<ServerMessage, { type: 'team_implement' }>> = {}) =>
    ({
      type: 'team_implement', active: true, stage: 'wave', waveNumber: 1, plannedWaves: 2,
      autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
      executorPersonaIds: ['p-1'], budget: null, planCardId: null, modeLocked: false,
      ...over,
    }) as unknown as ServerMessage;

  const wavePulse = (over: Partial<Extract<ServerMessage, { type: 'team_wave_pulse' }>> = {}) =>
    ({
      type: 'team_wave_pulse', stage: 'wave', waveNumber: 1, plannedWaves: 2,
      tasksActive: 2, tasksTotal: 5,
      lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds: 240, liveness: 'alive',
      ...over,
    }) as unknown as ServerMessage;

  it('пульс не добавляет элементов в items', () => {
    const before: ChatState = applyServerMessage(initialChatState(), waveState());
    const after: ChatState = applyServerMessage(before, wavePulse());
    expect(after.items).toEqual(before.items);
  });

  it('teamWavePulse обновляется в ChatState', () => {
    const before: ChatState = applyServerMessage(initialChatState(), waveState());
    expect(before.teamWavePulse).toBeUndefined();
    const after: ChatState = applyServerMessage(before, wavePulse({ liveness: 'quiet' }));
    expect(after.teamWavePulse?.liveness).toBe('quiet');
    expect(after.teamWavePulse?.quietSeconds).toBe(240);
  });

  it('новый пульс заменяет прежний — последнее событие выигрывает', () => {
    let s: ChatState = applyServerMessage(initialChatState(), waveState());
    s = applyServerMessage(s, wavePulse({ liveness: 'alive', quietSeconds: 30 }));
    s = applyServerMessage(s, wavePulse({ liveness: 'stalled', quietSeconds: 2400 }));
    expect(s.teamWavePulse?.liveness).toBe('stalled');
    expect(s.teamWavePulse?.quietSeconds).toBe(2400);
  });

  // stage приходит с бэка: пульс шлётся и в финальной проверке. Раньше редьюсер
  // хардкодил stage: 'wave' и отбрасывал всё в checking — это лишало поповер
  // и бейдж актуального состояния при зависшей проверке
  it('stage берётся из события, а не хардкодится', () => {
    const after: ChatState = applyServerMessage(
      applyServerMessage(initialChatState(), waveState()),
      wavePulse({ stage: 'checking' }),
    );
    expect(after.teamWavePulse?.stage).toBe('checking');
  });
});

// Подпись пульса различается по стадии: на проверке пишем «проверка», без номера
// волны — иначе бейдж при зависшей проверке показывал бы чужой «волна 1/2»
describe('teamPulseBadgeText под стадию', () => {
  const state = {} as SessionTeamImplement;
  const basePulse = {
    stage: 'wave' as const, waveNumber: 1, plannedWaves: 2,
    tasksActive: 2, tasksTotal: 5,
    lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds: 240, liveness: 'alive' as TeamWaveLiveness,
  };

  it('на волне подпись включает номер волны', () => {
    expect(teamPulseBadgeText(state, basePulse)).toContain('волна 1 из 2');
  });

  it('на проверке подпись — «проверка», номера волны нет', () => {
    const text = teamPulseBadgeText(state, { ...basePulse, stage: 'checking' });
    expect(text).toContain('проверка');
    expect(text).not.toContain('волна');
  });
});
