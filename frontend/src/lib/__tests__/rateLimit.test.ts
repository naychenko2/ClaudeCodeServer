import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { RateLimitInfo, UsageSnapshot } from '../../types';
import { toRateWindows, worstWindow, fmtReset, windowLabel, latestPerWindow } from '../rateLimit';

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
});
