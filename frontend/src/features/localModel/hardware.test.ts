// Тесты вкладки «Железо»: видимость вкладки (админ/не-админ/сохранённая hardware),
// опрос только при активной вкладке, окраска плитки по level, null в sparkline,
// «датчика нет» без графика, лимит 3 семейств, строка-ссылка, stale.
//
// Окружение node (без jsdom): чистые функции — прямыми вызовами, компоненты —
// renderToStaticMarkup (паттерн GpuCard.test.ts / localModelSelfScope.test.ts).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { HardwareReport, HardwareBrief, HwLevel } from './hardwareTypes';
import {
  hwClosest, hwLevelLabel, hwMemMargin, hwScalePos, hwSummary, hwBriefMem, hwBriefLevel,
  hwBriefFlags, familyChipDisabled, hwTileView, hwGpuOrdinal, hwRolesLabel,
  type HwTileView,
} from './hardwareFormat';
import { HwTile } from './HwTile';
import { Sparkline } from './Sparkline';
import { HwLinkRow } from './HwLinkRow';
import { GpuDetailCard } from './GpuDetailCard';
import { TempChart } from './TempChart';
import { resolveTab } from './LocalModelPanel';
import { createHardwarePoller } from './useLocalModelHardware';

// === Фикстура: снимок стенда (цифры макета v2, живые 18.10) ===

function spark(n: number, base: number, withNull = false): (number | null)[] {
  const arr: (number | null)[] = [];
  for (let i = 0; i < n; i++) {
    if (withNull && i === Math.floor(n / 2)) arr.push(null);
    else arr.push(base + (i % 3) - 1);
  }
  return arr;
}

function makeReport(over: Partial<HardwareReport> = {}): HardwareReport {
  return {
    at: Date.now(),
    status: 'ok',
    level: 'ok',
    gpus: [
      { index: 0, uuid: 'gpu-a', name: 'RTX 3090', roles: ['text'], utilPerc: 100, memUsedMb: 24022, memTotalMb: 24576, memBusPerc: 44, coreTempC: 61, memTempC: 68, hotspotTempC: 75, fanPerc: 0, clockMhz: 1590, clockMaxMhz: 2100, memClockMhz: 9501, powerW: 218, powerLimitW: 220, throttle: { powerCap: true, thermal: false }, pstate: 'P2', pcie: { gen: 4, width: 8 }, busId: '0000:0b:00.0', level: 'ok', levels: { mem: 'ok', core: 'ok', hotspot: 'ok' } },
      { index: 1, uuid: 'gpu-b', name: 'RTX 3090', roles: ['text', 'media'], utilPerc: 99, memUsedMb: 24022, memTotalMb: 24576, memBusPerc: 44, coreTempC: 64, memTempC: 81, hotspotTempC: 76, fanPerc: 52, clockMhz: 1485, clockMaxMhz: 2100, memClockMhz: 9501, powerW: 217, powerLimitW: 220, throttle: { powerCap: true, thermal: false }, pstate: 'P2', pcie: { gen: 4, width: 8 }, busId: '0000:0c:00.0', level: 'ok', levels: { mem: 'ok', core: 'ok', hotspot: 'ok' } },
    ],
    nodes: [
      { key: 'gpu0', kind: 'gpu', name: 'Карта 1', tempC: 68, sensor: 'ok', level: 'ok', warnC: 95, dangerC: 105, detail: {}, spark15m: spark(90, 68) },
      { key: 'gpu1', kind: 'gpu', name: 'Карта 2', tempC: 81, sensor: 'ok', level: 'ok', warnC: 95, dangerC: 105, detail: {}, spark15m: spark(90, 81) },
      { key: 'cpu', kind: 'cpu', name: 'Процессор', tempC: 75, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [71, 58] }, spark15m: spark(90, 75) },
      { key: 'vrm', kind: 'vrm', name: 'Питание процессора', tempC: 53, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: spark(90, 53) },
      { key: 'chipset', kind: 'chipset', name: 'Чипсет платы', tempC: 78, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { fanRpm: 3846 }, spark15m: spark(90, 78) },
      { key: 'disk-0', kind: 'disk', name: 'Диск кэша', tempC: 82.8, sensor: 'ok', level: 'danger', warnC: 74, dangerC: 81, detail: { role: 'kv', critC: 84, usedGb: 370, totalGb: 492 }, spark15m: spark(90, 82) },
      { key: 'disk-1', kind: 'disk', name: 'Системный диск', tempC: 59.9, sensor: 'ok', level: 'ok', warnC: 70, dangerC: 80, detail: { role: 'system', critC: null }, spark15m: spark(90, 59) },
      { key: 'ram', kind: 'ram', name: 'Оперативная память', tempC: null, sensor: 'none', level: 'none', warnC: null, dangerC: null, detail: { usedGb: 83, totalGb: 121 }, spark15m: [] },
    ],
    caseFans: [{ label: 'fan1', rpm: 1159 }, { label: 'fan2', rpm: 4821 }],
    series: [
      { key: 'mem-0', family: 'mem', gpuIndex: 0, label: 'Память · карта 1', points: spark(90, 68) },
      { key: 'mem-1', family: 'mem', gpuIndex: 1, label: 'Память · карта 2', points: spark(90, 81) },
      { key: 'core-0', family: 'core', gpuIndex: 0, label: 'Ядро · карта 1', points: spark(90, 61) },
      { key: 'core-1', family: 'core', gpuIndex: 1, label: 'Ядро · карта 2', points: spark(90, 64) },
      { key: 'cpu', family: 'cpu', label: 'Процессор', points: spark(90, 75) },
      { key: 'chipset', family: 'chipset', label: 'Чипсет', points: spark(90, 78) },
      { key: 'disk-0', family: 'disk', label: 'Диск кэша', points: spark(90, 82) },
      { key: 'disk-1', family: 'disk', label: 'Системный диск', points: spark(90, 59) },
    ],
    stepMs: 10000,
    ...over,
  };
}

