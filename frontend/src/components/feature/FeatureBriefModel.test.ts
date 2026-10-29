// Перевод ключей brief в слова (волна 6, шаг F2): строки — по макету
// docs/mockups/feature-summary-1.md (состояния §2) и разрезу п. 4 для kinds вне макета.
// Чистые функции без DOM и времени «сейчас»: nowMs и даты фиксированы, моменты
// построены локальными компонентами Date — TZ-независимо (CI Linux = dev Windows).

import { describe, it, expect } from 'vitest';
import {
  agoText, briefDoneText, briefElapsedFact, briefEstimateText, briefNextText, briefNowText, briefReadinessText, briefSubText, briefUpdatedText, genitiveName,
  voiceRoleText, voiceStaleLabel, voiceWhenText,
} from './FeatureBriefModel';
import type { FeatureBrief, FeatureBriefKind } from '../../lib/featurePage';

// «Сейчас» по часам макета: 25.10.2026 14:10 (локальное).
const NOW = new Date(2026, 9, 25, 14, 10).getTime();
// ISO от локальных компонентов (форматтеры читают локальные геттеры — совпадёт в любой TZ).
const at = (day: number, hh: number, mm = 0, ss = 0): string => new Date(2026, 9, day, hh, mm, ss).toISOString();

const NAMES: Record<string, string> = { 'p-gleb': 'Глеб', 'p-vera': 'Вера', 'p-kira': 'Кира', 'p-sofya': 'Софья' };
const nameOf = (id: string): string | null => NAMES[id] ?? null;

function brief(over: Partial<FeatureBrief> = {}): FeatureBrief {
  return {
    kind: 'running',
    now: null,
    reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [],
    elapsedMinutes: null,
    estimate: null,
    queue: null,
    note: null,
    computedAt: at(25, 14, 8),
    ...over,
  };
}

// ── 1. «Сейчас»: для каждого из 8 kind — точная строка макета/разреза ────────────────

