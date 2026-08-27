import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { DeployJournalRecord, DeployJournalStep } from './api';
import {
  deriveState, outcomeState, runPhaseState, phaseGroup, isTerminalPhase,
  buildPlan, buildStepRows, groupMs, msBeforeGroup, failedStep, switchingHint,
  durLabel, clockLabel, etaLabel, sane, parseTime,
  isDeployStart, parseDeployId,
  writeWatch, readWatch, clearWatch, markInitiator, isInitiator, clearInitiator,
  type StateInput, type StepRow, type DeployPlan,
} from './deployProgress';

// Модуль — шов с ЧУЖИМ форматом: журнал пишет внешний агент выкатки (ADR-010), он
// версионируется отдельно от сервера. Поэтому набор проверяет не «код делает, что написано»,
// а живучесть: незнакомая фаза, незнакомый шаг и оборванная связь не должны врать человеку.

const step = (name: string, ms: number, status?: string | null): DeployJournalStep =>
  ({ name, ms, ...(status === undefined ? {} : { status }) });

const record = (steps: DeployJournalStep[] | undefined, status = 'succeeded'): DeployJournalRecord => ({
  id: 'd-' + status,
  phase: status,
  steps,
  result: { ok: status === 'succeeded', status },
});

describe('deriveState', () => {
  // Живая выкатка: журнал прочитан, сервер отвечает
  const live: StateInput = {
    hasDeployId: true, phase: 'building', result: null,
    loaded: true, restored: false, reachable: true,
  };

  it('без deployId карточка ждёт заявку, а не грузится', () => {
    expect(deriveState({ ...live, hasDeployId: false, loaded: false })).toBe('queued');
  });

  it('журнал ещё не прочитан и метки нет — загрузка', () => {
    expect(deriveState({ ...live, loaded: false })).toBe('loading');
  });

  it('идёт сборка — показываем фазу журнала', () => {
    expect(deriveState(live)).toBe('building');
    expect(deriveState({ ...live, phase: 'queued' })).toBe('queued');
    expect(deriveState({ ...live, phase: 'verifying' })).toBe('verifying');
  });

  it('незнакомая фаза не роняет вывод — считаем сборкой', () => {
    expect(deriveState({ ...live, phase: 'warming-up' })).toBe('building');
    expect(deriveState({ ...live, phase: null })).toBe('building');
  });

  // Главный инвариант фичи: с шага stop прод погашен НАМЕРЕННО, и молчание сервера —
  // это пауза, а не провал выкатки.
  it('сервер недостижим на переключении — мёртвое окно, а НЕ ошибка', () => {
    const s = deriveState({ ...live, phase: 'switching', reachable: false });
    expect(s).toBe('dead');
    expect(s).not.toBe('failed');
  });

  it('сервер недостижим на проверке прода — тоже мёртвое окно', () => {
    // Прод уже поднимают, но порт ещё не отвечает: ответа нет — итога тоже нет
    expect(deriveState({ ...live, phase: 'verifying', reachable: false })).toBe('dead');
  });

  it('состояние восстановлено из метки при мёртвом сервере — сразу мёртвое окно, не загрузка', () => {
    // Страницу перезагрузили из кеша SW, журнал прочитать не у кого
    expect(deriveState({ ...live, phase: 'switching', loaded: false, restored: true, reachable: false }))
      .toBe('dead');
  });

  it('обрыв связи в самом начале, пока журнал не прочитан, даёт загрузку', () => {
    expect(deriveState({ ...live, loaded: false, restored: false, reachable: false })).toBe('loading');
  });

  it('терминальные исходы читаются из result', () => {
    expect(deriveState({ ...live, result: { status: 'succeeded', ok: true } })).toBe('succeeded');
    expect(deriveState({ ...live, result: { status: 'rolled_back', ok: false } })).toBe('rolled_back');
    expect(deriveState({ ...live, result: { status: 'failed', ok: false } })).toBe('failed');
  });

  it('терминальная фаза без result тоже даёт итог', () => {
    expect(deriveState({ ...live, phase: 'succeeded' })).toBe('succeeded');
    expect(deriveState({ ...live, phase: 'rolled_back' })).toBe('rolled_back');
  });

  it('терминальная фаза в чужом регистре даёт итог, а не «идёт»', () => {
    // Агент однажды напишет 'Succeeded' без result — карточка обязана показать итог
    expect(deriveState({ ...live, phase: 'Succeeded' })).toBe('succeeded');
    expect(deriveState({ ...live, phase: 'ROLLED_BACK' })).toBe('rolled_back');
    expect(deriveState({ ...live, phase: 'Failed' })).toBe('failed');
  });

  it('терминальная фаза в чужом регистре сильнее обрыва связи', () => {
    // Иначе итог выкатки подменился бы «мёртвым окном» навсегда
    expect(deriveState({ ...live, phase: 'Succeeded', reachable: false })).toBe('succeeded');
  });

  it('готовый итог сильнее обрыва связи', () => {
    // Итог уже прочитан — падение сервера после этого не превращает успех в паузу
    expect(deriveState({ ...live, result: { status: 'succeeded', ok: true }, reachable: false }))
      .toBe('succeeded');
  });
});

