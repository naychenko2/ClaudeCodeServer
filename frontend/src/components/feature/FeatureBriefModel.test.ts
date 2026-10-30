// Перевод ключей brief в слова (волна 6, шаг F2): строки — по макету
// docs/mockups/feature-summary-1.md (состояния §2) и разрезу п. 4 для kinds вне макета.
// Чистые функции без DOM и времени «сейчас»: nowMs и даты фиксированы, моменты
// построены локальными компонентами Date — TZ-независимо (CI Linux = dev Windows).

import { describe, it, expect } from 'vitest';
import {
  agoText, briefActiveTagLabel, briefActiveView, briefWaitTagLabel, briefDoneText, briefElapsedFact, briefEstimateText, briefNextText, briefNowText, briefReadinessText, briefSubText, briefUpdatedText, genitiveName, dativeName,
  voiceRoleText, voiceStaleLabel, voiceWhenText,
} from './FeatureBriefModel';
import type { FeatureBrief, FeatureBriefActive, FeatureBriefKind } from '../../lib/featurePage';

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

  it('waiting_you с тревогой «мерж» — «Нужно согласие на мерж в master» (Ш4: без «ждёт вас»)', () => {
    const b = brief({ kind: 'waiting_you', reason: 'мерж' });
    expect(briefNowText(b, { nameOf })).toBe('Нужно согласие на мерж в master');
  });

  it('waiting_you со своей причиной — текст причины', () => {
    const b = brief({ kind: 'waiting_you', reason: 'нужен твой ответ по макету' });
    expect(briefNowText(b, { nameOf })).toBe('Нужен ответ: нужен твой ответ по макету');
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

  it('waiting_you — «N · фича стоит» (Ш4: «ждёт вас» говорит тревога страницы)', () => {
    const b = brief({ kind: 'waiting_you', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) } });
    expect(briefSubText(b, NOW)).toBe('30 мин · фича стоит');
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
    expect(briefSubText(wait, NOW)).toBe('фича стоит');
    const hold = brief({ kind: 'paused', reason: 'manual', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 10) } });
    expect(briefSubText(hold, NOW)).toBe('исполнители не запускаются');
    const short = brief({ kind: 'waiting_you', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 9, 40) } });
    expect(briefSubText(short, NOW)).toBe('фича стоит');
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

  it('«2 из 5 шагов приняты · 1 возврат»: числа — mono-части, связный текст — обычные', () => {
    const parts = briefDoneText(brief({ done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' } }));
    expect(parts.map(p => p.text).join('')).toBe('2 из 5 шагов приняты · 1 возврат');
    expect(parts.map(p => p.mono)).toEqual([true, false, true, false]);
  });

  it('loops = 0 — хвоста про возвраты нет', () => {
    expect(textOf(brief({ done: { stepsAccepted: 5, stepsTotal: 5, loops: 0, planState: 'cut' } }))).toBe('5 из 5 шагов приняты');
  });

  it('склонение разряда: 1 возврат / 2 возврата / 5 возвратов / 11 возвратов (Ш3: словарь «возвратов»)', () => {
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 1, planState: 'cut' } }))).toContain('1 возврат');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 2, planState: 'cut' } }))).toContain('2 возврата');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 5, planState: 'cut' } }))).toContain('5 возвратов');
    expect(textOf(brief({ done: { stepsAccepted: 1, stepsTotal: 5, loops: 11, planState: 'cut' } }))).toContain('11 возвратов');
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

  it('при тревоге срок — дата как была и пояснение «пока ждёт вас, срок стоит» (Ш4); «за вами» нет', () => {
    const v = briefEstimateText(brief({ kind: 'waiting_you', estimate: HIST, note: noteWith({ etaMinutes: 5, etaEndsAt: at(25, 14, 15) }) }), OPTS);
    expect(v.main).toBe('≈ 5 мин');
    expect(v.tail).toBe('· к 14:15');
    expect(v.note).toBe('пока ждёт вас, срок стоит');
    // Пояснение — только при тревоге: у идущей фичи его нет.
    expect(briefEstimateText(brief({ kind: 'running', estimate: HIST, note: noteWith({ etaMinutes: 5, etaEndsAt: at(25, 14, 15) }) }), OPTS).note).toBeNull();
    // Без оценки числа нет — стоять нечему, пояснения нет.
    const none = briefEstimateText(brief({ kind: 'waiting_you' }), OPTS);
    expect(none.mainMuted).toBe(true);
    expect(none.note).toBeNull();
  });

  it('Ш4: «ждёт вас»/«за вами» в сводке не звучат — только пояснение срока', () => {
    const waiting = brief({
      kind: 'waiting_you', reason: 'мерж', estimate: HIST,
      now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) },
      done: { stepsAccepted: 8, stepsTotal: 9, loops: 0, planState: 'done' },
      note: noteWith({ etaMinutes: 5, etaEndsAt: at(25, 14, 15) }),
      active: [act({ state: 'waiting_you', since: at(25, 14, 2) }), act({ taskId: 't2', personaId: 'p-gleb' })],
    });
    const est = briefEstimateText(waiting, OPTS);
    const chips = briefReadinessText(waiting, { nameOf });
    const av = briefActiveView(waiting, AOPTS)!;
    const texts = [
      briefNowText(waiting, { nameOf }), briefSubText(waiting, NOW), est.main, est.tail, est.alt, est.source, est.verdict?.text,
      ...chips.chips.map(c => c.text), chips.chipsLabel, av.heading, av.sub, briefActiveTagLabel(waiting), briefWaitTagLabel(waiting),
    ].filter(Boolean).join(' | ');
    expect(texts).not.toMatch(/ждёт вас|за вами|ждёт ваш|без вас/i);
    expect(est.note).toBe('пока ждёт вас, срок стоит');
  });

  it('briefWaitTagLabel: вопрос — «нужен ответ», мерж-вопрос — тега-слова нет (тег «к мержу» из статуса)', () => {
    expect(briefWaitTagLabel(brief({ kind: 'waiting_you', reason: 'уточни' }))).toBe('нужен ответ');
    expect(briefWaitTagLabel(brief({ kind: 'waiting_you', reason: 'мерж' }))).toBeNull();
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
  it('«идёт N» по brief.elapsedMinutes убрано (П3): «с начала» строка итогов берёт из totals статов', () => {
    expect(briefElapsedFact(brief({ elapsedMinutes: 70 }))).toBeNull();
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

// Ш3: процент и полоса — только по трекеру (тот же trackerPercent, что у полос В2/С1/П2).
// note.estimate в готовность не входит ни числом, ни чипами, ни тускнением — она живёт
// только в «Срок» (тесты ниже не тронуты).
describe('Готовность', () => {
  const D98 = { stepsAccepted: 26, stepsTotal: 98, loops: 0, planState: 'cut' as const };

  it('при оценке оркестратора процент всё равно по трекеру: 26/98 → «27 %», без «по оценке»', () => {
    const v = briefReadinessText(brief({
      done: D98,
      note: noteWith({ done: 26, total: 98, percent: 100, inProgress: 30, remaining: ['что-то'] }),
    }), { nameOf });
    expect(v.percent).toBe(27);
    expect(v.label).toBe('Готовность · по трекеру');
    expect(text(v.caption)).toBe('26 из 98 шагов · 27 %');
    expect(text(v.caption)).not.toContain('по оценке');
    expect(v.ariaLabel).toBe('Готовность 27 %');
  });

  it('без оценки — те же «27 %»: оценка не влияет на готовность', () => {
    const v = briefReadinessText(brief({ done: D98 }), { nameOf });
    expect(v.percent).toBe(27);
    expect(text(v.caption)).toBe('26 из 98 шагов · 27 %');
  });

  it('факт трекера и чипы из next (до трёх), «Осталось:» — и при оценке', () => {
    const v = briefReadinessText(brief({
      note: noteWith({}),
      next: [{ stage: 'qa', personaId: 'p-vera' }, { stage: 'final', personaId: null }, { stage: 'merge', personaId: null }, { stage: 'x', personaId: null }],
    }), { nameOf });
    expect(text(v.tracker)).toBe('по трекеру: 2 из 5 шагов приняты · 1 возврат');
    expect(v.chipsLabel).toBe('Осталось:');
    expect(v.chips.map(c => c.text)).toEqual(['следом · проверка · Вера', 'финальное ревью', 'мерж']);
  });

  it('без оценки — по трекеру: «2 из 5 шагов · 40 %», чипы из next (до трёх)', () => {
    const v = briefReadinessText(brief({
      next: [{ stage: 'qa', personaId: 'p-vera' }, { stage: 'final', personaId: null }, { stage: 'merge', personaId: null }, { stage: 'x', personaId: null }],
    }), { nameOf });
    expect(v.label).toBe('Готовность · по трекеру');
    expect(text(v.caption)).toBe('2 из 5 шагов · 40 %');
    expect(v.chipsLabel).toBe('Осталось:');
    expect(v.chips.map(c => c.text)).toEqual(['следом · проверка · Вера', 'финальное ревью', 'мерж']);
  });

  it('ждёт мержа — один чип «мерж», процент по трекеру', () => {
    const v = briefReadinessText(brief({ kind: 'waiting_you', reason: 'мерж', done: { stepsAccepted: 8, stepsTotal: 9, loops: 0, planState: 'done' }, note: noteWith({}) }), { nameOf });
    expect(v.chips).toEqual([{ text: 'мерж', tone: 'you' }]);
    expect(text(v.caption)).toBe('8 из 9 шагов · 89 %');
  });

  it('закрытая — «всё сделано», флаг closed, процент по трекеру', () => {
    const v = briefReadinessText(brief({ kind: 'closed', done: { stepsAccepted: 9, stepsTotal: 9, loops: 0, planState: 'done' }, note: noteWith({}) }), { nameOf });
    expect(v.closed).toBe(true);
    expect(v.chipsEmpty).toBe('ничего — всё сделано');
    expect(v.chips).toEqual([]);
    expect(text(v.caption)).toBe('9 из 9 шагов · 100 %');
  });

  it('очередь с нарезанным планом — без «по трекеру:» перед «План готов»', () => {
    const v = briefReadinessText(brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } }), { nameOf });
    expect(text(v.tracker)).toBe('План готов · 0 из 4 шагов');
  });

  it('знаменателя нет — 0 %', () => {
    const empty = briefReadinessText(brief({ done: { stepsAccepted: 0, stepsTotal: 0, loops: 0, planState: 'pending' } }), { nameOf });
    expect(empty.percent).toBe(0);
  });
});

// ── «Готовность»: пометка «в master» (F1) ───────────────────────────────────────────

describe('Готовность: пометка «в master» (F1)', () => {
  const D5 = { stepsAccepted: 4, stepsTotal: 5, loops: 0, planState: 'cut' as const };

  it('по трекеру: пометка ОДНА — в подписи (не в факте)', () => {
    const v = briefReadinessText(brief({ done: D5 }), { nameOf, inMaster: 3 });
    expect(v.inMaster).toBe(3);
    expect(text(v.caption)).toBe('4 из 5 шагов · 80 % · 3 в master');
    // Ревю 07eee660 минор 3: «N в master» в одном блоке печаталось дважды (подпись + факт).
    expect(text(v.tracker)).toBe('по трекеру: 4 из 5 шагов приняты');
    expect(v.ariaLabel).toBe('Готовность 80 %, 3 в master');
  });

  it('нет поля / 0 / битые числа — поведение прежнее: пометки нет', () => {
    for (const opts of [{}, { inMaster: 0 }, { inMaster: -3 }, { inMaster: Number.NaN }, { inMaster: null }]) {
      const v = briefReadinessText(brief({ done: D5 }), { nameOf, ...opts });
      expect(v.inMaster).toBe(0);
      expect(text(v.caption)).toBe('4 из 5 шагов · 80 %');
      expect(text(v.tracker)).toBe('по трекеру: 4 из 5 шагов приняты');
      expect(v.ariaLabel).toBe('Готовность 80 %');
    }
  });

  it('при оценке оркестратора готовность всё равно трекерная: пометка на месте (Ш3)', () => {
    const v = briefReadinessText(brief({ note: noteWith({}) }), { nameOf, inMaster: 2 });
    expect(v.inMaster).toBe(2);
    expect(text(v.caption)).toBe('2 из 5 шагов · 40 % · 2 в master');
    expect(text(v.tracker)).toBe('по трекеру: 2 из 5 шагов приняты · 1 возврат');
    expect(v.ariaLabel).toBe('Готовность 40 %, 2 в master');
  });

  it('закрытая очередь с планом: без inMaster — «План готов» без хвоста; с inMaster — хвост есть', () => {
    const plain = briefReadinessText(brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } }), { nameOf });
    expect(text(plain.tracker)).toBe('План готов · 0 из 4 шагов');
    // С inMaster — хвост в подписи полоски (одна пометка), не в строке «План готов».
    const withMaster = briefReadinessText(brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } }), { nameOf, inMaster: 1 });
    expect(text(withMaster.tracker)).toBe('План готов · 0 из 4 шагов');
    expect(text(withMaster.caption)).toContain('1 в master');
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