describe('Сейчас: строки восьми kind', () => {
  it('running — «Ревью шага 3 (бэк) — у Глеба» (макет run)', () => {
    const b = brief({
      kind: 'running',
      now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) },
    });
    expect(briefNowText(b, { nameOf })).toBe('Ревью шага 3 (бэк) — у Глеба');
  });

  it('running без имени — часть «у …» опускается, строка целая', () => {
    const b = brief({
      kind: 'running',
      now: { stage: 'review', taskId: 't-rev', personaId: 'p-unknown', stepRef: '3', lane: 'back', since: at(25, 14, 5) },
    });
    expect(briefNowText(b, { nameOf })).toBe('Ревью шага 3 (бэк)');
  });

  it('waiting_you с тревогой «мерж» — «Ждёт вашего согласия на мерж в master» (макет wait)', () => {
    const b = brief({ kind: 'waiting_you', reason: 'мерж' });
    expect(briefNowText(b, { nameOf })).toBe('Ждёт вашего согласия на мерж в master');
  });

  it('waiting_you со своей причиной — текст причины', () => {
    const b = brief({ kind: 'waiting_you', reason: 'нужен твой ответ по макету' });
    expect(briefNowText(b, { nameOf })).toBe('Ждёт вашего ответа: нужен твой ответ по макету');
  });

  // Дефект c79050aa: у kind 'error' причина — машинный код бэка (ExecutorStopClassifier →
  // Attention.Reason, FeatureProjector.cs:143), а не текст: старый фикстур «сохранение
  // журнала упало» описывал поведение, которого бэк не даёт. Перевод — общая таблица.
  it('error с известным кодом — человеческий текст общей таблицы (defect c79050aa)', () => {
    const b = brief({ kind: 'error', reason: 'auth_failed' });
    expect(briefNowText(b, { nameOf })).toBe('Исполнитель встал: Не удалось авторизоваться у провайдера модели');
  });

  // R-F17 (п.1 R-F16): «Стоп» человека — в общей таблице: строка «Сейчас» называет причину
  // теми же словами, что плашка карточки и блок «Остановлена» (обе берут тот же текст через
  // executorStopVisual). Мутация: убрать запись interrupted_by_user из таблицы tasks.ts —
  // тест краснеет (строка теряет причину).
  it('error с interrupted_by_user — причина названа как на соседних поверхностях (R-F17 п.1)', () => {
    expect(briefNowText(brief({ kind: 'error', reason: 'interrupted_by_user' }), { nameOf }))
      .toBe('Исполнитель встал: Исполнение прервано');
  });

  it('error с кодом вне таблицы — нейтральная фраза без кода (defect c79050aa)', () => {
    expect(briefNowText(brief({ kind: 'error', reason: 'совсем_новый_код' }), { nameOf })).toBe('Исполнитель встал');
  });

  it('error — машинного кода (латиница с подчёркиванием) нет ни в одной строке (defect c79050aa)', () => {
    for (const r of ['auth_failed', 'interrupted_by_user', 'subagent_truncated', 'tool_call_leaked', 'feature_held_expired', 'device_wait_expired']) {
      const t = briefNowText(brief({ kind: 'error', reason: r }), { nameOf }) ?? '';
      expect(t, r).not.toMatch(/[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+/);
    }
  });

  // R-F17 (п.5): нет причины и неизвестный код — одно состояние, одна формулировка.
  // Мутация: вернуть в модель «Исполнитель остановлен» для неизвестного кода — тест краснеет.
  it('error без причины — та же фраза, что с неизвестным кодом, без двоеточия (R-F17 п.5)', () => {
    expect(briefNowText(brief({ kind: 'error' }), { nameOf })).toBe('Исполнитель встал');
    expect(briefNowText(brief({ kind: 'error' }), { nameOf }))
      .toBe(briefNowText(brief({ kind: 'error', reason: 'совсем_новый_код' }), { nameOf }));
  });

  it('paused — «На паузе — поставили вы в 13:40» (макет hold)', () => {
    const b = brief({
      kind: 'paused', reason: 'manual',
      now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) },
    });
    expect(briefNowText(b, { nameOf })).toBe('На паузе — поставили вы в 13:40');
  });

  it('paused без времени — строка без времени', () => {
    const b = brief({ kind: 'paused', reason: 'manual' });
    expect(briefNowText(b, { nameOf })).toBe('На паузе — поставили вы');
  });

  it('queued без очереди — «Ждёт своей очереди» без номера (решение плана)', () => {
    const b = brief({ kind: 'queued' });
    expect(briefNowText(b, { nameOf })).toBe('Ждёт своей очереди');
  });

  it('queued с позицией — числа контракта', () => {
    const b = brief({ kind: 'queued', queue: { position: 2, total: 3 } });
    expect(briefNowText(b, { nameOf })).toBe('В очереди: 2 из 3');
  });

  it('draft — «Готовится: разрез/план» (разрез п. 4)', () => {
    expect(briefNowText(brief({ kind: 'draft' }), { nameOf })).toBe('Готовится: разрез/план');
  });

  it('idle — «Ничего не идёт» (разрез п. 4)', () => {
    expect(briefNowText(brief({ kind: 'idle' }), { nameOf })).toBe('Ничего не идёт');
  });

  it('closed с датой мержа — «Смержена в master 24.10 в 18:05» (макет closed)', () => {
    expect(briefNowText(brief({ kind: 'closed' }), { nameOf, mergedAt: at(24, 18, 5) }))
      .toBe('Смержена в master 24.10 в 18:05');
  });

  it('closed без даты — «Закрыта» (разрез п. 4)', () => {
    expect(briefNowText(brief({ kind: 'closed' }), { nameOf })).toBe('Закрыта');
  });

  it('running без now — строки нет (макет: поля нет — строку не рисуем)', () => {
    expect(briefNowText(brief({ kind: 'running' }), { nameOf })).toBeNull();
  });

  it('running с пустым stage — строка без ведущего пробела (R-F8)', () => {
    const b = brief({
      kind: 'running',
      now: { stage: '', taskId: 't-x', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) },
    });
    const t = briefNowText(b, { nameOf });
    expect(t).toBe('шага 3 (бэк) — у Глеба');
    expect(t, 'ведущего пробела нет').not.toMatch(/^\s/);
  });

  it('running с пустым stage и без остальных частей — строки нет (null, не пустая строка)', () => {
    const b = brief({
      kind: 'running',
      now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 5) },
    });
    expect(briefNowText(b, { nameOf })).toBeNull();
  });
});

// ── 1a. Подстрока «Сейчас» (слова модели, перенесены из блока, R-F5) ────────────────

