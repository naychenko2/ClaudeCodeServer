import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { RateLimitInfo, UsageSnapshot } from '../../types';
import { toRateWindows, worstWindow, fmtReset, windowLabel, latestPerWindow, latestWithUtilization, snapshotFreshnessLabel, overageLabel, withAccountFallback, ratePillSegments, ratePillVisible, shortWindowLabel, ringPillHead, worstRingCandidate, windowCandidates, ringArcLength, RING_WINDOWS, type RingPillCandidate } from '../rateLimit';

const win = (limitType: string, over: Partial<RateLimitInfo> = {}): RateLimitInfo =>
  ({ limitType, ...over });

describe('toRateWindows', () => {
  it('окна без utilization и без status отфильтровываются', () => {
    const out = toRateWindows({
      five_hour: win('five_hour', { utilization: 0.5 }),
      empty: win('empty'),
      status_only: win('status_only', { status: 'allowed' }),
    });
    expect(out.map(w => w.limitType).sort()).toEqual(['five_hour', 'status_only']);
  });

  it('сортировка по utilization по убыванию', () => {
    const out = toRateWindows({
      a: win('a', { utilization: 0.2 }),
      b: win('b', { utilization: 0.9 }),
      c: win('c', { utilization: 0.5 }),
    });
    expect(out.map(w => w.limitType)).toEqual(['b', 'c', 'a']);
  });

  it('pct: округление и клампинг в 0..100, hasUtil отражает наличие данных', () => {
    const out = toRateWindows({
      over: win('over', { utilization: 1.2 }),
      neg: win('neg', { utilization: -0.1 }),
      mid: win('mid', { utilization: 0.456 }),
      none: win('none', { status: 'allowed' }),
    });
    const byType = Object.fromEntries(out.map(w => [w.limitType, w]));
    expect(byType.over).toMatchObject({ pct: 100, hasUtil: true });
    expect(byType.neg).toMatchObject({ pct: 0, hasUtil: true });
    expect(byType.mid).toMatchObject({ pct: 46, hasUtil: true });
    expect(byType.none).toMatchObject({ pct: 0, hasUtil: false });
  });

  it('уровни: rejected/overage/≥1 → danger; allowed_warning/≥0.6 → warn; иначе normal', () => {
    const out = toRateWindows({
      rej: win('rej', { utilization: 0.1, status: 'rejected' }),
      over: win('over', { utilization: 0.2, isUsingOverage: true }),
      full: win('full', { utilization: 1 }),
      warnStatus: win('warnStatus', { utilization: 0.1, status: 'allowed_warning' }),
      warnUtil: win('warnUtil', { utilization: 0.6 }),
      ok: win('ok', { utilization: 0.59 }),
    });
    const levels = Object.fromEntries(out.map(w => [w.limitType, w.level]));
    expect(levels).toEqual({
      rej: 'danger',
      over: 'danger',
      full: 'danger',
      warnStatus: 'warn',
      warnUtil: 'warn',
      ok: 'normal',
    });
  });

  it('seven_day_overage_included — служебное событие, а не окно: вместе с seven_day одно окно', () => {
    const out = toRateWindows({
      seven_day: win('seven_day', { utilization: 0.12, status: 'allowed' }),
      seven_day_overage_included: win('seven_day_overage_included', { utilization: 0.12, status: 'allowed' }),
    });
    expect(out.map(w => w.limitType)).toEqual(['seven_day']);
  });

  it('seven_day_overage_included отсекается и в пути через снимки аккаунта', () => {
    const reset = '2026-10-01T10:00:00Z';
    const out = withAccountFallback(
      [win('seven_day_overage_included', { status: 'allowed', resetsAt: reset })],
      [{ timestamp: '2026-09-27T11:50:00Z', limitType: 'seven_day', utilization: 0.12, resetsAt: reset },
        { timestamp: '2026-09-27T11:50:00Z', limitType: 'seven_day_overage_included', utilization: 0.12, resetsAt: reset }],
      '2026-09-27T12:00:00Z',
    );
    expect(out.map(w => w.limitType)).toEqual(['seven_day']);
    expect(ratePillSegments(out).map(s => s.label)).toEqual(['Нед']);
  });

  it('nimbus_quill — кодовое имя из ответа oauth/usage, а не окно: старый снимок без сброса отсекается', () => {
    const out = withAccountFallback(
      [],
      [{ timestamp: '2026-09-30T08:00:00Z', limitType: 'five_hour', utilization: 0.06, resetsAt: '2026-09-30T12:00:00Z' },
        { timestamp: '2026-09-30T08:00:00Z', limitType: 'nimbus_quill', utilization: 0 }],
      '2026-09-30T08:05:00Z',
    );
    expect(out.map(w => w.limitType)).toEqual(['five_hour']);
  });
});