function brief(over: Partial<HardwareBrief> = {}): HardwareBrief {
  return {
    level: 'ok',
    gpus: [
      { uuid: 'gpu-a', memTempC: 68, level: 'ok', powerCap: true, thermal: false },
      { uuid: 'gpu-b', memTempC: 81, level: 'ok', powerCap: true, thermal: false },
    ],
    ...over,
  };
}

// === 1. Видимость вкладки: админ / не-админ / сохранённая hardware ===

describe('resolveTab — видимость вкладки «Железо»', () => {
  it('админ: сохранённая hardware → hardware', () => {
    expect(resolveTab(true, 'hardware')).toBe('hardware');
  });
  it('админ: text/media — как сохранено', () => {
    expect(resolveTab(true, 'text')).toBe('text');
    expect(resolveTab(true, 'media')).toBe('media');
  });
  it('не-админ: всегда media (кроме hardware → text)', () => {
    expect(resolveTab(false, 'text')).toBe('media');
    expect(resolveTab(false, 'media')).toBe('media');
  });
  it('не-админ + сохранённая hardware → text (недоступную не показываем)', () => {
    expect(resolveTab(false, 'hardware')).toBe('text');
  });
});

// === 2. Опрос: только при активной вкладке; не-админ — не вызывает ===

describe('createHardwarePoller — опрос только при активной вкладке', () => {
  it('setActive(true, true) — fetch; setActive(false, false) — стоп', () => {
    const fetch = vi.fn().mockResolvedValue(makeReport());
    const clearTimer = vi.fn();
    const p = createHardwarePoller(fetch, 2000, () => 1, clearTimer);

    p.setActive(true, true);
    expect(fetch).toHaveBeenCalledTimes(1); // первый снимок сразу

    p.setActive(false, false);
    expect(clearTimer).toHaveBeenCalled(); // таймер остановлен
    p.dispose();
  });

  it('setActive(false, true) (вкладка неактивна) — fetch не идёт', () => {
    const fetch = vi.fn().mockResolvedValue(makeReport());
    const p = createHardwarePoller(fetch, 2000, () => 1, vi.fn());
    p.setActive(false, true); // open=false → неактивна
    expect(fetch).not.toHaveBeenCalled();
    p.dispose();
  });

  it('getSnapshot до активации — data null, loading false', () => {
    const p = createHardwarePoller(vi.fn(), 2000, () => 1, vi.fn());
    expect(p.getSnapshot()).toEqual({ data: null, loading: false });
    p.dispose();
  });
});