describe('Подстрока «Сейчас»', () => {
  it('running — «с ЧЧ:ММ · N мин» из now.since (макет «с какого времени · сколько длится»)', () => {
    const b = brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 58) } });
    expect(briefSubText(b, NOW)).toBe('с 13:58 · 12 мин');
  });

  it('waiting_you — «ждёт N · без вас фича стоит»', () => {
    const b = brief({ kind: 'waiting_you', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) } });
    expect(briefSubText(b, NOW)).toBe('ждёт 30 мин · без вас фича стоит');
  });

  it('paused — «на паузе N · исполнители не запускаются»', () => {
    const b = brief({ kind: 'paused', reason: 'manual', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) } });
    expect(briefSubText(b, NOW)).toBe('на паузе 30 мин · исполнители не запускаются');
  });

  it('нулевая длительность — «0 мин» нигде не печатается: остаётся статическая часть фразы (F10 минор, та же болезнь, что «идёт 0 мин»)', () => {
    // since == «сейчас» (0 мин) и меньше округляемой минуты (20 с) — числа нет ни в одной строке.
    const run = brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 10) } });
    expect(briefSubText(run, NOW)).toBe('с 14:10');
    const wait = brief({ kind: 'waiting_you', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 10) } });
    expect(briefSubText(wait, NOW)).toBe('без вас фича стоит');
    const hold = brief({ kind: 'paused', reason: 'manual', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 10) } });
    expect(briefSubText(hold, NOW)).toBe('исполнители не запускаются');
    const short = brief({ kind: 'waiting_you', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 9, 40) } });
    expect(briefSubText(short, NOW)).toBe('без вас фича стоит');
  });

  it('closed — «фича закрыта»', () => {
    expect(briefSubText(brief({ kind: 'closed' }), NOW)).toBe('фича закрыта');
  });

  it('нет since (running/waiting_you) и kinds без подстроки — null', () => {
    expect(briefSubText(brief({ kind: 'running' }), NOW)).toBeNull();
    expect(briefSubText(brief({ kind: 'waiting_you' }), NOW)).toBeNull();
    expect(briefSubText(brief({ kind: 'queued' }), NOW)).toBeNull();
  });
});

// ── 1b. «Сделано» (слова модели, перенесены из блока, R-F5) ─────────────────────────

describe('Сделано', () => {
  const textOf = (b: FeatureBrief) => briefDoneText(b).map(p => p.text).join('');

  it('«2 из 5 шагов приняты · 1 виток правок»: числа — mono-части, связный текст — обычные', () => {
    const parts = briefDoneText(brief({ done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' } }));
    expect(parts.map(p => p.text).join('')).toBe('2 из 5 шагов приняты · 1 виток правок');
    expect(parts.map(p => p.mono)).toEqual([true, false, true, false]);
  });

  it('loops = 0 — хвоста про витки нет', () => {
    expect(textOf(brief({ done: { stepsAccepted: 5, stepsTotal: 5, loops: 0, planState: 'cut' } }))).toBe('5 из 5 шагов приняты');
  });

  it('склонение разряда: 1 виток / 2 витка / 5 витков / 11 витков', () => {
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 1, planState: 'cut' } }))).toContain('1 виток правок');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 2, planState: 'cut' } }))).toContain('2 витка правок');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 5, planState: 'cut' } }))).toContain('5 витков правок');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 11, planState: 'cut' } }))).toContain('11 витков правок');
  });

  it('queued с нарезанным планом — «План готов · 0 из 4 шагов» (макет queue, R-F8)', () => {
    const b = brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } });
    const parts = briefDoneText(b);
    expect(parts.map(p => p.text).join('')).toBe('План готов · 0 из 4 шагов');
    // «План готов» и связка — обычный текст; счётчик — mono (макет: <b>0 из 4</b>).
    expect(parts.map(p => p.mono)).toEqual([false, false, true, false]);
  });

  it('queued с незавершённым планом — обычный счётчик, «План готов» не выдумывается (R-F8)', () => {
    for (const planState of ['pending', 'partial'] as const) {
      const b = brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState } });
      const t = textOf(b);
      expect(t, planState).not.toContain('План готов');
      expect(t, planState).toBe('0 из 4 шагов приняты');
    }
  });
});

// ── 2. «Дальше» из next[] ───────────────────────────────────────────────────────────

