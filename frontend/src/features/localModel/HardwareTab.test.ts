// Тесты вкладки «Стенды» (Ф3): выбор узла по умолчанию (худший уровень, при равенстве —
// первый), счётчик «N показателей без данных», плитка «Плата» (датчика нет / нет данных /
// периферия), причина падения скорости, сборка вкладки: ферма сверху, движки узла, баннер
// молчащего узла, пустое состояние «Нет связи с узлом», отсутствие строк «Железо» /
// прежнего баннера и инвариант «ветвление по kind, а не по ключу плитки».
//
// Окружение node (без jsdom): renderToStaticMarkup + чтение исходников для сторожей.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import type { HardwareFarm, HardwareFarmNode, HardwareNode, HardwareReport } from './hardwareTypes';
import {
  farmPickNode, hwIcon, hwNoDataCount, hwNoDataCountLabel, hwThrottled, hwTileView,
} from './hardwareFormat';
import { HardwareTab, nodeEngineLine } from './HardwareTab';
import { HwTile } from './HwTile';
import { mockLocalModelMedia, mockLocalModelStats } from './localModel.fixtures';

const SINCE = 1763270000000;
// Старые строки собираем из частей: сторож-grep по исходникам не должен их находить.
const OLD_BANNER = ['Стенд', 'не отвечает'].join(' ');
const OLD_TAB = ['Же', 'лезо'].join('');
function hms(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function fnode(over: Partial<HardwareFarmNode>): HardwareFarmNode {
  return {
    key: 'x', title: 'x', roles: [], engines: [], media: false, status: 'ok', since: null, level: 'ok',
    hottest: null, gpus: [], uptimeS: null, cpuLoadPerc: null, ramUsedPerc: null, ...over,
  };
}

function farm(nodes: HardwareFarmNode[]): HardwareFarm {
  return { at: 1763280000000, level: 'ok', farmNodes: nodes };
}

const WORKER = fnode({ key: 'worker', title: 'worker' });
const AI = fnode({ key: 'ai', title: 'ai', roles: ['text'], engines: ['main'], level: 'warn' });
const VIDEO = fnode({ key: 'video', title: 'video', roles: ['media', 'text'], engines: ['gemma'], media: true, level: 'ok' });

function tile(over: Partial<HardwareNode>): HardwareNode {
  return {
    key: 'k', kind: 'vrm', name: 'Плитка', tempC: 50, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105,
    detail: {}, spark15m: [], ...over,
  };
}

function report(nodes: HardwareNode[], over: Partial<HardwareReport> = {}): HardwareReport {
  return { at: 1, status: 'ok', level: 'ok', gpus: [], nodes, caseFans: [], series: [], stepMs: 10000, ...over };
}

// === 1. Выбор узла ===
describe('farmPickNode', () => {
  it('по умолчанию — худший уровень', () => {
    expect(farmPickNode(farm([WORKER, AI, VIDEO]), null)).toBe('ai');
  });
  it('при равенстве уровней — первый по порядку фермы', () => {
    expect(farmPickNode(farm([WORKER, fnode({ key: 'a' }), fnode({ key: 'b' })]), null)).toBe('worker');
    expect(farmPickNode(farm([fnode({ key: 'a', level: 'danger' }), fnode({ key: 'b', level: 'danger' })]), null)).toBe('a');
  });
  it('выбор человека сильнее уровня; узел пропал из фермы — снова умолчание', () => {
    const f = farm([WORKER, AI, VIDEO]);
    expect(farmPickNode(f, 'video')).toBe('video');
    expect(farmPickNode(f, 'gone')).toBe('ai');
  });
  it('фермы нет — null', () => {
    expect(farmPickNode(null, 'ai')).toBeNull();
    expect(farmPickNode(farm([]), null)).toBeNull();
  });
});

// === 2. Счётчик «без данных» ===
describe('счётчик «N показателей без данных»', () => {
  const r = report([
    tile({ key: 'a', tempC: null, sensor: 'nodata', level: 'none' }),
    tile({ key: 'b', tempC: null, sensor: 'none', level: 'none' }),
    tile({ key: 'c', tempC: null, sensor: 'nodata', level: 'none' }),
    tile({ key: 'd' }),
  ]);
  it('считает только nodata, «датчика нет» не входит', () => {
    expect(hwNoDataCount(r)).toBe(2);
  });
  it('склонение', () => {
    expect(hwNoDataCountLabel(0)).toBe('');
    expect(hwNoDataCountLabel(1)).toBe('1 показатель без данных');
    expect(hwNoDataCountLabel(2)).toBe('2 показателя без данных');
    expect(hwNoDataCountLabel(5)).toBe('5 показателей без данных');
    expect(hwNoDataCountLabel(11)).toBe('11 показателей без данных');
    expect(hwNoDataCountLabel(21)).toBe('21 показатель без данных');
  });
});

// === 3. Плитка «Плата» ===
describe('плитка «Плата» (kind=board)', () => {
  const board = (over: Partial<HardwareNode>) => tile({ key: 'board', kind: 'board', name: 'Плата', warnC: 80, dangerC: 85, ...over });
  it('иконка Thermometer, подпись «на плате», периферия в деталях', () => {
    expect(hwIcon('board')).toBe('Thermometer');
    const [v] = hwTileView(report([board({ tempC: 52, detail: { peripheralC: 47.4 } })]));
    expect(v.icon).toBe('Thermometer');
    expect(v.sensorLabel).toBe('на плате');
    expect(v.detail).toBe('периферия 47 °C');
  });
  it('значение 27 °C и периферия 37 °C рисуются в плитке «Плата»', () => {
    const [v] = hwTileView(report([board({ tempC: 27, detail: { peripheralC: 37 } })]));
    const html = renderToStaticMarkup(createElement(HwTile, { view: v }));
    expect(html).toContain('Плата');
    expect(html).toContain('27 °C');
    expect(html).toContain('периферия 37 °C');
  });
  it('без peripheralC деталь пуста', () => {
    const [v] = hwTileView(report([board({})]));
    expect(v.detail).toBe('');
  });
  it('ветвление по kind, а не по ключу: «Процессор 2» (cpu-1) — как cpu', () => {
    const [v] = hwTileView(report([tile({ key: 'cpu-1', kind: 'cpu', name: 'Процессор 2', detail: { ccdC: [60, 61] } })]));
    expect(v.sensorLabel).toBe('общая');
    expect(v.detail).toBe('кристаллы 60 и 61');
  });
  it('none → «датчика нет», nodata → «нет данных»; «0 °C» не рисуется', () => {
    const none = hwTileView(report([board({ tempC: null, sensor: 'none', level: 'none' })]))[0];
    const nodata = hwTileView(report([board({ tempC: null, sensor: 'nodata', level: 'none' })]))[0];
    const hNone = renderToStaticMarkup(createElement(HwTile, { view: none }));
    const hNodata = renderToStaticMarkup(createElement(HwTile, { view: nodata }));
    expect(hNone).toContain('датчика нет');
    expect(hNodata).toContain('нет данных');
    expect(hNone).not.toMatch(/>\s*0 °C</);
    expect(hNodata).not.toMatch(/>\s*0 °C</);
  });
});

// === 4. Причина падения скорости ===
describe('hwThrottled', () => {
  const g = (thermal: boolean, powerCap: boolean) => ({ throttle: { thermal, powerCap } }) as HardwareReport['gpus'][number];
  it('перегрев важнее лимита мощности; ничего — null', () => {
    expect(hwThrottled({ gpus: [g(false, true), g(true, false)] })).toBe('thermal');
    expect(hwThrottled({ gpus: [g(false, true)] })).toBe('powerCap');
    expect(hwThrottled({ gpus: [g(false, false)] })).toBeNull();
    expect(hwThrottled({ gpus: [] })).toBeNull();
  });
});

// === 5. Вкладка целиком ===
const stats = mockLocalModelStats('working');
const media = mockLocalModelMedia('run');

function render(over: Partial<Parameters<typeof HardwareTab>[0]>): string {
  const base: Parameters<typeof HardwareTab>[0] = {
    farm: farm([WORKER, AI, VIDEO]), farmLoading: false, selected: 'ai', onSelect: () => {},
    report: report([tile({ key: 'vrm' })]), loading: false, stats, media: null, isMobile: false,
    window: '15m', onWindowChange: () => {},
  };
  return renderToStaticMarkup(createElement(HardwareTab, { ...base, ...over }));
}

describe('HardwareTab', () => {
  it('ферма сверху, у узла с engines — карточка vLLM, без media — без ComfyUI', () => {
    const html = render({});
    expect(html.indexOf('Ферма')).toBeGreaterThanOrEqual(0);
    expect(html.indexOf('Ферма')).toBeLessThan(html.indexOf('Стенд<'));
    expect(html).toContain('vLLM');
    expect(html).not.toContain('ComfyUI');
  });
  it('у video — обе карточки движка', () => {
    const html = render({ selected: 'video', media });
    expect(html).toContain('vLLM');
    expect(html).toContain('ComfyUI');
  });
  it('у worker (ни engines, ни media) карточек движка нет', () => {
    const html = render({ selected: 'worker', stats: null });
    expect(html).not.toContain('Движок');
  });
  it('строка движка в сводке — только у выбранного узла', () => {
    expect(nodeEngineLine(WORKER, null, null)).toBeUndefined();
    expect(nodeEngineLine(AI, stats, null)).toMatch(/ток\/с|нет данных/);
    expect(nodeEngineLine(VIDEO, null, media)).toContain('очередь');
  });
  it('молчащий узел с прежним отчётом — баннер «Узел ai не отвечает с HH:MM:SS»', () => {
    const silent = fnode({ ...AI, status: 'down', since: SINCE, level: 'ok' });
    const html = render({ farm: farm([WORKER, silent, VIDEO]), report: report([tile({ key: 'vrm' })], { status: 'stale', staleSince: SINCE }) });
    expect(html).toContain(`Узел ai не отвечает с ${hms(SINCE)}`);
    expect(html).not.toContain(OLD_BANNER);
  });
  it('молчащий узел: плашка «Стенд» не говорит «всё в норме» (как плашка сводки — «узел молчит»)', () => {
    const silent = fnode({ ...AI, status: 'down', since: SINCE, level: 'ok' });
    const html = render({ farm: farm([WORKER, silent, VIDEO]), report: report([tile({ key: 'vrm' })], { status: 'stale', staleSince: SINCE, level: 'ok' }) });
    expect(html).not.toContain('всё в норме');
  });
  it('down и отчёта нет ни разу — «Нет связи с узлом ai», без баннера и плиток', () => {
    const silent = fnode({ ...AI, status: 'down', since: SINCE, level: 'ok' });
    const html = render({ farm: farm([WORKER, silent, VIDEO]), report: null });
    expect(html).toContain('Нет связи с узлом ai');
    expect(html).not.toContain('Узел ai не отвечает');
    expect(html).not.toContain('Температуры');
  });
  it('счётчик «без данных» под плитками: две nodata и одна none → «2 показателя»', () => {
    const html = render({ report: report([
      tile({ key: 'a', tempC: null, sensor: 'nodata', level: 'none' }),
      tile({ key: 'b', tempC: null, sensor: 'none', level: 'none' }),
      tile({ key: 'c', tempC: null, sensor: 'nodata', level: 'none' }),
    ]) });
    expect(html).toContain('2 показателя без данных');
  });
  it('два сокета: «Процессор 1» ok и «Процессор 2» danger — красной бывает только вторая', () => {
    const html = render({ selected: 'worker', stats: null, report: report([
      tile({ key: 'cpu-0', kind: 'cpu', name: 'Процессор 1', tempC: 60, level: 'ok' }),
      tile({ key: 'cpu-1', kind: 'cpu', name: 'Процессор 2', tempC: 99, level: 'danger' }),
    ]) });
    expect(html).toContain('Процессор 1');
    expect(html).toContain('Процессор 2');
    // Плитка danger — C.dangerBg/C.dangerBorder; у ok-плитки таких цветов нет.
    const tiles = html.split('<div title=').slice(1);
    expect(tiles).toHaveLength(2);
    const ok = tiles.find(t => t.includes('Процессор 1'))!;
    const bad = tiles.find(t => t.includes('Процессор 2'))!;
    expect(bad).toContain('--c-danger-border');
    expect(ok).not.toContain('--c-danger-border');
  });
  it('ферма не ответила — вкладка не падает, детали узла показаны', () => {
    const html = render({ farm: null, selected: null });
    expect(html).toContain('Стенд');
    expect(html).not.toContain('Ферма');
  });
  it('нет ни фермы, ни отчёта и идёт загрузка — скелетон, а не ошибка', () => {
    expect(() => render({ farm: null, selected: null, report: null, loading: true })).not.toThrow();
  });
  it('в интерфейсе нет старых строк: вкладки и баннера', () => {
    const html = render({});
    expect(html).not.toContain(OLD_TAB);
    expect(html).not.toContain(OLD_BANNER);
  });
});

// === 6. Сторож: плитку, порог и серию выбирает kind, а не литерал ключа ===
describe('инвариант: ни одного ветвления по ключу плитки', () => {
  for (const f of ['HardwareTab.tsx', 'hardwareFormat.ts', 'HwTile.tsx', 'TempChart.tsx']) {
    it(f, () => {
      const src = readFileSync(new URL(`./${f}`, import.meta.url), 'utf8');
      expect(src).not.toMatch(/key\s*===?\s*['"](cpu|vrm|board)/);
      expect(src).not.toMatch(/['"](cpu|vrm|board)['"]\s*===?\s*\w*\.?key/);
    });
  }
});