// === 3. Окраска плитки по level ===

describe('HwTile — окраска по level', () => {
  function tileHtml(level: HwLevel, tempC: number | null, sensor: 'ok' | 'none' | 'nodata' = 'ok'): string {
    const view: HwTileView = {
      key: 'x', icon: 'Cpu', name: 'Процессор', value: tempC, dec: false,
      level, sensor, sensorLabel: 'общая', detail: '', spark: spark(90, 70), thresholdTitle: '',
    };
    return renderToStaticMarkup(createElement(HwTile, { view }));
  }
  it('ok — белый фон, рамка borderLight, точка success', () => {
    const html = tileHtml('ok', 70);
    expect(html).toContain('var(--c-bg-white)');
    expect(html).toContain('var(--c-border-light)');
    expect(html).toContain('var(--c-success)');
  });
  it('warn — warningBg/warning, значение warningText', () => {
    const html = tileHtml('warn', 88);
    expect(html).toContain('var(--c-warning-bg)');
    expect(html).toContain('var(--c-warning)');
    expect(html).toContain('var(--c-warning-text)');
  });
  it('danger — dangerBg/dangerBorder, значение dangerText', () => {
    const html = tileHtml('danger', 96);
    expect(html).toContain('var(--c-danger-bg)');
    expect(html).toContain('var(--c-danger-border)');
    expect(html).toContain('var(--c-danger-text)');
  });
});

// === 4. null в sparkline рвёт линию ===

describe('Sparkline — null рвёт линию', () => {
  it('ряд без null — один polyline', () => {
    const html = renderToStaticMarkup(createElement(Sparkline, { points: [1, 2, 3, 4, 5], color: 'red' }));
    const count = (html.match(/<polyline/g) ?? []).length;
    expect(count).toBe(1);
  });
  it('ряд с null в середине — два polyline (разрыв)', () => {
    const html = renderToStaticMarkup(createElement(Sparkline, { points: [1, 2, null, 4, 5], color: 'red' }));
    const count = (html.match(/<polyline/g) ?? []).length;
    expect(count).toBe(2);
  });
  it('все null — линии нет (пустая строка)', () => {
    const html = renderToStaticMarkup(createElement(Sparkline, { points: [null, null, null], color: 'red' }));
    expect(html).toBe('');
  });
});

// === 5. «датчика нет» без графика ===

describe('HwTile — «датчика нет» (sensor=none) без графика', () => {
  it('показывает «датчика нет» курсивом и подложку, не sparkline', () => {
    const view: HwTileView = {
      key: 'ram', icon: 'MemoryStick', name: 'Оперативная память', value: null, dec: false,
      level: 'none', sensor: 'none', sensorLabel: '',
      detail: 'занято 83 из 121 ГБ', spark: [], thresholdTitle: '',
    };
    const html = renderToStaticMarkup(createElement(HwTile, { view }));
    expect(html).toContain('датчика нет');
    // Подложка (C.bgInset) вместо sparkline: нет <svg class="spark">
    expect(html).not.toContain('class="spark"');
    expect(html).toContain('var(--c-bg-inset)');
  });
  it('sensor=nodata (tempC=null) — «нет данных»', () => {
    const view: HwTileView = {
      key: 'g', icon: 'Gpu', name: 'Карта 1', value: null, dec: false,
      level: 'none', sensor: 'nodata', sensorLabel: 'ядро · память: нет данных',
      detail: '', spark: [], thresholdTitle: '',
    };
    const html = renderToStaticMarkup(createElement(HwTile, { view }));
    expect(html).toContain('нет данных');
  });
});

// === 6. Лимит 3 семейств ===