describe('outcomeState', () => {
  it.each(['succeeded', 'ok', 'success', 'SUCCEEDED'])('успех: %s', (s) =>
    expect(outcomeState(s, false)).toBe('succeeded'));

  it.each(['rolled_back', 'rolledback', 'rolled-back'])('откат: %s', (s) =>
    expect(outcomeState(s, false)).toBe('rolled_back'));

  it.each(['failed', 'fail', 'error'])('провал: %s', (s) =>
    expect(outcomeState(s, true)).toBe('failed'));

  it('незнакомый статус решается флагом ok', () => {
    // Словарь агента может расшириться — показать всё равно надо что-то осмысленное
    expect(outcomeState('finished-somehow', true)).toBe('succeeded');
    expect(outcomeState('finished-somehow', false)).toBe('failed');
  });

  it('статуса нет — без ok это провал', () => {
    expect(outcomeState(null, undefined)).toBe('failed');
    expect(outcomeState(undefined, true)).toBe('succeeded');
  });
});

describe('runPhaseState', () => {
  it.each(['queued', 'building', 'switching', 'verifying'] as const)('фаза %s остаётся собой', (p) =>
    expect(runPhaseState(p)).toBe(p));

  it('незнакомая и пустая фаза — сборка', () => {
    expect(runPhaseState('sandbox-warmup')).toBe('building');
    expect(runPhaseState(null)).toBe('building');
    expect(runPhaseState(undefined)).toBe('building');
  });
});

describe('phaseGroup / isTerminalPhase', () => {
  it('группа = фаза для четырёх ходовых фаз', () => {
    expect(phaseGroup('switching')).toBe('switching');
    expect(phaseGroup('queued')).toBe('queued');
  });

  it('терминальная и незнакомая фаза сводятся к building', () => {
    // Верхние строки карточки должны отрисоваться при любом значении из журнала
    expect(phaseGroup('succeeded')).toBe('building');
    expect(phaseGroup('что-то новое')).toBe('building');
    expect(phaseGroup(null)).toBe('building');
  });

  it('терминальность считается по трём исходам', () => {
    expect(isTerminalPhase('succeeded')).toBe(true);
    expect(isTerminalPhase('rolled_back')).toBe(true);
    expect(isTerminalPhase('failed')).toBe(true);
    expect(isTerminalPhase('verifying')).toBe(false);
    expect(isTerminalPhase(null)).toBe(false);
  });

  it('регистр фазы не важен: журнал пишет внешний агент со своим форматом', () => {
    expect(isTerminalPhase('Succeeded')).toBe(true);
    expect(isTerminalPhase('ROLLED_BACK')).toBe(true);
    expect(isTerminalPhase('Failed')).toBe(true);
    expect(isTerminalPhase('Verifying')).toBe(false);
    expect(isTerminalPhase('BUILDING')).toBe(false);
  });
});