describe('latestPerWindow', () => {
  const snap = (ts: string, limitType: string, over: Partial<UsageSnapshot> = {}): UsageSnapshot =>
    ({ timestamp: ts, limitType, ...over });

  it('берёт последний снимок каждого окна', () => {
    const out = latestPerWindow([
      snap('2026-07-19T10:00:00Z', 'five_hour', { utilization: 0.3 }),
      snap('2026-07-19T12:00:00Z', 'five_hour', { utilization: 0.5 }),
      snap('2026-07-19T11:00:00Z', 'seven_day', { utilization: 0.1 }),
    ]);
    const byType = Object.fromEntries(out.map(w => [w.limitType, w]));
    expect(byType.five_hour.pct).toBe(50);
    expect(byType.seven_day.pct).toBe(10);
  });

  it('свежий снимок без процента наследует процент того же окна (сброс совпадает)', () => {
    const reset = '2026-07-19T15:00:00Z';
    const out = latestPerWindow([
      snap('2026-07-19T10:00:00Z', 'five_hour', { utilization: 0.51, resetsAt: reset }),
      snap('2026-07-19T12:00:00Z', 'five_hour', { status: 'allowed', resetsAt: reset }),
    ]);
    expect(out[0]).toMatchObject({ pct: 51, hasUtil: true, resetsAt: reset });
  });

  it('после сброса окна старый процент не подставляется', () => {
    const out = latestPerWindow([
      snap('2026-07-19T10:00:00Z', 'five_hour', { utilization: 0.9, resetsAt: '2026-07-19T11:00:00Z' }),
      snap('2026-07-19T12:00:00Z', 'five_hour', { status: 'allowed', resetsAt: '2026-07-19T16:00:00Z' }),
    ]);
    expect(out[0].hasUtil).toBe(false);
  });
});