describe('familyChipDisabled — лимит трёх семейств', () => {
  it('3 выбрано → четвёртый (неon) disabled', () => {
    expect(familyChipDisabled(false, 3, 3)).toBe(true);
  });
  it('3 выбрано → on-чип не disabled', () => {
    expect(familyChipDisabled(true, 3, 3)).toBe(false);
  });
  it('2 выбрано → остальные не disabled', () => {
    expect(familyChipDisabled(false, 2, 3)).toBe(false);
  });
});

// === 7. Строка-ссылка: худший уровень, память, плашки, role=button ===

describe('HwLinkRow — строка-ссылка «Видеокарты»', () => {
  it('role=button + aria-label + «Железо →» + «память 68° · 81°»', () => {
    const html = renderToStaticMarkup(createElement(HwLinkRow, { brief: brief(), onOpen: () => {} }));
    expect(html).toContain('role="button"');
    expect(html).toContain('Видеокарты');
    expect(html).toContain('память 68° · 81°');
    expect(html).toContain('Железо →');
  });
  it('powerCap → плашка «в лимите мощности»', () => {
    const html = renderToStaticMarkup(createElement(HwLinkRow, { brief: brief(), onOpen: () => {} }));
    expect(html).toContain('в лимите мощности');
  });
  it('thermal → плашка «перегрев»', () => {
    const b = brief({ gpus: [{ uuid: 'gpu-a', memTempC: 68, level: 'ok', powerCap: false, thermal: true }] });
    const html = renderToStaticMarkup(createElement(HwLinkRow, { brief: b, onOpen: () => {} }));
    expect(html).toContain('перегрев');
  });
  it('hwBriefMem — «память N° · M°»', () => {
    expect(hwBriefMem(brief())).toBe('память 68° · 81°');
  });
  it('hwBriefLevel — худший уровень', () => {
    expect(hwBriefLevel(brief({ level: 'warn' }))).toBe('warn');
  });
  it('hwBriefFlags — thermal/powerCap', () => {
    expect(hwBriefFlags(brief())).toEqual({ thermal: false, powerCap: true });
  });
});

// === 8. stale ===

describe('stale — сводка и приглушение', () => {
  it('hwSummary: stale → «нет свежих данных»', () => {
    expect(hwSummary({ level: 'ok', status: 'stale', nodes: makeReport().nodes })).toBe('нет свежих данных');
  });
  it('hwSummary: ok → «Горячее всех — …»', () => {
    const s = hwSummary({ level: 'ok', status: 'ok', nodes: makeReport().nodes });
    expect(s).toMatch(/^Горячее всех — /);
  });
  it('hwSummary: danger → «Ближе всех к пределу …, опасно с …»', () => {
    const nodes = makeReport().nodes.map(n => n.key === 'disk-0' ? { ...n, level: 'danger' as HwLevel } : n);
    const s = hwSummary({ level: 'danger', status: 'ok', nodes });
    expect(s).toContain('Ближе всех к пределу');
    expect(s).toContain('опасно с');
  });
  it('hwClosest — худший уровень, при равенстве меньший запас', () => {
    const nodes = makeReport().nodes;
    const c = hwClosest(nodes);
    // Диск кэша (82.8, danger) — худший
    expect(c?.key).toBe('disk-0');
  });
  it('hwLevelLabel — плашки', () => {
    expect(hwLevelLabel('ok')).toBe('всё в норме');
    expect(hwLevelLabel('warn')).toBe('есть горячие узлы');
    expect(hwLevelLabel('danger')).toBe('есть опасность');
  });
});

// === hwTileView: порядок, подписи, детали ===