// ── Задачи в работе: заголовок, строки, тег (макет 2, «Итерация 2») ────────────────────

const act = (over: Partial<FeatureBriefActive> = {}): FeatureBriefActive => ({
  taskId: 't1', stage: 'prod', stepRef: '4', lane: 'front', personaId: 'p-kira', state: 'working',
  since: at(25, 14, 5), reason: null, ...over,
});
const AOPTS = { nameOf, nowMs: NOW };

describe('Задачи в работе: briefActiveView', () => {
  it('поля active нет / пусто / эпик ждёт мержа / одна задача без ожидания — null (прежняя строка)', () => {
    expect(briefActiveView(brief(), AOPTS)).toBeNull();
    expect(briefActiveView(brief({ active: [] }), AOPTS)).toBeNull();
    expect(briefActiveView(brief({ active: [act()] }), AOPTS)).toBeNull();
    expect(briefActiveView(brief({ kind: 'waiting_you', active: [act({ stage: 'feature', state: 'waiting_you' })] }), AOPTS)).toBeNull();
  });

  it('несколько задач, никто не ждёт — «3 задачи в работе», подстрока по полосам, строки и время', () => {
    const v = briefActiveView(brief({ active: [
      act(), act({ taskId: 't2', stage: 'review', stepRef: '3', lane: 'back', personaId: 'p-gleb', state: 'review', since: at(25, 13, 58) }),
      act({ taskId: 't3', stage: 'qa', stepRef: null, lane: null, personaId: null }),
    ] }), AOPTS)!;
    expect(v.heading).toBe('3 задачи в работе');
    expect(v.sub).toBe('параллельно: фронт и бэк');
    expect(v.answer).toBeNull();
    expect(v.rows.map(r => r.title)).toEqual(['Шаг 4 (фронт)', 'Ревью шага 3 (бэк)', 'Проверка']);
    expect(v.rows[0].time).toBe('с 14:05 · 5 мин');
    expect(v.rows[1].time).toBe('с 13:58 · 12 мин');
    expect(v.rows[0].ariaLabel).toBe('Шаг 4 (фронт) — Кира, открыть задачу');
    expect(v.rows[2].ariaLabel).toBe('Проверка, открыть задачу');
    expect(v.tail).toBeNull();
  });

  it('одна линия — «линия: бэк»; линий нет — подстроки нет', () => {
    const two = [act({ lane: 'back' }), act({ taskId: 't2', lane: 'back' })];
    expect(briefActiveView(brief({ active: two }), AOPTS)!.sub).toBe('линия: бэк');
    expect(briefActiveView(brief({ active: two.map(a => ({ ...a, lane: null })) }), AOPTS)!.sub).toBeNull();
  });

  it('ждёт человека — заголовок про задачу, «Ответить Кире», рамка-ромб; остальные считаются в подстроке', () => {
    const v = briefActiveView(brief({ kind: 'waiting_you', active: [
      act({ state: 'waiting_you', since: at(25, 14, 2) }), act({ taskId: 't2', personaId: 'p-gleb' }), act({ taskId: 't3', personaId: 'p-vera' }),
    ] }), AOPTS)!;
    expect(v.heading).toBe('Кира задаёт вопрос по шагу 4');
    expect(v.sub).toBe('8 мин · шаг 4 стоит, 2 другие задачи идут');
    expect(v.answer).toEqual({ taskId: 't1', label: 'Ответить Кире' });
    expect(v.rows).toHaveLength(3);
  });

  it('ждёт человека и задача одна — заголовок и кнопка есть, списка нет; имени нет — «Ответить»', () => {
    const v = briefActiveView(brief({ kind: 'waiting_you', active: [act({ state: 'waiting_you', personaId: null })] }), AOPTS)!;
    expect(v.heading).toBe('Задача задаёт вопрос по шагу 4');
    expect(v.answer?.label).toBe('Ответить');
    expect(v.rows).toEqual([]);
    expect(v.sub).toBe('5 мин · шаг 4 стоит');
  });

  it('пауза — заголовок прежний (null), в списке остановленные, даже если задача одна', () => {
    const v = briefActiveView(brief({ kind: 'paused', active: [act({ state: 'held', since: at(25, 13, 40) })] }), AOPTS)!;
    expect(v.heading).toBeNull();
    expect(v.answer).toBeNull();
    expect(v.rows).toHaveLength(1);
    expect(v.rows[0].time).toBe('остановлен в 13:40');
    expect(v.rows[0].state).toBe('held');
  });

  it('ошибка — «· стоит» у строки и время старта состояния', () => {
    const v = briefActiveView(brief({ active: [act({ state: 'error', since: at(25, 14, 0) }), act({ taskId: 't2' })] }), AOPTS)!;
    expect(v.rows[0].stopped).toBe('· стоит');
    expect(v.rows[0].time).toBe('с 14:00');
    expect(v.rows[1].stopped).toBeNull();
  });

  it('больше четырёх — видно 4, хвост «ещё N в работе — во вкладке «Задачи» ›»', () => {
    const six = Array.from({ length: 6 }, (_, i) => act({ taskId: `t${i}` }));
    const v = briefActiveView(brief({ active: six }), AOPTS)!;
    expect(v.rows).toHaveLength(4);
    expect(v.tail).toBe('ещё 2 в работе — во вкладке «Задачи» ›');
    expect(v.heading).toBe('6 задач в работе');
  });

  it('название без шага: стадия одним словом; prod без шага — «Производство»', () => {
    const v = briefActiveView(brief({ active: [act({ stage: 'prod', stepRef: null, lane: null }), act({ taskId: 't2', stage: 'final', stepRef: null, lane: null })] }), AOPTS)!;
    expect(v.rows.map(r => r.title)).toEqual(['Производство', 'Финальное ревью']);
  });
});

describe('Задачи в работе: тег со счётом и падеж', () => {
  it('«идёт · 3 в работе» и «нужен ответ · 1 из 3»; одна задача и прочие kind — тег прежний', () => {
    const three = [act(), act({ taskId: 't2' }), act({ taskId: 't3', state: 'waiting_you' })];
    expect(briefActiveTagLabel(brief({ kind: 'running', active: three }))).toBe('идёт · 3 в работе');
    expect(briefActiveTagLabel(brief({ kind: 'waiting_you', active: three }))).toBe('нужен ответ · 1 из 3');
    expect(briefActiveTagLabel(brief({ kind: 'running', active: [act()] }))).toBeNull();
    expect(briefActiveTagLabel(brief({ kind: 'paused', active: three }))).toBeNull();
    expect(briefActiveTagLabel(brief({ kind: 'running' }))).toBeNull();
  });

  it('дательный падеж имени', () => {
    expect([ 'Кира', 'Глеб', 'Софья', 'Игорь', 'Дмитрий' ].map(dativeName)).toEqual(['Кире', 'Глебу', 'Софье', 'Игорю', 'Дмитрию']);
  });
});
