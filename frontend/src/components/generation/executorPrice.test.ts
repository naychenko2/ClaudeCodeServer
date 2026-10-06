import { describe, expect, it } from 'vitest';
import { badgeTone, rowPriceShort } from './ExecutorList';

describe('цена и тон строки исполнителя (контракт ExecutorRowDto)', () => {
  it('короткая цена собирается из полей; подпись price — только запасной вариант', () => {
    expect(rowPriceShort({ price: '$0.04 / шт.', free: true })).toBe('бесплатно');
    expect(rowPriceShort({ price: 'x', free: false, amount: 0.04, unit: 'usd' })).toBe('$0.04');
    expect(rowPriceShort({ price: 'x', free: false, amount: 2, unit: 'credits' })).toBe('2 кр.');
    expect(rowPriceShort({ price: 'x', free: false, amount: 5, unit: 'rub' })).toBe('5 ₽');
    expect(rowPriceShort({ price: 'кредиты' })).toBe('кредиты');
  });

  it('тоны контракта good/warn переводятся в тоны кита', () => {
    expect(badgeTone('good')).toBe('success');
    expect(badgeTone('warn')).toBe('warning');
    expect(badgeTone('info')).toBe('info');
    expect(badgeTone(undefined)).toBe('neutral');
  });
});