describe('hwTileView — модели плиток', () => {
  it('порядок узлов фиксированный (gpu, gpu, cpu, vrm, chipset, disk, disk, ram)', () => {
    const views = hwTileView(makeReport());
    expect(views.map(v => v.key)).toEqual(['gpu0', 'gpu1', 'cpu', 'vrm', 'chipset', 'disk-0', 'disk-1', 'ram']);
  });
  it('gpu: подпись «память», деталь «ядро N · гор. точка M»', () => {
    const v = hwTileView(makeReport())[0];
    expect(v.sensorLabel).toBe('память');
    expect(v.detail).toBe('ядро 61 · гор. точка 75');
  });
  it('cpu: деталь «кристаллы 71 и 58»', () => {
    const v = hwTileView(makeReport()).find(x => x.key === 'cpu')!;
    expect(v.detail).toBe('кристаллы 71 и 58');
  });
  it('chipset: деталь «вентилятор 3846 об/мин»', () => {
    const v = hwTileView(makeReport()).find(x => x.key === 'chipset')!;
    expect(v.detail).toBe('вентилятор 3846 об/мин');
  });
  it('disk kv: «до предела 1,2 °C · занято 370 из 492 ГБ»', () => {
    const v = hwTileView(makeReport()).find(x => x.key === 'disk-0')!;
    expect(v.detail).toBe('до предела 1,2 °C · занято 370 из 492 ГБ');
  });
  it('disk system (critC=null): «предел не сообщает — пороги 70 / 80»', () => {
    const v = hwTileView(makeReport()).find(x => x.key === 'disk-1')!;
    expect(v.detail).toBe('предел не сообщает — пороги 70 / 80');
  });
  it('ram: «занято 83 из 121 ГБ»', () => {
    const v = hwTileView(makeReport()).find(x => x.key === 'ram')!;
    expect(v.detail).toBe('занято 83 из 121 ГБ');
  });
});

// === GpuDetailCard: память, шкала, плашки, «Подробнее» ===

describe('GpuDetailCard — карточка видеокарты', () => {
  const gpu = makeReport().gpus[1]; // gpu-b: mem 81, powerCap
  it('показывает имя · роли, «карта 2 из 2», память 81 °C', () => {
    const html = renderToStaticMarkup(createElement(GpuDetailCard, { gpu, total: 2 }));
    expect(html).toContain('RTX 3090 · текст · медиа');
    expect(html).toContain('карта 2 из 2');
    expect(html).toContain('81 °C');
    expect(html).toContain('память карты');
  });
  it('память ≥ 95 → «память замедляется»', () => {
    const g = { ...gpu, memTempC: 98, levels: { ...gpu.levels, mem: 'warn' } };
    const html = renderToStaticMarkup(createElement(GpuDetailCard, { gpu: g, total: 2 }));
    expect(html).toContain('память замедляется');
  });
  it('powerCap → «Упирается в лимит мощности»', () => {
    const html = renderToStaticMarkup(createElement(GpuDetailCard, { gpu, total: 2 }));
    expect(html).toContain('Упирается в лимит мощности');
  });
  it('hwMemMargin: запас / замедление', () => {
    expect(hwMemMargin(80)).toBe('запас до замедления 15 °C');
    expect(hwMemMargin(98)).toBe('память замедляется');
    expect(hwMemMargin(null)).toBe('датчик памяти не ответил');
  });
  it('hwScalePos: 30→0%, 110→100%, 95→81,25%', () => {
    expect(hwScalePos(30)).toBe(0);
    expect(hwScalePos(110)).toBe(100);
    expect(Math.round(hwScalePos(95) * 100) / 100).toBe(81.25);
  });
  it('hwRolesLabel / hwGpuOrdinal', () => {
    expect(hwRolesLabel(['text', 'media'])).toBe('текст · медиа');
    expect(hwRolesLabel(['text'])).toBe('текст');
    expect(hwGpuOrdinal(0, 2)).toBe('карта 1 из 2');
  });
});

// === TempChart: рендер, лимит, пустое состояние ===

describe('TempChart — график «Температуры»', () => {
  it('рендер: заголовок, окна, чипы, легенда', () => {
    const html = renderToStaticMarkup(createElement(TempChart, {
      report: makeReport(), window: '15m', onWindowChange: () => {}, isMobile: false,
    }));
    expect(html).toContain('Температуры');
    expect(html).toContain('Память карт');
    expect(html).toContain('15 мин');
  });
  it('по умолчанию выбрано «память карт» (chip on) и ряды памяти в легенде', () => {
    const html = renderToStaticMarkup(createElement(TempChart, {
      report: makeReport(), window: '15m', onWindowChange: () => {}, isMobile: false,
    }));
    // Легенда: «Память · карта 1» и «Память · карта 2»
    expect(html).toContain('Память · карта 1');
    expect(html).toContain('Память · карта 2');
  });
});
