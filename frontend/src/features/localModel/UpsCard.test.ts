// Тесты карточки «ИБП» вкладки «Стенды» (макет v4, §«Компактный ИБП»): плашка питания
// по уровням бэкенда, сводка, одна KPI «Заряд», одна полоса из трёх ячеек, молчащий ИБП,
// отдельная секция «Динамика за 15 мин» (sibling карточки) и её ряды.
// Окружение node: SSR — renderToStaticMarkup (паттерн FarmSummaryCard.test.ts).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { HardwareFarm, HardwareUps } from './hardwareTypes';
import { UpsCard, UpsDynamicsSection, upsPill, upsSummary } from './UpsCard';
import { HardwareTab } from './HardwareTab';

const SINCE = 1763650000000;

function hms(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// Живой снимок Raskat HIL-3000VA+ (20.11.2026), уровни — как отдаёт бэкенд.
function ups(over: Partial<HardwareUps> = {}): HardwareUps {
  return {
    title: 'Raskat', status: 'ok', since: null, level: 'ok', power: 'line',
    powerLevel: 'ok', loadLevel: 'ok',
    loadPerc: 22, loadW: 528, chargePerc: 100, batteryV: 54.1, batteryNominalV: 48,
    inputV: 222, inputHz: 49.9, outputV: 222, tempC: 28,
    loadSpark15m: [20, 22, null, 22], chargeSpark15m: [100, 100], inputVSpark15m: [221, 222],
    batteryDeciVSpark15m: [541, 540],
    ...over,
  };
}

describe('upsPill', () => {
  it('от сети — нейтральная плашка', () => {
    expect(upsPill(ups())).toEqual({ label: 'от сети', level: 'ok' });
  });
  it('от батареи и батарея садится — по powerLevel', () => {
    expect(upsPill(ups({ power: 'battery', powerLevel: 'warn', level: 'warn' }))).toEqual({ label: 'от батареи', level: 'warn' });
    expect(upsPill(ups({ power: 'lowBattery', powerLevel: 'danger', level: 'danger' }))).toEqual({ label: 'батарея садится', level: 'danger' });
  });
  it('от сети с высокой нагрузкой — плашка нагрузки', () => {
    expect(upsPill(ups({ loadLevel: 'warn', level: 'warn' })).label).toBe('высокая нагрузка');
    expect(upsPill(ups({ loadLevel: 'danger', level: 'danger' })).label).toBe('перегрузка');
  });
  it('молчащий ИБП — «ИБП молчит», а не «от сети»', () => {
    expect(upsPill(ups({ status: 'down', since: SINCE })).label).toBe('ИБП молчит');
  });
});

describe('upsSummary', () => {
  it('выход равен входу — только температура', () => {
    expect(upsSummary(ups())).toBe('внутри 28 °C');
  });
  it('выход отличается от входа больше чем на 5 В — показан', () => {
    expect(upsSummary(ups({ inputV: 190, outputV: 220 }))).toBe('выход 220 В · внутри 28 °C');
  });
  it('молчит — время начала', () => {
    expect(upsSummary(ups({ status: 'stale', since: SINCE }))).toBe(`не отвечает с ${hms(SINCE)}, показаны последние данные`);
  });
});

describe('UpsCard', () => {
  const render = (u: HardwareUps) => renderToStaticMarkup(createElement(UpsCard, { ups: u, isMobile: false }));

  it('шапка, плашка role=status и сводка', () => {
    const html = render(ups());
    expect(html).toContain('ИБП · Raskat');
    expect(html).toContain('от сети');
    expect(html).toContain('role="status"');
    expect(html).toContain('aria-labelledby="ups-card-title"');
    expect(html).toContain('внутри 28 °C');
  });

  it('ровно одна KPI «Заряд» и одна полоса из трёх ячеек', () => {
    const html = render(ups());
    expect((html.match(/Заряд/g) ?? []).length).toBe(1);
    expect((html.match(/repeat\(3, minmax\(0, 1fr\)\)/g) ?? []).length).toBe(1);
    expect(html).toContain('Нагрузка');
    expect(html).toContain('Сеть');
    expect(html).toContain('Батарея');
    expect(html).toContain('22 %');
    expect(html).toContain('222 В');
    expect(html).toContain('54,1 В');
    expect(html).toContain('по напряжению');
    expect(html).toContain('≈ 528 Вт');
    expect(html).toContain('49,9 Гц');
    expect(html).toContain('номинал 48 В');
  });

  it('заряд 0 — «0 %», а не «нет данных»', () => {
    const html = render(ups({ chargePerc: 0 }));
    expect(html).toContain('0 %');
    expect(html).not.toContain('нет данных');
  });

  it('chargePerc null — «нет данных», а не 0 (ловит замену null нулём)', () => {
    const html = render(ups({ chargePerc: null }));
    expect(html).toContain('нет данных');
    expect(html).not.toContain('0 %');
  });

  it('lowBattery — заливка KPI по powerLevel; обычный заряд без заливки', () => {
    const html = render(ups({ power: 'lowBattery', powerLevel: 'danger', chargePerc: 18 }));
    expect(html).toContain('var(--c-danger-bg)');
    expect(html).toContain('var(--c-danger-border)');
    expect(html).toContain('var(--c-danger-text)');
    expect(html).toContain('18 %');
    // Обычный заряд при питании от сети: danger-заливки нет
    expect(render(ups())).not.toContain('var(--c-danger-bg)');
  });

  it('lowBattery с powerLevel=warn — жёлтый текст значения KPI', () => {
    expect(render(ups({ power: 'lowBattery', powerLevel: 'warn', chargePerc: 64 }))).toContain('var(--c-warning-text)');
  });

  it('молчащий ИБП: значения textMuted, заливки сняты, плашка молчания остаётся', () => {
    const html = render(ups({
      status: 'stale', since: SINCE, power: 'lowBattery', powerLevel: 'danger',
      loadLevel: 'danger', loadPerc: 96,
    }));
    expect(html).toContain('ИБП молчит');
    expect(html).toContain('role="status"');
    // тревожные заливки KPI/полосы сняты (danger полностью; warn остаётся только на плашке молчания)
    expect(html).not.toContain('var(--c-danger-bg)');
    expect((html.match(/var\(--c-warning-bg\)/g) ?? []).length).toBe(1);
    expect(html).toContain('var(--c-text-muted)');
    // значения не выдаются за текущую тревогу: цвет значения — не dangerText
    expect(html).not.toContain('var(--c-danger-text)');
  });

  it('секция динамики не вложена в UpsCard (ловит вложение DisclosureSection)', () => {
    const html = render(ups());
    expect(html).not.toContain('ups-dynamics');
    expect(html).not.toContain('Динамика за 15 мин');
  });
});

describe('UpsDynamicsSection', () => {
  const render = (u: HardwareUps, open = false) => renderToStaticMarkup(createElement(UpsDynamicsSection, { ups: u, open, onToggle: () => {}, isMobile: false }));

  it('open=false: aria-expanded="false", тело скрыто, четыре подписи рядов смонтированы', () => {
    const html = render(ups());
    expect(html).toContain('id="ups-dynamics"');
    expect(html).toContain('aria-expanded="false"');
    expect(html).toContain('display:none');
    expect(html).toContain('Динамика за 15 мин');
    expect(html).toContain('Заряд');
    expect(html).toContain('Нагрузка');
    expect(html).toContain('Сеть');
    expect(html).toContain('Батарея');
  });

  it('open=true: тело раскрыто', () => {
    expect(render(ups(), true)).toContain('display:block');
  });

  it('сетки динамики — две колонки minmax(0, 1fr)', () => {
    expect(render(ups())).toContain('repeat(2, minmax(0, 1fr))');
  });

  it('молчит — линия textMuted; пустой ряд — подложка', () => {
    const html = render(ups({ status: 'stale', since: SINCE }));
    expect(html).toContain('stroke="var(--c-text-muted)"');
    const empty = render(ups({ chargeSpark15m: [null, null] }));
    expect(empty).toContain('var(--c-bg-inset)');
  });
});

describe('HardwareTab + ИБП', () => {
  const farm = (u: HardwareUps | null | undefined): HardwareFarm => ({ at: 0, level: 'ok', farmNodes: [], ups: u });
  const render = (f: HardwareFarm) => renderToStaticMarkup(createElement(HardwareTab, {
    farm: f, farmLoading: false, selected: null, onSelect: () => {}, report: null, loading: false,
    stats: null, media: null, isMobile: false, window: '15m', onWindowChange: () => {},
  }));
  it('ИБП настроен — карточка есть', () => {
    expect(render(farm(ups()))).toContain('ИБП · Raskat');
  });
  it('ИБП не настроен или старый ответ без поля — карточки нет', () => {
    expect(render(farm(null))).not.toContain('ИБП ·');
    expect(render(farm(undefined))).not.toContain('ИБП ·');
  });
});