describe('пилюля лимитов в шапке чата', () => {
  const snap = (ts: string, limitType: string, over: Partial<UsageSnapshot> = {}): UsageSnapshot =>
    ({ timestamp: ts, limitType, ...over });
  const NOW = '2026-09-27T12:00:00Z';
  const R5 = '2026-09-27T15:00:00Z';
  const RW = '2026-10-01T10:00:00Z';

  it('три окна: постоянный порядок 5ч → неделя → модель, свой уровень у каждого', () => {
    const windows = toRateWindows({
      seven_day_opus: win('seven_day_opus', { utilization: 0.3 }),
      seven_day: win('seven_day', { utilization: 0.12 }),
      five_hour: win('five_hour', { utilization: 0.41 }),
    });
    expect(ratePillSegments(windows)).toEqual([
      { limitType: 'five_hour', label: '5ч', text: '41%', pct: 41, level: 'normal' },
      { limitType: 'seven_day', label: 'Нед', text: '12%', pct: 12, level: 'normal' },
      { limitType: 'seven_day_opus', label: 'Opus', text: '30%', pct: 30, level: 'normal' },
    ]);
  });

  it('окно без utilization в чате берёт процент из снимка аккаунта (то же окно)', () => {
    const out = withAccountFallback(
      [win('five_hour', { status: 'allowed', resetsAt: R5 })],
      [snap('2026-09-27T11:50:00Z', 'five_hour', { utilization: 0.41, resetsAt: R5 }),
        snap('2026-09-27T11:50:00Z', 'seven_day', { utilization: 0.12, resetsAt: RW })],
      NOW,
    );
    expect(ratePillSegments(out).map(s => `${s.label} ${s.text}`)).toEqual(['5ч 41%', 'Нед 12%']);
  });

  it('свежий чат без событий: окна целиком из снимка аккаунта', () => {
    const out = withAccountFallback([], [snap('2026-09-27T11:00:00Z', 'seven_day_opus', { utilization: 0.3 })], NOW);
    expect(ratePillSegments(out).map(s => s.text)).toEqual(['30%']);
  });

  it('процент из чата свежее снимка аккаунта', () => {
    const out = withAccountFallback(
      [win('five_hour', { utilization: 0.7, resetsAt: R5 })],
      [snap('2026-09-27T11:00:00Z', 'five_hour', { utilization: 0.4, resetsAt: R5 })],
      NOW,
    );
    expect(ratePillSegments(out)[0]).toMatchObject({ text: '70%', level: 'warn' });
  });

  it('событие чата из сброшенного окна не перебивает свежий снимок аккаунта', () => {
    const out = withAccountFallback(
      [win('five_hour', { utilization: 0.9, resetsAt: '2026-09-27T11:00:00Z' })],
      [snap('2026-09-27T11:55:00Z', 'five_hour', { utilization: 0.05, resetsAt: '2026-09-27T16:00:00Z' })],
      NOW,
    );
    expect(ratePillSegments(out)[0]).toMatchObject({ text: '5%', level: 'normal' });
  });

  it('событие чата без процента и без сброса не затирает процент аккаунта', () => {
    const out = withAccountFallback(
      [win('five_hour', { status: 'allowed' })],
      [snap('2026-09-27T11:50:00Z', 'five_hour', { utilization: 0.41, resetsAt: R5 })],
      NOW,
    );
    expect(ratePillSegments(out)[0].text).toBe('41%');
  });

  it('окно без процента нигде → прочерк, а не 0%', () => {
    const out = withAccountFallback([win('five_hour', { status: 'allowed', resetsAt: R5 })], [], NOW);
    expect(ratePillSegments(out)).toEqual([{ limitType: 'five_hour', label: '5ч', text: '—', pct: null, level: 'normal' }]);
  });

  it('пустой набор → нет сегментов', () => {
    expect(ratePillSegments([])).toEqual([]);
    expect(withAccountFallback([], [], NOW)).toEqual([]);
  });

  it('danger: окно у предела помечено своим уровнем', () => {
    const windows = toRateWindows({
      five_hour: win('five_hour', { utilization: 0.5 }),
      seven_day: win('seven_day', { utilization: 1, isUsingOverage: true }),
      seven_day_opus: win('seven_day_opus', { utilization: 0.2 }),
    });
    expect(ratePillSegments(windows).find(s => s.limitType === 'seven_day')?.level).toBe('danger');
  });

  it('снимок аккаунта из сброшенного окна: старые «90%» не горят, окно остаётся прочерком «устарело»', () => {
    const out = withAccountFallback(
      [],
      [snap('2026-09-27T10:00:00Z', 'five_hour', { utilization: 0.9, resetsAt: '2026-09-27T11:00:00Z' }),
        snap('2026-09-27T11:50:00Z', 'seven_day', { utilization: 0.12, resetsAt: RW })],
      NOW,
    );
    expect(ratePillSegments(out).map(s => `${s.label} ${s.text}`)).toEqual(['5ч —', 'Нед 12%']);
    expect(ratePillSegments(out)[0]).toEqual({ limitType: 'five_hour', label: '5ч', text: '—', pct: null, level: 'normal', stale: true });
    expect(ratePillSegments(out)[1].stale).toBeFalsy();
    expect(out.some(w => w.level === 'danger')).toBe(false);
  });

  it('устаревшее окно берёт сброс последнего снимка; свежий снимок того же окна убирает пометку', () => {
    const stale = withAccountFallback(
      [],
      [snap('2026-09-27T09:00:00Z', 'five_hour', { utilization: 0.5, resetsAt: '2026-09-27T10:00:00Z' }),
        snap('2026-09-27T10:30:00Z', 'five_hour', { utilization: 0.8, resetsAt: '2026-09-27T11:00:00Z' })],
      NOW,
    );
    expect(stale).toHaveLength(1);
    expect(stale[0]).toMatchObject({ limitType: 'five_hour', stale: true, hasUtil: false, resetsAt: '2026-09-27T11:00:00Z' });

    const fresh = withAccountFallback(
      [],
      [snap('2026-09-27T10:30:00Z', 'five_hour', { utilization: 0.8, resetsAt: '2026-09-27T11:00:00Z' }),
        snap('2026-09-27T11:55:00Z', 'five_hour', { utilization: 0.05, resetsAt: '2026-09-27T16:00:00Z' })],
      NOW,
    );
    expect(ratePillSegments(fresh)).toEqual([{ limitType: 'five_hour', label: '5ч', text: '5%', pct: 5, level: 'normal' }]);
  });

  it('сброшенное служебное окно не превращается в устаревшее', () => {
    const out = withAccountFallback(
      [],
      [snap('2026-09-27T10:00:00Z', 'seven_day_overage_included', { utilization: 0.1, resetsAt: '2026-09-27T11:00:00Z' })],
      NOW,
    );
    expect(out).toEqual([]);
  });

  it('сброшенный снимок не глушит событие чата без процента: окно остаётся прочерком', () => {
    const out = withAccountFallback(
      [win('five_hour', { status: 'allowed' })],
      [snap('2026-09-27T10:00:00Z', 'five_hour', { utilization: 0.9, resetsAt: '2026-09-27T11:00:00Z' })],
      NOW,
    );
    expect(ratePillSegments(out)).toEqual([{ limitType: 'five_hour', label: '5ч', text: '—', pct: null, level: 'normal' }]);
  });

  it('не больше max окон в постоянном порядке, остальные считаются в more', () => {
    const windows = toRateWindows({
      extra_usage: win('extra_usage', { utilization: 0.9 }),
      seven_day_opus: win('seven_day_opus', { utilization: 0.3 }),
      seven_day_sonnet: win('seven_day_sonnet', { utilization: 0.1 }),
      seven_day: win('seven_day', { utilization: 0.12 }),
      five_hour: win('five_hour', { utilization: 0.41 }),
    });
    const v = ratePillVisible(windows);
    expect(v.segments.map(s => s.label)).toEqual(['5ч', 'Нед', 'Opus']);
    expect(v.more).toBe(2);
    expect(ratePillVisible(windows.slice(0, 2)).more).toBe(0);
  });

  it('короткие подписи: известные, per-model и эвристики', () => {
    expect(shortWindowLabel('five_hour')).toBe('5ч');
    expect(shortWindowLabel('seven_day')).toBe('Нед');
    expect(shortWindowLabel('seven_day_sonnet')).toBe('Sonnet');
    expect(shortWindowLabel('extra_usage')).toBe('Доп');
    expect(shortWindowLabel('rolling_5hr_v2')).toBe('5ч');
    expect(shortWindowLabel('nimbus_quill')).toBe('Др.');
  });
});

