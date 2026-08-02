import { describe, it, expect, vi, afterEach } from 'vitest';
import { formatPostTime, formatPostTimeFull } from '../postTime';

// Время «сейчас» фиксируем — иначе тест «сегодня/вчера» зависел бы от даты прогона
const NOW = new Date('2026-07-31T15:30:00');

const at = (iso: string) => new Date(iso).getTime();

describe('formatPostTime', () => {
  afterEach(() => { vi.useRealTimers(); });

  const freeze = () => { vi.useFakeTimers(); vi.setSystemTime(NOW); };

  it('совсем свежее — «только что»', () => {
    freeze();
    expect(formatPostTime(at('2026-07-31T15:29:30'))).toBe('только что');
  });

  it('в пределах часа — сколько минут назад', () => {
    freeze();
    expect(formatPostTime(at('2026-07-31T15:05:00'))).toBe('25 мин назад');
  });

  it('час назад и больше, но сегодня — только часы и минуты', () => {
    freeze();
    expect(formatPostTime(at('2026-07-31T09:05:00'))).toBe('09:05');
  });

  // Часы клиента могут уйти вперёд сервера: «-3 мин назад» читалось бы как поломка
  it('время из будущего относительным не подписываем', () => {
    freeze();
    expect(formatPostTime(at('2026-07-31T15:35:00'))).toBe('15:35');
  });

  it('вчера — с пометкой', () => {
    freeze();
    expect(formatPostTime(at('2026-07-30T22:40:00'))).toBe('вчера 22:40');
  });

  it('раньше в этом году — дата без года', () => {
    freeze();
    const s = formatPostTime(at('2026-03-05T14:00:00'))!;
    expect(s).toContain('5 мар');
    expect(s).toContain('14:00');
    expect(s).not.toContain('2026');
  });

  it('прошлый год — дата с годом', () => {
    freeze();
    expect(formatPostTime(at('2025-12-01T10:00:00'))).toContain('2025');
  });

  // Старая история поля не несёт — панель просто не рисует время
  it('пустое или битое значение — null', () => {
    expect(formatPostTime(undefined)).toBeNull();
    expect(formatPostTime(null)).toBeNull();
    expect(formatPostTime(NaN)).toBeNull();
  });
});

describe('formatPostTimeFull', () => {
  it('полная дата с месяцем словом', () => {
    expect(formatPostTimeFull(at('2026-07-31T15:30:00'))).toContain('июля');
  });

  it('пустое значение — null', () => {
    expect(formatPostTimeFull(undefined)).toBeNull();
  });
});
