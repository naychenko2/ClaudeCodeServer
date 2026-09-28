import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { RateLimitInfo, UsageSnapshot } from '../../types';
import { toRateWindows, worstWindow, fmtReset, windowLabel, latestPerWindow, latestWithUtilization, snapshotFreshnessLabel, overageLabel, withAccountFallback, ratePillSegments, ratePillCompact, ratePillVisible, ratePillMoreText, shortWindowLabel } from '../rateLimit';

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
    expect(ratePillCompact(windows)).toEqual({ head: { limitType: 'five_hour', label: '5ч', text: '41%', pct: 41, level: 'normal' }, more: 2 });
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

  it('пустой набор → нет сегментов и нет сжатой формы', () => {
    expect(ratePillSegments([])).toEqual([]);
    expect(ratePillCompact([])).toBeNull();
    expect(withAccountFallback([], [], NOW)).toEqual([]);
  });

  it('danger: сжатая форма показывает окно у предела, перерасход помечен «+»', () => {
    const windows = toRateWindows({
      five_hour: win('five_hour', { utilization: 0.5 }),
      seven_day: win('seven_day', { utilization: 1, isUsingOverage: true }),
      seven_day_opus: win('seven_day_opus', { utilization: 0.2 }),
    });
    expect(ratePillCompact(windows)).toEqual({ head: { limitType: 'seven_day', label: 'Нед', text: '100%+', pct: 100, level: 'danger' }, more: 2 });
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

  it('хвост «+N» отделён отступом, в том числе после перерасхода', () => {
    expect(ratePillMoreText(2)).toBe(' +2');
    expect(ratePillMoreText(0)).toBe('');
    const windows = toRateWindows({
      five_hour: win('five_hour', { utilization: 0.5 }),
      seven_day: win('seven_day', { utilization: 1, isUsingOverage: true }),
      seven_day_opus: win('seven_day_opus', { utilization: 0.2 }),
    });
    const c = ratePillCompact(windows)!;
    expect(c.head.text + ratePillMoreText(c.more)).toBe('100%+ +2');
  });

  it('десктоп: не больше трёх окон в постоянном порядке, остальные — счётчиком', () => {
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