describe('buildPlan', () => {
  it('истории нет — прогноза нет (ни бара, ни ETA)', () => {
    expect(buildPlan(null)).toBeNull();
    expect(buildPlan(undefined)).toBeNull();
    expect(buildPlan([])).toBeNull();
  });

  it('неуспешные выкатки в прогноз не идут', () => {
    expect(buildPlan([record([step('frontend', 10_000)], 'failed')])).toBeNull();
    expect(buildPlan([record([step('frontend', 10_000)], 'rolled_back')])).toBeNull();
  });

  it('успешная запись без шагов не даёт прогноза', () => {
    expect(buildPlan([record(undefined), record([], 'succeeded')])).toBeNull();
  });

  it('одна выкатка — план из её шагов', () => {
    const plan = buildPlan([record([step('frontend', 10_000), step('swap', 3_000)])])!;
    expect(plan.steps).toEqual([{ name: 'frontend', ms: 10_000 }, { name: 'swap', ms: 3_000 }]);
    expect(plan.totalMs).toBe(13_000);
  });

  it('несколько выкаток сводятся медианой, порядок — по самой свежей', () => {
    const plan = buildPlan([
      record([step('frontend', 20_000), step('swap', 4_000)]),
      record([step('swap', 2_000), step('frontend', 10_000)]),
    ])!;
    expect(plan.steps.map(s => s.name)).toEqual(['frontend', 'swap']);
    expect(plan.steps.map(s => s.ms)).toEqual([15_000, 3_000]);
  });

  it('записи без шагов не ломают расчёт по остальным', () => {
    const plan = buildPlan([record(undefined), record([step('frontend', 10_000)])])!;
    expect(plan.steps).toEqual([{ name: 'frontend', ms: 10_000 }]);
  });

  it('кривые ms не портят прогноз', () => {
    // sane отсекает мусор: в выборку идёт только вменяемое значение
    const plan = buildPlan([
      record([step('frontend', 10_000)]),
      record([step('frontend', -1)]),
      record([step('frontend', Number.NaN)]),
    ])!;
    expect(plan.steps[0].ms).toBe(10_000);
  });

  it('одна аномально долгая выкатка не тянет прогноз вверх', () => {
    // Медиана, а не среднее: пересборка образа песочницы бывает раз в месяц,
    // а среднее (27 с) врало бы человеку про каждую выкатку
    const plan = buildPlan([
      record([step('frontend', 10_000)]),
      record([step('frontend', 12_000)]),
      record([step('frontend', 60_000)]),
    ])!;
    expect(plan.steps[0].ms).toBe(12_000);
  });

  it('чётная выборка — среднее двух центральных', () => {
    const plan = buildPlan([
      record([step('frontend', 10_000)]),
      record([step('frontend', 12_000)]),
      record([step('frontend', 20_000)]),
      record([step('frontend', 90_000)]),
    ])!;
    expect(plan.steps[0].ms).toBe(16_000);
  });

  it('порядок записей на медиану не влияет', () => {
    const ms = (recs: DeployJournalRecord[]) => buildPlan(recs)!.steps[0].ms;
    const a = record([step('frontend', 60_000)]);
    const b = record([step('frontend', 10_000)]);
    const c = record([step('frontend', 12_000)]);
    // порядок задаёт только имена шагов, а вес шага — выборка целиком
    expect(ms([a, b, c])).toBe(12_000);
    expect(ms([b, c, a])).toBe(12_000);
  });

  it('все длительности нулевые — прогноза нет, а не бар на ноль секунд', () => {
    expect(buildPlan([record([step('frontend', 0), step('swap', 0)])])).toBeNull();
  });

  it('на прогноз берутся только пять последних выкаток', () => {
    const fresh = Array.from({ length: 5 }, () => record([step('frontend', 10_000)]));
    const ancient = record([step('frontend', 600_000)]);
    expect(buildPlan([...fresh, ancient])!.steps[0].ms).toBe(10_000);
  });

  it('мёртвое окно считается от шага stop до конца переключения', () => {
    const plan = buildPlan([record([
      step('frontend', 10_000), step('data-backup', 5_000),
      step('stop', 2_000), step('swap', 3_000), step('start', 4_000),
      step('health', 1_000),
    ])])!;
    // data-backup идёт ДО stop — связь ещё есть, в окно он не входит
    expect(plan.deadMs).toBe(9_000);
  });

  it('шага stop нет (агент переименовал) — окном считается вся фаза переключения', () => {
    const plan = buildPlan([record([
      step('frontend', 10_000), step('data-backup', 5_000), step('swap', 3_000),
    ])])!;
    expect(plan.deadMs).toBe(8_000);
  });
});