describe('worstWindow', () => {
  it('уровень тревоги важнее процента использования', () => {
    const windows = toRateWindows({
      warn: win('warn', { utilization: 0.9, status: 'allowed_warning' }),
      danger: win('danger', { utilization: 0.2, status: 'rejected' }),
    });
    expect(worstWindow(windows)?.limitType).toBe('danger');
  });

  it('при равном уровне побеждает большее использование', () => {
    const windows = toRateWindows({
      a: win('a', { utilization: 0.3 }),
      b: win('b', { utilization: 0.5 }),
    });
    expect(worstWindow(windows)?.limitType).toBe('b');
  });

  it('пустой список → undefined', () => {
    expect(worstWindow([])).toBeUndefined();
  });
});

describe('fmtReset', () => {
  // Полдень по локальному времени — чтобы «+5 часов» гарантированно оставались в тех же сутках
  const BASE = new Date(2026, 6, 3, 12, 0, 0);

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(BASE);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const plus = (ms: number) => new Date(BASE.getTime() + ms).toISOString();

  it('пустое или невалидное значение → пустая строка', () => {
    expect(fmtReset(undefined)).toBe('');
    expect(fmtReset('не дата')).toBe('');
  });

  it('срок в прошлом → «скоро»', () => {
    expect(fmtReset(plus(-60_000))).toBe('скоро');
  });

  it('меньше часа → только минуты', () => {
    expect(fmtReset(plus(45 * 60_000))).toBe('через 45м');
  });

  it('меньше 6 часов → часы и минуты', () => {
    expect(fmtReset(plus(90 * 60_000))).toBe('через 1ч 30м');
    expect(fmtReset(plus(5 * 3600_000 + 59 * 60_000))).toBe('через 5ч 59м');
  });

  it('больше 6 часов в тот же день → абсолютное время «в HH:MM»', () => {
    expect(fmtReset(plus(7 * 3600_000))).toBe('в 19:00');
  });

  it('другой день → дата и время', () => {
    const s = fmtReset(plus(30 * 3600_000));
    expect(s).toMatch(/^\d{1,2} .+, \d{2}:\d{2}$/); // «4 июл., 18:00»
    expect(s.startsWith('в ')).toBe(false);
  });
});

