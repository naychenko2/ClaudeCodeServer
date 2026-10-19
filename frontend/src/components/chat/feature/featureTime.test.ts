// Время в режиме фичи (featureTime): ISO → метки в МЕСТНОМ поясе.
//
// Окружение node (без jsdom): чистые функции, без React. Ключевой инвариант —
// метки считаются ЛОКАЛЬНЫМИ геттерами (getHours/getDate/getMonth), а не getUTC*:
// бэк отдаёт UTC-ISO, а макет считает время в местном поясе человека.
//
// Тесты НЕ зависят от пояса машины: ожидаемое вычисляем теми же локальными
// геттерами от той же Date (CI в UTC, разработка в МСК). Граничный случай 23:30Z
// дополнительно фиксируем через Intl с ЯВНЫМ timeZone (UTC → 18.10, МСК → 19.10) —
// это и есть «поясозависимость», которую ловим: в МСК тот же момент уже следующий день.

import { describe, expect, it } from 'vitest';
import { hhmm, dateTimeLabel, dateOnly } from './featureTime';

// Ожидаемая «dd.mm» теми же локальными геттерами от той же Date (НЕ через dateOnly:
// мутация dateOnly должна краснить тест, а не сдвигать ожидание).
function localDateOnly(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
}

// Календарная дата момента в ЯВНОМ timeZone (en-CA → YYYY-MM-DD; не зависит от
// пояса машины, в отличие от локальных геттеров).
function calendarDateIn(iso: string, timeZone: string): string {
  const d = new Date(iso);
  return new Intl.DateTimeFormat('en-CA', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
  }).format(d);
}

describe('dateOnly: локальная календарная дата', () => {
  it('«dd.mm» — локальная дата (не UTC): совпадает с локальными геттерами', () => {
    const iso = '2026-10-18T23:30:00Z';
    expect(dateOnly(iso)).toBe(localDateOnly(iso));
  });

  it('граничный 23:30Z: в UTC календарная дата 18.10, в МСК (UTC+3) — 19.10', () => {
    const iso = '2026-10-18T23:30:00Z';
    // Один и тот же момент: в UTC это 18.10 (23:30), в МСК — уже 19.10 (02:30).
    // Intl с явным timeZone фиксирует оба конца независимо от пояса машины.
    expect(calendarDateIn(iso, 'UTC')).toBe('2026-10-18');
    expect(calendarDateIn(iso, 'Europe/Moscow')).toBe('2026-10-19');
    // dateOnly показывает ЛОКАЛЬНУЮ дату: ровно ту, что дают локальные геттеры
    // этой машины (в UTC-машине 18.10, в МСК-машине 19.10) — не «среднее» и не UTC.
    expect(dateOnly(iso)).toBe(localDateOnly(iso));
  });

  it('пустая/невалидная → null', () => {
    expect(dateOnly(null)).toBeNull();
    expect(dateOnly(undefined)).toBeNull();
    expect(dateOnly('')).toBeNull();
    expect(dateOnly('not-a-date')).toBeNull();
  });
});

describe('hhmm / dateTimeLabel: локальные метки (регрессия)', () => {
  it('hhmm — локальные часы:минуты', () => {
    const iso = '2026-10-18T23:30:00Z';
    const d = new Date(iso);
    expect(hhmm(iso)).toBe(`${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`);
  });

  it('dateTimeLabel — локальная дата-время', () => {
    const iso = '2026-10-18T23:30:00Z';
    const d = new Date(iso);
    const expected =
      `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')} ` +
      `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    expect(dateTimeLabel(iso)).toBe(expected);
  });
});