describe('buildStepRows', () => {
  it('незнакомый шаг не исчезает: сырое имя и группа предыдущего известного', () => {
    // Агент выкатки обновляется отдельно от сервера — новый шаг обязан остаться видимым
    const rows = buildStepRows(record([
      step('frontend', 1_000, 'ok'),
      step('publish-modules', 2_000, 'ok'),
      step('health', 500, 'ok'),
    ], 'succeeded'), null, false);
    const unknown = rows.find(r => r.name === 'publish-modules')!;
    expect(unknown).toBeDefined();
    expect(unknown.label).toBeNull();
    expect(unknown.group).toBe('building');
    expect(unknown.status).toBe('done');
  });

  it('незнакомый шаг в переключении наследует switching, а не откатывается к building', () => {
    const rows = buildStepRows(record([
      step('frontend', 1_000, 'ok'), step('stop', 500, 'ok'), step('warm-cache', 700, 'ok'),
    ], 'succeeded'), null, false);
    expect(rows.find(r => r.name === 'warm-cache')!.group).toBe('switching');
  });

  it('словарь статусов агента раскладывается на наши', () => {
    const rows = buildStepRows(record([
      step('frontend', 1_000, 'ok'), step('mcp', 500, 'running'), step('swap', 200, 'failed'),
    ], 'failed'), null, true);
    expect(rows.map(r => r.status)).toEqual(['done', 'run', 'fail']);
    // У идущего шага длительности ещё нет — её нельзя показывать
    expect(rows[1].ms).toBeNull();
  });

  it('упавший шаг помечается провалом и держит свою длительность', () => {
    const rows = buildStepRows(record([step('swap', 4_000, 'fail')], 'failed'), null, false);
    expect(rows[0]).toEqual({ name: 'swap', label: 'подменяю сборку', group: 'switching', status: 'fail', ms: 4_000 });
  });

  it('последний шаг незавершённой выкатки без статуса считается идущим', () => {
    const rows = buildStepRows(record([step('frontend', 1_000), step('mcp', 0)], 'building'), null, true);
    expect(rows.map(r => r.status)).toEqual(['done', 'run']);
  });

  it('в завершённой выкатке шаги без статуса — пройденные', () => {
    const rows = buildStepRows(record([step('frontend', 1_000), step('health', 900)]), null, false);
    expect(rows.map(r => r.status)).toEqual(['done', 'done']);
  });

  it('шаги мёртвого окна ждут, а не считаются пройденными', () => {
    // Связь оборвалась на stop: то, что агент делает дальше, до нас не доехало —
    // выдавать это за сделанное нельзя
    const plan = buildPlan([record([
      step('frontend', 10_000), step('stop', 1_000), step('swap', 3_000), step('start', 4_000),
    ])])!;
    const rows = buildStepRows(record([step('frontend', 9_000, 'ok'), step('stop', 0)], 'switching'), plan, true);
    expect(rows.find(r => r.name === 'stop')!.status).toBe('run');
    expect(rows.filter(r => ['swap', 'start'].includes(r.name)).map(r => r.status)).toEqual(['wait', 'wait']);
    expect(rows.some(r => r.status === 'done' && r.group === 'switching')).toBe(false);
  });

  it('ожидаемые шаги из плана добавляются в хвост и не дублируют записанные', () => {
    const plan = buildPlan([record([step('frontend', 10_000), step('health', 1_000)])])!;
    const rows = buildStepRows(record([step('frontend', 9_000, 'ok')], 'building'), plan, true);
    expect(rows.map(r => r.name)).toEqual(['frontend', 'health']);
    expect(rows[1].status).toBe('wait');
    expect(rows[1].ms).toBeNull();
  });

  it('ни записи, ни плана — пустой список (карточке есть что показать пустым состоянием)', () => {
    expect(buildStepRows(null, null, true)).toEqual([]);
  });

  it('записи ещё нет, а план есть — все шаги в ожидании', () => {
    const plan = buildPlan([record([step('frontend', 10_000), step('stop', 1_000)])])!;
    const rows = buildStepRows(null, plan, true);
    expect(rows.map(r => r.status)).toEqual(['wait', 'wait']);
    expect(rows.map(r => r.group)).toEqual(['building', 'switching']);
  });
});