describe('windowLabel', () => {
  it('известные типы и эвристики по названию', () => {
    expect(windowLabel('five_hour')).toBe('5 часов');
    expect(windowLabel('seven_day')).toBe('Неделя');
    expect(windowLabel('rolling_5hr_v2')).toBe('5 часов');
    expect(windowLabel('weekly_all_models')).toBe('Неделя');
    expect(windowLabel('')).toBe('Лимит');
  });

  it('окна опроса /api/oauth/usage: per-model и перерасход', () => {
    expect(windowLabel('seven_day_opus')).toBe('Неделя · Opus');
    expect(windowLabel('seven_day_sonnet')).toBe('Неделя · Sonnet');
    expect(windowLabel('seven_day_fable')).toBe('Неделя · Fable');
    expect(windowLabel('extra_usage')).toBe('Перерасход · месяц');
  });

  it('незнакомое per-model окно получает читаемую подпись из ключа', () => {
    expect(windowLabel('seven_day_haiku')).toBe('Неделя · Haiku');
    expect(windowLabel('seven_day_new_model')).toBe('Неделя · New model');
  });
});

describe('overageLabel', () => {
  it('rejected → «перерасход недоступен»', () => {
    expect(overageLabel('rejected')).toBe('перерасход недоступен');
  });

  it('allowed_warning → «перерасход почти исчерпан»', () => {
    expect(overageLabel('allowed_warning')).toBe('перерасход почти исчерпан');
  });

  it('неизвестный статус или его отсутствие → «перерасход ограничен», сырой статус не утекает', () => {
    expect(overageLabel('some_new_status')).toBe('перерасход ограничен');
    expect(overageLabel(undefined)).toBe('перерасход ограничен');
  });
});

describe('latestWithUtilization', () => {
  const snap = (timestamp: string, utilization?: number, source?: UsageSnapshot['source']): UsageSnapshot =>
    ({ timestamp, limitType: 'five_hour', utilization, source });

  it('пустой список или снимки без процента → null', () => {
    expect(latestWithUtilization([])).toBeNull();
    expect(latestWithUtilization([snap('2026-07-29T10:00:00Z')])).toBeNull();
  });

  it('выбирает самый свежий снимок с процентом, игнорируя resets-only', () => {
    const out = latestWithUtilization([
      snap('2026-07-29T10:00:00Z', 0.3, 'oauth'),
      snap('2026-07-29T12:00:00Z'),            // свежее, но без utilization — не считается
      snap('2026-07-29T11:00:00Z', 0.4, 'probe'),
    ]);
    expect(out?.source).toBe('probe');
    expect(out?.timestamp).toBe('2026-07-29T11:00:00Z');
  });

  it('битый timestamp пропускается', () => {
    const out = latestWithUtilization([
      snap('не дата', 0.9, 'turn'),
      snap('2026-07-29T10:00:00Z', 0.3, 'oauth'),
    ]);
    expect(out?.source).toBe('oauth');
  });
});

describe('snapshotFreshnessLabel', () => {
  const NOW = new Date('2026-07-29T12:00:00Z').getTime();
  const ago = (ms: number) => new Date(NOW - ms).toISOString();

  it('невалидный timestamp → null', () => {
    expect(snapshotFreshnessLabel('turn', 'не дата', NOW)).toBeNull();
  });

  it('моложе минуты → «только что», с источником и без', () => {
    expect(snapshotFreshnessLabel('turn', ago(20_000), NOW)).toBe('Живой ход · только что');
    expect(snapshotFreshnessLabel(null, ago(20_000), NOW)).toBe('только что');
  });

  it('минуты → «N мин назад» с ярлыком каждого источника', () => {
    expect(snapshotFreshnessLabel('turn', ago(3 * 60_000), NOW)).toBe('Живой ход · 3 мин назад');
    expect(snapshotFreshnessLabel('probe', ago(3 * 60_000), NOW)).toBe('Пинг · 3 мин назад');
    expect(snapshotFreshnessLabel('oauth', ago(3 * 60_000), NOW)).toBe('OAuth-опрос · 3 мин назад');
  });

  it('60+ минут → «N ч назад»; без источника — только возраст', () => {
    expect(snapshotFreshnessLabel('probe', ago(125 * 60_000), NOW)).toBe('Пинг · 2 ч назад');
    expect(snapshotFreshnessLabel(null, ago(125 * 60_000), NOW)).toBe('2 ч назад');
    expect(snapshotFreshnessLabel(undefined, ago(90 * 60_000), NOW)).toBe('1 ч назад');
  });

  it('неизвестный источник не утекает в подпись — только возраст', () => {
    expect(snapshotFreshnessLabel('что-то-новое' as UsageSnapshot['source'], ago(5 * 60_000), NOW)).toBe('5 мин назад');
  });

  it('метка в будущем (расхождение часов) → «только что», не отрицательный возраст', () => {
    expect(snapshotFreshnessLabel('probe', ago(-30_000), NOW)).toBe('Пинг · только что');
  });
});

