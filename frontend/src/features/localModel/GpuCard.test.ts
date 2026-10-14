// Null-поля видеокарты в GpuCard: каждое по отдельности → «—»,
// без «0», без NaN/Infinity, полоса не ломается, memTotalMb=0 не даёт деления на ноль.
// Рендер через renderToStaticMarkup (node, без jsdom): GpuCard — чисто презентационный,
// хуков и эффектов нет.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { GpuCard } from './GpuCard';

// Базовая карта со всеми полями заполненными — для контрольных проверок.
const FULL = {
  index: 0,
  name: 'RTX 3090',
  utilPerc: 42,
  memUsedMb: 21934,
  memTotalMb: 24576,
  tempC: 67,
  powerW: 212,
  powerLimitW: 220,
};

function render(gpu: Partial<typeof FULL> & { index: number; name: string }) {
  return renderToStaticMarkup(
    createElement(GpuCard, { gpu: gpu as never, total: 2, kind: 'media' }),
  );
}

// === utilPerc = null ===

describe('utilPerc = null', () => {
  it('«Загрузка» → «—», не «0 %»', () => {
    const html = render({ ...FULL, utilPerc: null });
    // formatPerc(null) → «—»
    expect(html).toContain('—');
    // Не должно быть «0 %» (мутация: null → 0 даёт formatPerc(0/100) = «0 %»)
    expect(html).not.toContain('0 %');
  });

  it('полоса: width 0% (не NaN/Infinity)', () => {
    const html = render({ ...FULL, utilPerc: null });
    // Полоса: width: 0% (utilPerc=null → 0)
    expect(html).toMatch(/width:\s*0%/);
    // Не должно быть NaN или Infinity в width
    expect(html).not.toMatch(/width:\s*(NaN|Infinity)/);
  });
});

// === tempC = null ===

describe('tempC = null', () => {
  it('«Темп.» → «—», не «0 °C»', () => {
    const html = render({ ...FULL, tempC: null });
    // tempC=null → «—»
    expect(html).toContain('—');
    // Не должно быть «0 °C» (мутация: null → 0 даёт «0 °C»)
    expect(html).not.toContain('0 °C');
  });

  it('hot = false (не warning)', () => {
    const html = render({ ...FULL, tempC: null });
    // hot = (null != null && ...) = false → нет warning-цвета
    // C.warningText не должен появиться (он применяется только при hot=true)
    // Проверяем, что нет «0 °C» — это было бы признаком null→0
    expect(html).not.toContain('0 °C');
  });
});

// === memUsedMb = null ===

describe('memUsedMb = null', () => {
  it('«Память» → «—»', () => {
    const html = render({ ...FULL, memUsedMb: null });
    // formatGpuMem(null, 24576) → «—»
    expect(html).toContain('—');
    // Не должно быть «0/24 ГБ» (мутация: null → 0)
    expect(html).not.toContain('0/');
  });
});

// === memTotalMb = null ===

describe('memTotalMb = null', () => {
  it('«Память» → «—», в шапке нет ГБ', () => {
    const html = render({ ...FULL, memTotalMb: null });
    // formatGpuMem(21934, null) → «—»
    expect(html).toContain('—');
    // totalGb = null → в шапке нет «24 ГБ»
    expect(html).not.toContain('24 ГБ');
  });
});

// === memTotalMb = 0 (деление на ноль) ===

describe('memTotalMb = 0', () => {
  it('«Память» → «—», не Infinity/NaN', () => {
    const html = render({ ...FULL, memTotalMb: 0 });
    // formatGpuMem(21934, 0): totalMb=0, Number.isFinite(0)=true →
    // ruNum(21934/1024) / ruNum(0/1024) = «21,4/0 ГБ»
    // Но memTotalMb=0 → totalGb = Math.round(0/1024) = 0 → «0 ГБ» в шапке
    // Ключевое: нет деления на ноль в formatGpuMem (onumber.isFinite(0) = true,
    // 0/1024 = 0, ruNum(0) = «0») → «21,4/0 ГБ» — не Infinity
    expect(html).not.toContain('Infinity');
    expect(html).not.toContain('NaN');
  });
});

// === powerW = null ===

describe('powerW = null', () => {
  it('«Мощность» → «—»', () => {
    const html = render({ ...FULL, powerW: null });
    // formatPower(null, 220) → «—»
    expect(html).toContain('—');
    // Не должно быть «0 Вт» (мутация: null → 0)
    expect(html).not.toContain('0 Вт');
  });
});

// === powerLimitW = null ===

describe('powerLimitW = null', () => {
  it('«Мощность» → «212 Вт» (без лимита)', () => {
    const html = render({ ...FULL, powerLimitW: null });
    // formatPower(212, null) → «212 Вт»
    expect(html).toContain('212 Вт');
    // Не должно быть «212/0 Вт» (мутация: null → 0)
    expect(html).not.toContain('212/0 Вт');
  });
});

// === Все null одновременно ===

describe('все поля null', () => {
  it('карта рендерится, нет NaN/Infinity/«0 °C»', () => {
    const html = render({
      index: 0,
      name: 'RTX 3090',
      utilPerc: null,
      memUsedMb: null,
      memTotalMb: null,
      tempC: null,
      powerW: null,
      powerLimitW: null,
    });
    expect(html).not.toContain('NaN');
    expect(html).not.toContain('Infinity');
    expect(html).not.toContain('0 °C');
    // Имя карты и «карта 0 из 2» на месте
    expect(html).toContain('RTX 3090');
    expect(html).toContain('карта 0 из 2');
  });
});