describe('groupMs / msBeforeGroup / failedStep', () => {
  const rows: StepRow[] = [
    { name: 'frontend', label: 'фронтенд', group: 'building', status: 'done', ms: 10_000 },
    { name: 'mcp', label: 'MCP-серверы', group: 'building', status: 'done', ms: 2_000 },
    { name: 'stop', label: 'останавливаю прод', group: 'switching', status: 'fail', ms: 3_000 },
    { name: 'swap', label: 'подменяю сборку', group: 'switching', status: 'run', ms: null },
    { name: 'health', label: 'проверка здоровья', group: 'verifying', status: 'wait', ms: null },
  ];

  it('groupMs суммирует только состоявшиеся шаги группы', () => {
    expect(groupMs(rows, 'building')).toBe(12_000);
    expect(groupMs(rows, 'switching')).toBe(3_000); // упавший считается, идущий — нет
    expect(groupMs(rows, 'verifying')).toBe(0);
  });

  it('msBeforeGroup складывает всё, что было до группы', () => {
    expect(msBeforeGroup(rows, 'building')).toBe(0);
    expect(msBeforeGroup(rows, 'switching')).toBe(12_000);
    expect(msBeforeGroup(rows, 'verifying')).toBe(15_000);
  });

  it('failedStep берёт первый упавший', () => {
    expect(failedStep(rows)!.name).toBe('stop');
  });

  it('падений нет — последний состоявшийся шаг', () => {
    expect(failedStep(rows.filter(r => r.status !== 'fail'))!.name).toBe('swap');
  });

  it('одни ожидания — некого винить', () => {
    expect(failedStep(rows.filter(r => r.status === 'wait'))).toBeNull();
    expect(failedStep([])).toBeNull();
  });
});

describe('switchingHint', () => {
  it('перечисляет шаги переключения из плана', () => {
    const plan = buildPlan([record([
      step('frontend', 10_000), step('stop', 1_000), step('swap', 3_000),
    ])])!;
    expect(switchingHint(plan)).toBe('останавливаю прод · подменяю сборку');
  });

  it('плана нет — подсказка из карты известных шагов, а не пустая строка', () => {
    const hint = switchingHint(null);
    expect(hint).toContain('останавливаю прод');
    expect(hint).toContain('запускаю прод');
  });
});

describe('форматы времени', () => {
  it('durLabel: секунды и минуты', () => {
    expect(durLabel(0)).toBe('0 с');
    expect(durLabel(41_000)).toBe('41 с');
    expect(durLabel(59_400)).toBe('59 с');
    expect(durLabel(120_000)).toBe('2 мин');
    expect(durLabel(128_000)).toBe('2 мин 8 с');
  });

  it('durLabel: невменяемое значение — пустая строка, а не «-3 с»', () => {
    expect(durLabel(-1)).toBe('');
    expect(durLabel(null)).toBe('');
    expect(durLabel(undefined)).toBe('');
    expect(durLabel(25 * 60 * 60 * 1000)).toBe('');
  });

  it('clockLabel: секундомер с ведущим нулём', () => {
    expect(clockLabel(0)).toBe('0:00');
    expect(clockLabel(7_000)).toBe('0:07');
    expect(clockLabel(187_000)).toBe('3:07');
    expect(clockLabel(3_599_000)).toBe('59:59');
  });

  it('clockLabel: от часа и выше — часы:минуты:секунды', () => {
    // Секундомер тикает по локальному таймеру: при зависшем агенте «93:17»
    // читалось бы как ошибка вёрстки, а не как полтора часа
    expect(clockLabel(3_600_000)).toBe('1:00:00');
    expect(clockLabel(3_600_000 + 187_000)).toBe('1:03:07');
    expect(clockLabel(3_600_000 + 3_599_000)).toBe('1:59:59');
    expect(clockLabel(2 * 3_600_000)).toBe('2:00:00');
    expect(clockLabel(10 * 3_600_000 + 9_000)).toBe('10:00:09');
  });

  it('clockLabel: отрицательное время (часы клиента ушли вперёд) — ноль', () => {
    expect(clockLabel(-5_000)).toBe('0:00');
  });

  it('etaLabel: до 45 секунд — «меньше минуты»', () => {
    expect(etaLabel(0)).toBe('меньше минуты');
    expect(etaLabel(45_000)).toBe('меньше минуты');
    expect(etaLabel(-1_000)).toBe('меньше минуты');
    expect(etaLabel(46_000)).toBe('≈ 1 мин');
    expect(etaLabel(180_000)).toBe('≈ 3 мин');
  });

  it('sane пропускает вменяемое и режет мусор', () => {
    expect(sane(0)).toBe(0);
    expect(sane(1_234)).toBe(1_234);
    expect(sane(24 * 60 * 60 * 1000)).toBe(24 * 60 * 60 * 1000);
    expect(sane(24 * 60 * 60 * 1000 + 1)).toBeNull();
    expect(sane(-1)).toBeNull();
    expect(sane(Number.NaN)).toBeNull();
    expect(sane(Number.POSITIVE_INFINITY)).toBeNull();
    expect(sane(null)).toBeNull();
    expect(sane(undefined)).toBeNull();
  });

  it('parseTime читает ISO и не верит мусору', () => {
    expect(parseTime('2026-08-21T10:00:00Z')).toBe(Date.parse('2026-08-21T10:00:00Z'));
    expect(parseTime('вчера вечером')).toBeNull();
    expect(parseTime('')).toBeNull();
    expect(parseTime(null)).toBeNull();
    expect(parseTime(undefined)).toBeNull();
  });
});