describe('кольцевая пилюля шапки: текст худшего показателя', () => {
  const c = (label: string, pct: number | null, level: RingPillCandidate['level'] = 'normal', stale?: boolean): RingPillCandidate =>
    ({ label, text: pct === null ? '—' : `${pct}%`, pct, level, stale });

  it('уровень важнее процента: warn 61% бьёт normal 90%', () => {
    expect(worstRingCandidate([c('Ctx', 90), c('5ч', 61, 'warn')])?.label).toBe('5ч');
  });

  it('при равном уровне — больший процент', () => {
    expect(ringPillHead({ pct: 40, level: 'normal' }, [c('5ч', 55), c('Нед', 12)]))
      .toEqual({ label: '5ч', text: '55%', level: 'normal', kind: 'value' });
  });

  it('кандидат без процента проигрывает любому с процентом того же уровня', () => {
    expect(worstRingCandidate([c('5ч', null), c('Нед', 0)])?.label).toBe('Нед');
  });

  it('полная ничья — по порядку колец: контекст, затем окна', () => {
    expect(ringPillHead({ pct: 30, level: 'normal' }, [c('5ч', 30), c('Нед', 30)])?.label).toBe('Ctx');
    expect(worstRingCandidate([c('5ч', 30), c('Нед', 30)])?.label).toBe('5ч');
  });

  it('окно вне колец (Opus 95%) тоже кандидат', () => {
    const windows = toRateWindows({
      five_hour: win('five_hour', { utilization: 0.41 }),
      seven_day: win('seven_day', { utilization: 0.12 }),
      seven_day_opus: win('seven_day_opus', { utilization: 0.95 }),
    });
    expect(ringPillHead({ pct: 62, level: 'normal' }, windowCandidates(windows)))
      .toEqual({ label: 'Opus', text: '95%', level: 'warn', kind: 'value' });
  });

  it('перерасход: текст «100%+» окна danger', () => {
    const windows = toRateWindows({ seven_day: win('seven_day', { utilization: 1, isUsingOverage: true }) });
    expect(ringPillHead({ pct: 92, level: 'danger' }, windowCandidates(windows))?.text).toBe('100%+');
  });

  it('контекст danger перебивает окна в норме', () => {
    expect(ringPillHead({ pct: 92, level: 'danger' }, [c('5ч', 81)]))
      .toEqual({ label: 'Ctx', text: '92%', level: 'danger', kind: 'value' });
  });

  it('свежесжатый контекст: «Ctx ✦», пока окна спокойны', () => {
    expect(ringPillHead({ level: 'normal', fresh: true }, [c('5ч', 41)]))
      .toEqual({ label: 'Ctx', text: '✦', level: 'normal', kind: 'fresh' });
  });

  it('свежесжатый контекст уступает тревожному окну', () => {
    expect(ringPillHead({ level: 'normal', fresh: true }, [c('5ч', 41), c('Нед', 70, 'warn')]))
      .toEqual({ label: 'Нед', text: '70%', level: 'warn', kind: 'value' });
  });

  it('только контекст (нет окон)', () => {
    expect(ringPillHead({ pct: 62, level: 'normal' }, [])?.text).toBe('62%');
  });

  it('только лимиты (оценки контекста нет)', () => {
    expect(ringPillHead(null, [c('5ч', 81, 'warn'), c('Нед', 34)])?.label).toBe('5ч');
  });

  it('процентов нет ни у кого → «в норме»; все окна устарели → «—»', () => {
    expect(ringPillHead(null, [c('5ч', null), c('Нед', null)])).toEqual({ label: '', text: 'в норме', level: 'normal', kind: 'calm' });
    expect(ringPillHead(null, [c('5ч', null, 'normal', true)])).toEqual({ label: '', text: '—', level: 'normal', kind: 'unknown' });
  });

  it('нечего показывать → null', () => {
    expect(ringPillHead(null, [])).toBeNull();
  });

  it('провайдер: квота GLM под своим именем соревнуется с контекстом', () => {
    expect(ringPillHead({ pct: 20, level: 'normal' }, [c('GLM', 34)])?.label).toBe('GLM');
    expect(ringPillHead({ pct: 20, level: 'normal' }, [c('GLM', 97, 'danger')]))
      .toEqual({ label: 'GLM', text: '97%', level: 'danger', kind: 'value' });
  });

  it('провайдер: тревожный денежный баланс без процента бьёт контекст в норме', () => {
    const money: RingPillCandidate = { label: '', text: '0.80 USD', pct: null, level: 'warn' };
    expect(ringPillHead({ pct: 62, level: 'normal' }, [money]))
      .toEqual({ label: '', text: '0.80 USD', level: 'warn', kind: 'value' });
  });

  it('DeepSeek в начале сессии (5.8b): баланс в норме, оценки контекста нет → баланс, не «—»', () => {
    const money: RingPillCandidate = { label: '', text: '4.20 USD', pct: null, level: 'normal' };
    expect(ringPillHead(null, [], money))
      .toEqual({ label: '', text: '4.20 USD', level: 'normal', kind: 'value' });
    // Есть что сказать и без него — idle не перебивает
    expect(ringPillHead({ pct: 62, level: 'normal' }, [], money)?.text).toBe('62%');
  });
});