describe('Дальше: строка из next', () => {
  it('running — «После ревью — проверка (Вера), затем финальное ревью и мерж» (макет run)', () => {
    const b = brief({
      kind: 'running',
      now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) },
      next: [{ stage: 'qa', personaId: 'p-vera' }, { stage: 'final', personaId: null }, { stage: 'merge', personaId: null }],
    });
    expect(briefNextText(b, nameOf)).toBe('После ревью — проверка (Вера), затем финальное ревью и мерж');
  });

  it('waiting_you с мержем — «После вашего «да» — мерж в master» (макет wait)', () => {
    const b = brief({ kind: 'waiting_you', reason: 'мерж', next: [{ stage: 'merge', personaId: null }] });
    expect(briefNextText(b, nameOf)).toBe('После вашего «да» — мерж в master');
  });

  it('paused — «После снятия паузы — производство (Кира)»', () => {
    const b = brief({ kind: 'paused', reason: 'manual', next: [{ stage: 'prod', personaId: 'p-kira' }] });
    expect(briefNextText(b, nameOf)).toBe('После снятия паузы — производство (Кира)');
  });

  it('closed — «Ничего — фича закрыта» (макет closed)', () => {
    expect(briefNextText(brief({ kind: 'closed' }), nameOf)).toBe('Ничего — фича закрыта');
  });

  it('next пуст — строки нет', () => {
    expect(briefNextText(brief({ kind: 'running', next: [] }), nameOf)).toBeNull();
  });

  it('waiting_you с причиной не «мерж» и пустым next — строки нет, мерж не выдумывается (R-F2)', () => {
    const b = brief({ kind: 'waiting_you', reason: 'нужен твой ответ по макету', next: [] });
    expect(briefNextText(b, nameOf)).toBeNull();
  });

  it('waiting_you с причиной не «мерж» и следующим шагом — общий префикс без слова «мерж»', () => {
    const b = brief({ kind: 'waiting_you', reason: 'нужен твой ответ по макету', next: [{ stage: 'design', personaId: null }] });
    expect(briefNextText(b, nameOf)).toBe('После вашего «да» — дизайн');
  });

  // Дефект 16080f64: стадия в префиксе «После …» стояла в именительном («После производство
  // — ревью»). Каждая именная стадия (ключи = StageKey) обязана дать родительный падеж.
  it('defect 16080f64: префикс склоняет активную стадию — каждая именная стадия', () => {
    const cases: Array<[string, string]> = [
      ['cut', 'После разреза — мерж'],
      ['design', 'После дизайна — мерж'],
      ['plan', 'После плана — мерж'],
      ['prod', 'После производства — мерж'],
      ['review', 'После ревью — мерж'], // «ревью» — средний род, не склоняется
      ['qa', 'После проверки — мерж'],
      ['final', 'После финального ревью — мерж'],
      ['merge', 'После мержа — мерж'],
    ];
    for (const [stage, expected] of cases) {
      const b = brief({
        kind: 'running',
        now: { stage, taskId: 't-x', personaId: null, stepRef: null, lane: null, since: at(25, 14, 5) },
        next: [{ stage: 'merge', personaId: null }],
      });
      expect(briefNextText(b, nameOf), stage).toBe(expected);
    }
  });

  it('defect 16080f64: неизвестный ключ стадии не склоняется — ключ как есть (форму не выдумываем)', () => {
    const b = brief({
      kind: 'running',
      now: { stage: 'staging', taskId: 't-x', personaId: null, stepRef: null, lane: null, since: at(25, 14, 5) },
      next: [{ stage: 'merge', personaId: null }],
    });
    expect(briefNextText(b, nameOf)).toBe('После staging — мерж');
  });
});

// ── 3. «Срок» (правило двух оценок, макет feature-summary-2) ──────────────────────────

const HIST = { remainingMinutes: 150, p25: 110, p75: 220, source: 'history' as const, sampleSize: 14 };
const OPTS = { nameOf, nowMs: NOW };

function noteWith(est: Partial<NonNullable<FeatureBrief['note']>['estimate'] & object> | null, over: Partial<NonNullable<FeatureBrief['note']>> = {}): FeatureBrief['note'] {
  return {
    text: 'голос', at: at(25, 13, 56), byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'prod', stale: false,
    estimate: est === null ? null : {
      done: 4, total: 9, percent: 45, inProgress: 0, remaining: ['проверка · Вера', 'финал'], etaMinutes: 160,
      why: null, agreement: 'within', etaEndsAt: at(25, 16, 50), ...est,
    },
    ...over,
  };
}