describe('isDeployStart / parseDeployId', () => {
  it('инструмент опознаётся по суффиксу и без регистра', () => {
    // Префикс зависит от имени, под которым MCP-сервер подключён к ходу
    expect(isDeployStart('mcp__wsp__deploy_start')).toBe(true);
    expect(isDeployStart('MCP__WSP__Deploy_Start')).toBe(true);
    expect(isDeployStart('mcp__wsp__deploy_status')).toBe(false);
    expect(isDeployStart('deploy_start_now')).toBe(false);
  });

  it('deployId достаётся из JSON-ответа', () => {
    expect(parseDeployId('{"deployId":"20260821-101500","queued":true}')).toBe('20260821-101500');
  });

  it('отказ приходит текстом ошибки — зацепиться не за что', () => {
    expect(parseDeployId('Выкатка уже идёт (409)')).toBeNull();
    expect(parseDeployId('{"ok":false,"error":"deploy disabled"}')).toBeNull();
    expect(parseDeployId('{"deployId":""}')).toBeNull();
  });

  it('ответа нет вовсе — null', () => {
    expect(parseDeployId(undefined)).toBeNull();
    expect(parseDeployId('')).toBeNull();
  });

  it('id вытаскивается и из текста вокруг битого JSON', () => {
    // CLI иногда дописывает свою обвязку — терять заявку из-за этого нельзя
    expect(parseDeployId('Готово: {"deployId": "abc-1"} (см. журнал)')).toBe('abc-1');
  });
});

// === Метки хранилищ ===
// Ключи — часть контракта между вкладками и перезагрузками, поэтому проверяются буквально.
const WATCH_KEY = 'cc_deploy_watch';
const INITIATOR_KEY = 'cc_deploy_initiator';

function fakeStorage() {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => (m.has(k) ? m.get(k)! : null),
    setItem: (k: string, v: string) => { m.set(k, String(v)); },
    removeItem: (k: string) => { m.delete(k); },
    clear: () => { m.clear(); },
    key: (i: number) => Array.from(m.keys())[i] ?? null,
    get length() { return m.size; },
  };
}

