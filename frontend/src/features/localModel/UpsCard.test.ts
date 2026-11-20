// Тесты карточки «ИБП» вкладки «Стенды»: плашка питания по уровням бэкенда, сводка,
// плитки (значения, единицы, окраска), молчащий ИБП и встраивание во вкладку (farm.ups).
// Окружение node: SSR — renderToStaticMarkup (паттерн FarmSummaryCard.test.ts).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { HardwareFarm, HardwareUps } from './hardwareTypes';
import { UpsCard, upsPill, upsSummary, upsTiles } from './UpsCard';
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

describe('upsTiles', () => {
  it('четыре плитки с единицами; от батареи — красится сеть, заряд — нет', () => {
    const t = upsTiles(ups({ loadLevel: 'warn', powerLevel: 'warn', power: 'battery', inputV: 0 }));
    expect(t.map(x => [x.name, x.unit, x.value, x.level])).toEqual([
      ['Нагрузка', '%', 22, 'warn'],
      ['Заряд', '%', 100, 'ok'],
      ['Сеть', 'В', 0, 'warn'],
      ['Батарея', 'В', 54.1, 'ok'],
    ]);
    expect(t[2].detail).toBe('49,9 Гц');
    expect(t[3].detail).toBe('номинал 48 В');
  });
  it('батарея садится — заряд красный', () => {
    expect(upsTiles(ups({ power: 'lowBattery', powerLevel: 'danger' }))[1].level).toBe('danger');
  });
  it('нет значения — «нет данных», а не 0', () => {
    expect(upsTiles(ups({ chargePerc: null }))[1].sensor).toBe('nodata');
  });
});

describe('UpsCard', () => {
  it('рендерит шапку, плашку и значения', () => {
    const html = renderToStaticMarkup(createElement(UpsCard, { ups: ups(), isMobile: true }));
    expect(html).toContain('ИБП · Raskat');
    expect(html).toContain('от сети');
    expect(html).toContain('22 %');
    expect(html).toContain('54,1 В');
    expect(html).toContain('aria-labelledby="ups-card-title"');
    expect(html).toContain('role="status"');
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