describe('кольцевая пилюля шапки: много окон', () => {
  // Четыре окна: два в кольцах (5ч, Нед), два вне колец (Opus, Sonnet)
  const windows = toRateWindows({
    five_hour: win('five_hour', { utilization: 0.41 }),
    seven_day: win('seven_day', { utilization: 0.12 }),
    seven_day_opus: win('seven_day_opus', { utilization: 0.3 }),
    seven_day_sonnet: win('seven_day_sonnet', { utilization: 0.97 }),
  });

  it('колец не больше трёх: под окна — ровно RING_WINDOWS, первые по порядку колец', () => {
    const { segments } = ratePillVisible(windows, RING_WINDOWS);
    expect(RING_WINDOWS).toBe(2);                      // + внешнее кольцо контекста = 3
    expect(segments.map(s => s.label)).toEqual(['5ч', 'Нед']);
  });

  it('на пилюле ровно один худший показатель, без счётчика «+N»', () => {
    const head = ringPillHead({ pct: 62, level: 'normal' }, windowCandidates(windows));
    expect(head).toEqual({ label: 'Sonnet', text: '97%', level: 'warn', kind: 'value' });
    expect(Object.keys(head!)).not.toContain('more');
    expect(head!.text).not.toMatch(/\+\d/);
  });

  it('тон обводки — худшее окно, даже если оно вне колец', () => {
    expect(worstWindow(windows)?.limitType).toBe('seven_day_sonnet');
    expect(worstWindow(windows)?.level).toBe('warn');
  });
});

describe('длина дуги кольца', () => {
  // Окружности колец иконки 20×20 (r 3 и 9): inner — самое короткое, outer — самое длинное
  const L = 2 * Math.PI * 3;
  const OUTER = 2 * Math.PI * 9;
  it('0 и нет процента — без дуги', () => {
    expect(ringArcLength(0, L)).toBe(0);
    expect(ringArcLength(null, L)).toBe(0);
  });
  it('малая доля не короче 3px', () => {
    expect(ringArcLength(1, L)).toBe(3);
  });
  it('99% не дотягивает до полного круга, 100% — полный', () => {
    expect(ringArcLength(99, L)).toBeCloseTo(L - 1.5);
    expect(ringArcLength(100, L)).toBeCloseTo(L);
  });
  it('середина — пропорционально', () => {
    expect(ringArcLength(50, OUTER)).toBeCloseTo(OUTER / 2);
  });
  it('на outer 1% тоже тянется до 3px, 99% — до L − 1.5', () => {
    expect(ringArcLength(1, OUTER)).toBe(3);
    expect(ringArcLength(99, OUTER)).toBeCloseTo(OUTER - 1.5);
  });
});