describe('метка мёртвого окна (localStorage)', () => {
  beforeEach(() => {
    // Окружение тестов — node, DOM-хранилищ там нет
    vi.stubGlobal('localStorage', fakeStorage());
  });
  afterEach(() => { vi.unstubAllGlobals(); });

  it('записанная метка читается обратно и знает своё время', () => {
    writeWatch({ deployId: 'd1', startedAt: '2026-08-21T10:00:00Z', phase: 'switching' });
    const w = readWatch()!;
    expect(w.deployId).toBe('d1');
    expect(w.phase).toBe('switching');
    expect(w.startedAt).toBe('2026-08-21T10:00:00Z');
    expect(typeof w.ts).toBe('number');
  });

  it('метки нет — null', () => {
    expect(readWatch()).toBeNull();
  });

  it('битая или неполная метка не восстанавливается', () => {
    localStorage.setItem(WATCH_KEY, 'не json');
    expect(readWatch()).toBeNull();
    localStorage.setItem(WATCH_KEY, JSON.stringify({ phase: 'switching', ts: Date.now() }));
    expect(readWatch()).toBeNull();
    localStorage.setItem(WATCH_KEY, JSON.stringify({ deployId: 'd1', phase: 'switching' }));
    expect(readWatch()).toBeNull();
  });

  it('метка старше 30 минут протухает и убирается', () => {
    // Вкладку могли закрыть на сутки — старая метка показала бы «сервер перезапускается» зря
    localStorage.setItem(WATCH_KEY, JSON.stringify({
      deployId: 'd1', startedAt: null, phase: 'switching', ts: Date.now() - 31 * 60_000,
    }));
    expect(readWatch()).toBeNull();
    expect(localStorage.getItem(WATCH_KEY)).toBeNull();
  });

  it('метка в пределах TTL живёт', () => {
    localStorage.setItem(WATCH_KEY, JSON.stringify({
      deployId: 'd1', startedAt: null, phase: 'switching', ts: Date.now() - 29 * 60_000,
    }));
    expect(readWatch()!.deployId).toBe('d1');
  });

  it('clearWatch без аргумента убирает метку', () => {
    writeWatch({ deployId: 'd1', startedAt: null, phase: 'switching' });
    clearWatch();
    expect(readWatch()).toBeNull();
  });

  it('clearWatch с чужим deployId не трогает чужую метку', () => {
    // Карточка старой выкатки не должна гасить наблюдение за новой
    writeWatch({ deployId: 'd2', startedAt: null, phase: 'switching' });
    clearWatch('d1');
    expect(readWatch()!.deployId).toBe('d2');
    clearWatch('d2');
    expect(readWatch()).toBeNull();
  });

  it('хранилище недоступно (приватный режим) — работаем молча, без исключений', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
      removeItem: () => { throw new Error('denied'); },
    });
    expect(() => writeWatch({ deployId: 'd1', startedAt: null, phase: 'switching' })).not.toThrow();
    expect(readWatch()).toBeNull();
    expect(() => clearWatch('d1')).not.toThrow();
  });
});

describe('метка инициатора (sessionStorage)', () => {
  beforeEach(() => { vi.stubGlobal('sessionStorage', fakeStorage()); });
  afterEach(() => { vi.unstubAllGlobals(); });

  it('инициатор помнится в пределах вкладки', () => {
    markInitiator('d1');
    expect(sessionStorage.getItem(INITIATOR_KEY)).toBe('d1');
    expect(isInitiator('d1')).toBe(true);
  });

  it('чужая выкатка — не наша: сама переезжать вкладка не должна', () => {
    markInitiator('d1');
    expect(isInitiator('d2')).toBe(false);
  });

  it('метки нет — не инициатор', () => {
    expect(isInitiator('d1')).toBe(false);
  });

  it('clearInitiator убирает только свою метку', () => {
    markInitiator('d1');
    clearInitiator('d2');
    expect(isInitiator('d1')).toBe(true);
    clearInitiator('d1');
    expect(isInitiator('d1')).toBe(false);
  });

  it('хранилище недоступно — false вместо падения', () => {
    vi.stubGlobal('sessionStorage', {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
      removeItem: () => { throw new Error('denied'); },
    });
    expect(() => markInitiator('d1')).not.toThrow();
    expect(isInitiator('d1')).toBe(false);
    expect(() => clearInitiator('d1')).not.toThrow();
  });
});

describe('DeployPlan', () => {
  it('план несёт шаги, сумму и мёртвое окно', () => {
    const plan: DeployPlan | null = buildPlan([record([step('frontend', 10_000), step('stop', 2_000)])]);
    expect(plan).toMatchObject({ totalMs: 12_000, deadMs: 2_000 });
    expect(plan!.steps).toHaveLength(2);
  });
});