describe('Срок', () => {
  it('свежая оценка внутри разброса — её число, «к 16:50» от etaEndsAt, источник и «сходится»', () => {
    const v = briefEstimateText(brief({ estimate: HIST, note: noteWith({}) }), OPTS);
    expect(v.header).toBe('Срок');
    expect(v.main).toBe('≈ 2 ч 40 мин');
    expect(v.tail).toBe('· к 16:50');
    expect(v.source).toBe('по оценке Софьи · 14 мин назад');
    expect(v.alt).toBe('по истории ≈ 2 ч 30 мин (1 ч 50 мин – 3 ч 40 мин)');
    expect(v.verdict).toEqual({ text: 'сходится', tone: 'ok' });
    expect(v.why).toBeNull();
  });

  it('свежая вне разброса — метка «дольше» / «быстрее, чем по истории» и «почему» оркестратора', () => {
    const above = briefEstimateText(brief({ estimate: HIST, note: noteWith({ etaMinutes: 360, agreement: 'above', why: 'добавился шаг 4б' }) }), OPTS);
    expect(above.verdict).toEqual({ text: 'дольше, чем по истории', tone: 'warn' });
    expect(above.why).toBe('добавился шаг 4б');
    const below = briefEstimateText(brief({ estimate: HIST, note: noteWith({ etaMinutes: 60, agreement: 'below', why: 'всё параллельно' }) }), OPTS);
    expect(below.verdict).toEqual({ text: 'быстрее, чем по истории', tone: 'warn' });
  });

  it('«почему» при «сходится» не показывается; agreement = null — строки истории нет', () => {
    expect(briefEstimateText(brief({ estimate: HIST, note: noteWith({ why: 'лишнее' }) }), OPTS).why).toBeNull();
    const v = briefEstimateText(brief({ estimate: HIST, note: noteWith({ agreement: null }) }), OPTS);
    expect(v.alt).toBeNull();
    expect(v.verdict).toBeNull();
  });

  it('устаревшая оценка — главное число по истории с временем от computedAt, оценка оркестратора строкой с меткой', () => {
    const v = briefEstimateText(brief({ estimate: HIST, note: noteWith({}, { stale: true, at: at(25, 12, 10) }) }), OPTS);
    expect(v.main).toBe('≈ 2 ч 30 мин');
    expect(v.tail).toBe('· к 16:38'); // 14:08 + 150 мин
    expect(v.source).toBe('разброс 1 ч 50 мин – 3 ч 40 мин · по 14 прошлым фичам');
    expect(v.alt).toBe('Софья: ≈ 2 ч 40 мин · 2 ч назад');
    expect(v.verdict).toEqual({ text: 'до смены этапа', tone: 'warn' });
  });

  it('устаревшая на паузе — метка «до паузы», время готовности заменено словами', () => {
    const v = briefEstimateText(brief({ kind: 'paused', estimate: HIST, note: noteWith({ etaEndsAt: null }, { stale: true }) }), OPTS);
    expect(v.tail).toBe('после снятия паузы');
    expect(v.verdict?.text).toBe('до паузы');
  });

  it('нет оценки оркестратора — по истории с разбросом; об оркестраторе ни слова', () => {
    const v = briefEstimateText(brief({ estimate: HIST }), OPTS);
    expect(v.main).toBe('≈ 2 ч 30 мин');
    expect(v.tail).toBe('· к 16:38');
    expect(v.source).toBe('разброс 1 ч 50 мин – 3 ч 40 мин · по 14 прошлым фичам');
    expect(v.alt).toBeNull();
    expect(v.verdict).toBeNull();
  });

  it('оценка есть, но срок null — как будто оценки оркестратора нет', () => {
    const v = briefEstimateText(brief({ estimate: HIST, note: noteWith({ etaMinutes: null, etaEndsAt: null, agreement: null }) }), OPTS);
    expect(v.main).toBe('≈ 2 ч 30 мин');
    expect(v.alt).toBeNull();
  });

  it('нет никакой оценки — «Оценки пока нет» приглушённо и «прошлых фич … мало»; «≈ 0» нигде', () => {
    const kinds: FeatureBriefKind[] = ['running', 'error', 'paused', 'draft', 'idle'];
    for (const kind of kinds) {
      const v = briefEstimateText(brief({ kind, elapsedMinutes: 70 }), OPTS);
      expect(v.main, kind).toBe('Оценки пока нет');
      expect(v.mainMuted, kind).toBe(true);
      expect(v.source, kind).toBe('прошлых фич для сравнения мало');
    }
  });

  it('время готовности только в «идёт»: пауза, очередь, error — без «к HH:MM»', () => {
    expect(briefEstimateText(brief({ kind: 'paused', estimate: HIST }), OPTS).tail).toBe('после снятия паузы');
    expect(briefEstimateText(brief({ kind: 'queued', estimate: HIST }), OPTS).tail).toBe('после старта');
    expect(briefEstimateText(brief({ kind: 'error', estimate: HIST }), OPTS).tail).toBeNull();
  });

  it('больше 12 ч — дата: «завтра к 10:30», «к 31.10»', () => {
    const tomorrow = briefEstimateText(brief({ estimate: HIST, note: noteWith({ etaMinutes: 1200, etaEndsAt: at(26, 10, 30) }) }), OPTS);
    expect(tomorrow.tail).toBe('· завтра к 10:30');
    const far = briefEstimateText(brief({ estimate: HIST, note: noteWith({ etaMinutes: 9000, etaEndsAt: at(31, 10, 30) }) }), OPTS);
    expect(far.tail).toBe('· к 31.10');
  });

  it('ждёт вас — «за вами», мерж оркестратора строкой, времени и строки истории нет', () => {
    const v = briefEstimateText(brief({ kind: 'waiting_you', estimate: HIST, note: noteWith({ etaMinutes: 5, etaEndsAt: null }) }), OPTS);
    expect(v.main).toBe('за вами');
    expect(v.tail).toBeNull();
    expect(v.alt).toBe('после вашего «да» — мерж ≈ 5 мин (Софья)');
    expect(briefEstimateText(brief({ kind: 'waiting_you', estimate: HIST }), OPTS).alt).toBeNull();
  });

  it('очередь: оценка оркестратора — «после старта» и «старт ≈ 15:00»; без оценки — «Оценка — после старта»', () => {
    const q = at(25, 15, 0);
    const withEst = briefEstimateText(brief({ kind: 'queued', queueStartEstimate: q, note: noteWith({ etaMinutes: 240, etaEndsAt: null, agreement: null }) }), OPTS);
    expect(withEst.main).toBe('≈ 4 ч');
    expect(withEst.tail).toBe('после старта');
    expect(withEst.alt).toBe('старт ≈ 15:00');
    const none = briefEstimateText(brief({ kind: 'queued' }), OPTS);
    expect(none.main).toBe('Оценка — после старта');
    expect(none.mainMuted).toBe(true);
  });

  it('оценка с нулевым остатком — «≈ 0» не печатается', () => {
    const v = briefEstimateText(brief({ estimate: { ...HIST, remainingMinutes: 0 }, note: noteWith({ etaMinutes: 0, etaEndsAt: null, agreement: null }) }), OPTS);
    expect(v.main).toBe('меньше минуты');
    const hist0 = briefEstimateText(brief({ estimate: { ...HIST, remainingMinutes: 0 } }), OPTS);
    expect(`${hist0.main} ${hist0.alt ?? ''}`).not.toMatch(/≈\s*0/);
  });

  it('closed — «Итог»: «шла 6 ч 40 мин» + «на старте Софья обещала ≈ 5 ч · по истории ≈ 4 ч 30 мин»', () => {
    const b = brief({ kind: 'closed', elapsedMinutes: 400, initialEtaMinutes: 300, estimate: { ...HIST, remainingMinutes: 270 }, note: noteWith({}) });
    const v = briefEstimateText(b, OPTS);
    expect(v.header).toBe('Итог');
    expect(v.main).toBe('шла 6 ч 40 мин');
    expect(v.alt).toBe('на старте Софья обещала ≈ 5 ч · по истории ≈ 4 ч 30 мин');
    expect(briefEstimateText(brief({ kind: 'closed', elapsedMinutes: 400 }), OPTS).alt).toBeNull();
  });

  it('closed без длительности или с нулём — «Оценки пока нет», «шла 0 мин» нет', () => {
    expect(briefEstimateText(brief({ kind: 'closed' }), OPTS).main).toBe('Оценки пока нет');
    expect(briefEstimateText(brief({ kind: 'closed', elapsedMinutes: 0 }), OPTS).main).toBe('Оценки пока нет');
  });
});

