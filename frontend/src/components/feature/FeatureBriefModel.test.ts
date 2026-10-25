// Перевод ключей brief в слова (волна 6, шаг F2): строки — по макету
// docs/mockups/feature-summary-1.md (состояния §2) и разрезу п. 4 для kinds вне макета.
// Чистые функции без DOM и времени «сейчас»: nowMs и даты фиксированы, моменты
// построены локальными компонентами Date — TZ-независимо (CI Linux = dev Windows).

import { describe, it, expect } from 'vitest';
import {
  agoText, briefEstimateText, briefNextText, briefNowText, genitiveName,
  updatedText, voiceRoleText, voiceStaleLabel, voiceWhenText,
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

  it('error — «Исполнитель встал: <причина>» (разрез п. 4)', () => {
    const b = brief({ kind: 'error', reason: 'сохранение журнала упало' });
    expect(briefNowText(b, { nameOf })).toBe('Исполнитель встал: сохранение журнала упало');
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
});

// ── 3. «Оценка» ─────────────────────────────────────────────────────────────────────

describe('Оценка', () => {
  it('running с оценкой — «≈ 2 ч 30 мин осталось» + «идёт 1 ч 10 мин» (макет run)', () => {
    const b = brief({
      elapsedMinutes: 70,
      estimate: { remainingMinutes: 150, p25: 120, p75: 180, source: 'history', sampleSize: 4 },
    });
    expect(briefEstimateText(b)).toEqual({
      header: 'Оценка', main: '≈ 2 ч 30 мин осталось', fact: 'идёт 1 ч 10 мин',
    });
  });

  it('waiting_you с оценкой — «Осталось — за вами» (макет wait)', () => {
    const b = brief({
      kind: 'waiting_you', elapsedMinutes: 260,
      estimate: { remainingMinutes: 5, p25: 3, p75: 9, source: 'history', sampleSize: 4 },
    });
    expect(briefEstimateText(b).main).toBe('Осталось — за вами');
    expect(briefEstimateText(b).fact).toBe('идёт 4 ч 20 мин');
  });

  it('estimate = null — «Оценки пока нет» во всех не-закрытых kind; «≈ 0» нигде (критерий c3)', () => {
    const kinds: FeatureBriefKind[] = ['running', 'waiting_you', 'error', 'paused', 'queued', 'draft', 'idle'];
    for (const kind of kinds) {
      const v = briefEstimateText(brief({ kind, elapsedMinutes: 70 }));
      expect(v.main, kind).toBe('Оценки пока нет');
      expect(v.main, kind).not.toMatch(/≈\s*0/);
    }
  });

  it('estimate с нулевым остатком — «≈ 0» не печатается', () => {
    const b = brief({ estimate: { remainingMinutes: 0, p25: 0, p75: 1, source: 'history', sampleSize: 4 } });
    const v = briefEstimateText(b);
    expect(v.main).toBe('меньше минуты осталось');
    expect(`${v.main} ${v.fact ?? ''}`).not.toMatch(/≈\s*0/);
  });

  it('closed — подпись меняется на «Итог»: «шла 6 ч 40 мин» (макет closed)', () => {
    const b = brief({ kind: 'closed', elapsedMinutes: 400 });
    expect(briefEstimateText(b)).toEqual({ header: 'Итог', main: 'шла 6 ч 40 мин', fact: null });
  });

  it('closed без длительности — «Оценки пока нет»', () => {
    const b = brief({ kind: 'closed' });
    expect(briefEstimateText(b).main).toBe('Оценки пока нет');
  });

  it('elapsed = null — факта «идёт» нет', () => {
    expect(briefEstimateText(brief({})).fact).toBeNull();
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
  it('updatedText — «обновлено 2 мин назад» (макет)', () => {
    expect(updatedText(at(25, 14, 8), NOW)).toBe('обновлено 2 мин назад');
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
  it('некириллическое окончание — как есть', () => {
    expect(genitiveName('Alex')).toBe('Alex');
  });
});
