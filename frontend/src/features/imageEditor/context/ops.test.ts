import { describe, expect, it } from 'vitest';
import { pickOp } from '../format';
import {
  activeChoice, DEFAULT_CHOICE, footPrice, isOneVariant, launchMarks, modeOp, opBlockReason, PANEL_OPS, queueText, quickOf,
  resolveOp, runVerb,
} from './ops';

const STATES: [boolean, boolean][] = [[false, false], [true, false], [true, true]];

describe('«Авто» — ровно pickOp', () => {
  it.each(STATES)('картинка %s, отметки %s', (hasImage, hasMask) => {
    expect(resolveOp('auto', hasImage, hasMask)).toBe(pickOp(hasImage, hasMask));
    expect(opBlockReason('auto', hasImage, hasMask)).toBe('');
  });

  it('явная операция идёт как есть', () => {
    expect(resolveOp('upscale', true, true)).toBe('upscale');
  });
});

describe('операции и причины', () => {
  it('без картинки — только «Авто» и «По тексту»', () => {
    const open = PANEL_OPS.filter(o => !opBlockReason(o.op, false, false)).map(o => o.op);
    expect(open).toEqual(['auto', 'generate']);
    expect(opBlockReason('removeBackground', false, false)).toBe('Сначала выберите картинку в ленте или загрузите её');
  });

  it('«По отмеченному» ждёт отметки', () => {
    expect(opBlockReason('inpaint', true, false)).toBe('Отметьте место кистью в редакторе картинки');
    expect(opBlockReason('inpaint', true, true)).toBe('');
  });

  it('операции без промпта идут быстрым путём, один вариант — у трёх из них', () => {
    expect(quickOf('outpaint')).toBe('outpaint');
    expect(quickOf('upscale')).toBe('upscale');
    expect(quickOf('edit')).toBeNull();
    expect(quickOf('generate')).toBeNull();
    expect(isOneVariant('removeBackground')).toBe(true);
    expect(isOneVariant('outpaint')).toBe(false);
    expect(runVerb('edit')).toBe('Изменить');
  });
});

describe('операция запуска с режимом', () => {
  it('«Изменить» сам становится инпейнтом при отметках; причина — у «Изменить»', () => {
    expect(modeOp('edit', true, false)).toEqual({ op: 'edit', reason: '' });
    expect(modeOp('edit', true, true)).toEqual({ op: 'inpaint', reason: '' });
    expect(modeOp('inpaint', true, false)).toEqual({ op: 'edit', reason: '' });
    expect(modeOp('edit', false, false).reason).toBe('Сначала выберите картинку в ленте или загрузите её');
  });

  it('прочие операции — как выбраны, с причиной своей операции', () => {
    expect(modeOp('upscale', true, true)).toEqual({ op: 'upscale', reason: '' });
    expect(modeOp('removeBackground', false, false).reason).toBe('Сначала выберите картинку в ленте или загрузите её');
  });
});

describe('выбор, с которым идёт запуск', () => {
  it('без режима — выбор вкладки по умолчанию', () => {
    expect(activeChoice('ops-test-p')).toEqual(DEFAULT_CHOICE);
  });
});

describe('пометки запуска', () => {
  const mask = { type: 'mask' };
  const arrow = { type: 'arrow' };
  it('при «Вся картинка» маска не уходит, стрелки остаются', () => {
    expect(launchMarks([mask, arrow], true)).toEqual([arrow]);
    expect(launchMarks([mask, arrow], false)).toEqual([mask, arrow]);
  });
});

describe('цена низа в две строки', () => {
  it('платная: итог и короткая расшифровка', () => {
    expect(footPrice({ amount: 0.08, unit: 'usd', approx: true }, 2)).toEqual(['≈ $0.08', '2 × $0.04']);
    expect(footPrice({ amount: 4, unit: 'credits', approx: false }, 2)).toEqual(['4 кредита', '2 × 2 кр.']);
  });

  it('бесплатная: время и очередь GPU; очередь бейджем, только непустая', () => {
    expect(footPrice({ amount: 0, unit: 'free', approx: false, etaSeconds: 30, queueLength: 2 }, 1))
      .toEqual(['Бесплатно', '≈ 30 с · очередь GPU: 2']);
    expect(queueText({ unit: 'free', queueLength: 2 })).toBe('GPU: перед вами 2 в очереди');
    expect(queueText({ unit: 'free', queueLength: 0 })).toBeUndefined();
    expect(queueText({ unit: 'usd', queueLength: 3 })).toBeUndefined();
  });

  it('цена не пришла', () => {
    expect(footPrice(null, 2)[0]).toBe('Цена уточняется');
  });
});