describe('Первый факт строки итогов', () => {
  it('идёт N по elapsedMinutes; ноль и меньше минуты — не число', () => {
    expect(briefElapsedFact(brief({ elapsedMinutes: 70 }))).toEqual({ label: 'идёт', value: '1 ч 10 мин' });
    expect(briefElapsedFact(brief({ elapsedMinutes: 0 }))).toBeNull();
    expect(briefElapsedFact(brief({ elapsedMinutes: 0.4 }))).toBeNull();
    expect(briefElapsedFact(brief({}))).toBeNull();
  });

  it('очередь — «в очереди N» только по queueWaitMinutes, числа из elapsedMinutes никогда', () => {
    expect(briefElapsedFact(brief({ kind: 'queued', queueWaitMinutes: 25 }))).toEqual({ label: 'в очереди', value: '25 мин' });
    expect(briefElapsedFact(brief({ kind: 'queued', elapsedMinutes: 25 }))).toBeNull();
    expect(briefElapsedFact(brief({ kind: 'queued', queueWaitMinutes: 0 }))).toBeNull();
  });

  it('у закрытой фактов «идёт» нет: длительность уже в «Итог»', () => {
    expect(briefElapsedFact(brief({ kind: 'closed', elapsedMinutes: 400 }))).toBeNull();
  });
});

// ── «Готовность» ────────────────────────────────────────────────────────────────────

const text = (parts: { text: string }[]): string => parts.map(p => p.text).join('');

describe('Готовность', () => {
  it('по оценке оркестратора: «4 готово · 3 в работе · из 9 · 45 %», штриховка по inProgress', () => {
    const v = briefReadinessText(brief({ note: noteWith({ inProgress: 3 }) }), { nameOf });
    expect(v.label).toBe('Готовность');
    expect(text(v.caption)).toBe('4 готово · 3 в работе · из 9 · 45 %');
    expect(v.percent).toBe(45);
    expect(v.inProgressPercent).toBe(33);
    expect(v.ariaLabel).toBe('Готовность 45 %');
    expect(v.stale).toBe(false);
  });

  it('без задач в работе — «4 из 9 · 45 %» и без штриховки', () => {
    const v = briefReadinessText(brief({ note: noteWith({ inProgress: 0 }) }), { nameOf });
    expect(text(v.caption)).toBe('4 из 9 · 45 %');
    expect(v.inProgressPercent).toBe(0);
  });

  it('факт трекера и чипы: первый «следом», остальные как есть', () => {
    const v = briefReadinessText(brief({ note: noteWith({}) }), { nameOf });
    expect(text(v.tracker)).toBe('по трекеру: 2 из 5 шагов приняты · 1 виток правок');
    expect(v.chipsLabel).toBe('Ещё не начато:');
    expect(v.chips).toEqual([{ text: 'следом · проверка · Вера', tone: 'next' }, { text: 'финал', tone: 'plain' }]);
  });

  it('устаревшая оценка — полоска тусклая, у факта трекера метка', () => {
    const v = briefReadinessText(brief({ note: noteWith({}, { stale: true }) }), { nameOf });
    expect(v.stale).toBe(true);
    expect(v.staleLabel).toBe('до смены этапа');
    const paused = briefReadinessText(brief({ kind: 'paused', note: noteWith({}, { stale: true }) }), { nameOf });
    expect(paused.staleLabel).toBe('до паузы');
  });

  it('без оценки — по трекеру: «2 из 5 шагов · 40 %», чипы из next (до трёх)', () => {
    const v = briefReadinessText(brief({
      next: [{ stage: 'qa', personaId: 'p-vera' }, { stage: 'final', personaId: null }, { stage: 'merge', personaId: null }, { stage: 'x', personaId: null }],
    }), { nameOf });
    expect(v.label).toBe('Готовность · по трекеру');
    expect(text(v.caption)).toBe('2 из 5 шагов · 40 %');
    expect(v.chipsLabel).toBe('Осталось:');
    expect(v.chips.map(c => c.text)).toEqual(['следом · проверка · Вера', 'финальное ревью', 'мерж']);
    expect(v.stale).toBe(false);
  });

  it('ждёт мержа — один чип «мерж · за вами»', () => {
    const v = briefReadinessText(brief({ kind: 'waiting_you', reason: 'мерж', note: noteWith({ done: 8, total: 9, percent: 95, remaining: ['мерж'] }) }), { nameOf });
    expect(v.chips).toEqual([{ text: 'мерж · за вами', tone: 'you' }]);
    expect(text(v.caption)).toBe('8 из 9 · 95 %');
  });

  it('закрытая — «всё сделано», штриховки нет, флаг closed', () => {
    const v = briefReadinessText(brief({ kind: 'closed', note: noteWith({ done: 9, total: 9, percent: 100, inProgress: 2, remaining: [] }) }), { nameOf });
    expect(v.closed).toBe(true);
    expect(v.chipsEmpty).toBe('ничего — всё сделано');
    expect(v.chips).toEqual([]);
    expect(v.inProgressPercent).toBe(0);
    expect(text(v.caption)).toBe('9 из 9 · 100 %');
  });

  it('очередь с нарезанным планом — без «по трекеру:» перед «План готов»', () => {
    const v = briefReadinessText(brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } }), { nameOf });
    expect(text(v.tracker)).toBe('План готов · 0 из 4 шагов');
  });

  it('percent не пришёл — округлённое done/total; шагов нет — 0 %', () => {
    const noPct = briefReadinessText(brief({ note: noteWith({ percent: undefined as unknown as number }) }), { nameOf });
    expect(noPct.percent).toBe(44);
    const empty = briefReadinessText(brief({ done: { stepsAccepted: 0, stepsTotal: 0, loops: 0, planState: 'pending' } }), { nameOf });
    expect(empty.percent).toBe(0);
  });
});

// ── 4. «N мин/ч назад» ──────────────────────────────────────────────────────────────

describe('«N мин/ч назад»', () => {
  it('меньше минуты — «только что» (макет: «обновлено только что»)', () => {
    expect(agoText(at(25, 14, 9, 30), NOW)).toBe('только что');
  });
  it('25 минут — «25 мин назад»', () => {
    expect(agoText(at(25, 13, 45), NOW)).toBe('25 мин назад');
  });
  it('полтора часа — «1 ч назад»', () => {
    expect(agoText(at(25, 12, 40), NOW)).toBe('1 ч назад');
  });
  it('сутки и больше — дата «24.10, 18:06» (макет closed voice)', () => {
    // Порог «суток» считаем по своему NOW: 25.10 18:10 → до 24.10 18:06 больше 24 ч.
    const later = new Date(2026, 9, 25, 18, 10).getTime();
    expect(agoText(at(24, 18, 6), later)).toBe('24.10, 18:06');
  });
  it('нет даты / нечитаемая — null', () => {
    expect(agoText(null, NOW)).toBeNull();
    expect(agoText('не дата', NOW)).toBeNull();
  });
});

// ── 4a. «обновлено …» (F15, дефект f2da9354): по updatedAt, не по computedAt ─────────

describe('«обновлено …» (F15, дефект f2da9354)', () => {
  it('updatedAt 3 часа назад — «обновлено 3 ч назад», не по computedAt (критерий c1)', () => {
    // computedAt фикстуры — 14:08 (2 мин назад): считай строка от него, было бы «2 мин назад».
    const b = brief({ updatedAt: at(25, 11, 10) });
    expect(briefUpdatedText(b, null, NOW)).toBe('обновлено 3 ч назад');
  });

  it('updatedAt нет (поля нет или null) — строки нет, computedAt не подставляется (критерий c2)', () => {
    expect(briefUpdatedText(brief({}), null, NOW)).toBeNull();
    expect(briefUpdatedText(brief({ updatedAt: null }), null, NOW)).toBeNull();
  });

  it('«только что» — только когда разница меньше минуты; полная минута — число (W5-R-F3b)', () => {
    expect(briefUpdatedText(brief({ updatedAt: at(25, 14, 9, 30) }), null, NOW)).toBe('обновлено только что');
    expect(briefUpdatedText(brief({ updatedAt: at(25, 14, 9) }), null, NOW)).toBe('обновлено 1 мин назад');
  });

  it('closed — по дате закрытия (макет §Поведение): mergedAt, а его нет — updatedAt', () => {
    const b = brief({ kind: 'closed', updatedAt: at(25, 11, 10) });
    // mergedAt 30 с назад (< минуты) — «только что»; ровно минута — уже «1 мин назад» (W5-R-F3b).
    expect(briefUpdatedText(b, at(25, 14, 9, 30), NOW)).toBe('обновлено только что');
    expect(briefUpdatedText(b, at(25, 14, 9), NOW)).toBe('обновлено 1 мин назад');
    expect(briefUpdatedText(b, null, NOW)).toBe('обновлено 3 ч назад');
  });
});

// ── 5. Голос: метка устаревшего, роль, время подписи ────────────────────────────────

describe('Голос оркестратора', () => {
  const fresh = { text: 'Идём по плану', at: at(25, 13, 56), byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'review', stale: false };
  const stale = { ...fresh, at: at(25, 13, 25), stale: true };

  it('свежий — метки нет, время «14 мин назад»', () => {
    expect(voiceStaleLabel(fresh, 'running')).toBeNull();
    expect(voiceWhenText(fresh, 'running', NOW)).toBe('14 мин назад');
  });

  it('устаревший в паузе — «до паузы, 45 мин назад» (макет hold)', () => {
    expect(voiceStaleLabel(stale, 'paused')).toBe('до паузы');
    expect(voiceWhenText(stale, 'paused', NOW)).toBe('до паузы, 45 мин назад');
  });

  it('устаревший вне паузы — «до смены этапа»', () => {
    expect(voiceStaleLabel(stale, 'running')).toBe('до смены этапа');
    expect(voiceWhenText(stale, 'running', NOW)).toBe('до смены этапа, 45 мин назад');
  });

  it('нечитаемая дата голоса — только метка, без висячего запятого', () => {
    expect(voiceWhenText({ ...stale, at: 'не дата' }, 'paused', NOW)).toBe('до паузы');
  });

  it('роль: «ведёт фичу», у закрытой — «вела фичу» (макет closed)', () => {
    expect(voiceRoleText('running')).toBe('ведёт фичу');
    expect(voiceRoleText('closed')).toBe('вела фичу');
  });
});

// ── 6. Родительный падеж для «у {Имя}» ─────────────────────────────────────────────

describe('genitiveName', () => {
  it('частые имена проекта', () => {
    expect(genitiveName('Глеб')).toBe('Глеба');
    expect(genitiveName('Кира')).toBe('Киры');
    expect(genitiveName('Софья')).toBe('Софьи');
    expect(genitiveName('Денис')).toBe('Дениса');
    expect(genitiveName('Игорь')).toBe('Игоря');
  });
  it('имена на «-й» — «я», не «+а» (R-F2: не «Дмитрийа»)', () => {
    expect(genitiveName('Дмитрий')).toBe('Дмитрия');
    expect(genitiveName('Сергей')).toBe('Сергея');
    expect(genitiveName('Алексей')).toBe('Алексея');
  });
  it('некириллическое окончание — как есть', () => {
    expect(genitiveName('Alex')).toBe('Alex');
  });
});
