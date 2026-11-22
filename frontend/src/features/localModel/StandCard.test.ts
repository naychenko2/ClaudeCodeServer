// Тесты StandCard и OtherSensorsSection (ST2): рендер по пропам без опросов и
// состояния. StandCard: две нейтральные KPI (0 ≠ null, RAM sensor=none не скрывает
// KPI, CPU без истории и плейсхолдера), secondary без спарков, CPU-0/CPU-1, GPU без
// памяти — ядро с пометкой, stale снимает заливки. Секция: aria-expanded при
// open=false, мета тревоги/счётчика, «датчика нет» vs «нет данных», спарк только у
// диска кэша. DOM-паттерн — как FarmKpiCard.test.ts: linkedom + createRoot, mount
// с размонтированием.

import { describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
(win as unknown as { matchMedia: unknown }).matchMedia = () => ({
  matches: false, addEventListener() {}, removeEventListener() {},
});
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: react-dom читает window при загрузке
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { StandCard } = await import('./StandCard');
const { OtherSensorsSection, sensorRows, sensorsMeta, rowValueText, uptimeLabel, fansValue } = await import('./OtherSensorsSection');
import type { HardwareReport } from './hardwareTypes';

// Монтирует компонент и оставляет дерево живым; unmount снимает его из body.
function mount(el: React.ReactElement): { host: Element; unmount: () => void } {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => { root.render(el); });
  return { host, unmount: () => { act(() => { root.unmount(); }); host.remove(); } };
}

// Обе сетки repeat(2, minmax(0, 1fr)) карточки: [0] — KPI, [1] — температуры.
function grids(host: Element): Element[] {
  return Array.from(host.querySelectorAll('div[style*="repeat(2, minmax(0, 1fr))"]'));
}

// Помощник spark: N точек вокруг base (как в hardwareHierarchy.test.ts).
function spark(n: number, base: number): (number | null)[] {
  const arr: (number | null)[] = [];
  for (let i = 0; i < n; i++) arr.push(base + (i % 3) - 1);
  return arr;
}

// Снимок стенда с ДВУМЯ сокетами cpu-0/cpu-1 (ветвление по kind, не по key —
// проверяется и в hardwareHierarchy.test.ts) и водой в warn: тревога для меты секции.
function makeReport(over: Partial<HardwareReport> = {}): HardwareReport {
  return {
    at: Date.now(), status: 'ok', level: 'warn', cpuLoadPerc: 41,
    gpus: [
      { index: 0, uuid: 'gpu-a', name: 'RTX 3090', roles: ['text'], utilPerc: 100, memUsedMb: 24022, memTotalMb: 24576, memBusPerc: 44, coreTempC: 61, memTempC: 68, hotspotTempC: 75, fanPerc: 0, clockMhz: 1590, clockMaxMhz: 2100, memClockMhz: 9501, powerW: 218, powerLimitW: 220, throttle: { powerCap: true, thermal: false }, pstate: 'P2', pcie: { gen: 4, width: 8 }, busId: '0000:0b:00.0', level: 'ok', levels: { mem: 'ok', core: 'ok', hotspot: 'ok' } },
    ],
    nodes: [
      { key: 'gpu0', kind: 'gpu', name: 'Карта 1', tempC: 68, sensor: 'ok', level: 'ok', warnC: 95, dangerC: 105, detail: {}, spark15m: spark(90, 68) },
      { key: 'cpu-0', kind: 'cpu', name: 'Процессор 1', tempC: 75, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [71, 58] }, spark15m: spark(90, 75) },
      { key: 'cpu-1', kind: 'cpu', name: 'Процессор 2', tempC: 74, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [70, 57] }, spark15m: spark(90, 74) },
      { key: 'vrm-0', kind: 'vrm', name: 'Питание процессора 1', tempC: 53, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: spark(90, 53) },
      { key: 'board', kind: 'board', name: 'Плата', tempC: 42, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { peripheralC: 39 }, spark15m: spark(90, 42) },
      { key: 'chipset', kind: 'chipset', name: 'Чипсет платы', tempC: 78, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { fanRpm: 3846 }, spark15m: spark(90, 78) },
      { key: 'water', kind: 'water', name: 'Вода СЖО', tempC: 51, sensor: 'ok', level: 'warn', warnC: 50, dangerC: 55, detail: {}, spark15m: spark(90, 51) },
      { key: 'disk-0', kind: 'disk', name: 'Диск кэша', tempC: null, sensor: 'none', level: 'none', warnC: null, dangerC: null, detail: { role: 'kv', critC: null, usedGb: 370, totalGb: 492, usedPerc: 75 }, spark15m: spark(90, 75) },
      { key: 'disk-1', kind: 'disk', name: 'Системный диск', tempC: 59.9, sensor: 'ok', level: 'ok', warnC: 70, dangerC: 80, detail: { role: 'system', critC: null }, spark15m: spark(90, 59) },
      { key: 'ram-0', kind: 'ram', name: 'Оперативная память', tempC: null, sensor: 'none', level: 'none', warnC: null, dangerC: null, detail: { usedGb: 83, totalGb: 121, usedPerc: 69 }, spark15m: spark(90, 69) },
    ],
    caseFans: [{ label: 'fan1', rpm: 1159 }, { label: 'fan2', rpm: 4821 }],
    series: [], stepMs: 10000, uptimeS: 140400,
    ...over,
  };
}

describe('StandCard', () => {
  it('две KPI: 0 — измеренное значение, а не «нет данных»; primary нейтральны', () => {
    const r = makeReport({ cpuLoadPerc: 0 });
    (r.nodes.find(n => n.key === 'ram-0')!.detail).usedPerc = 0;
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const g = grids(host);
    expect(g.length).toBe(2);
    const cells = Array.from(g[0].children);
    expect((cells[0].children[1]?.textContent ?? '').trim()).toBe('0 %');
    expect((cells[1].children[1]?.textContent ?? '').trim()).toBe('0 %');
    expect(cells[0].textContent ?? '').not.toContain('нет данных');
    // нейтральность: ни точек success, ни тревожных заливок в primary (мутация
    // «добавить C.success в primary» краснит именно эту проверку)
    expect(g[0].innerHTML).not.toContain('var(--c-success)');
    expect(g[0].innerHTML).not.toContain('var(--c-warning-bg)');
    expect(g[0].innerHTML).not.toContain('var(--c-danger-bg)');
    unmount();
  });

  it('RAM sensor=none с занятостью — KPI показывается со значением', () => {
    const r = makeReport();
    const ram = r.nodes.find(n => n.kind === 'ram')!;
    expect(ram.sensor).toBe('none');
    ram.detail.usedPerc = 69;
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const cells = Array.from(grids(host)[0].children);
    expect((cells[1].children[1]?.textContent ?? '').trim()).toBe('69 %');
    unmount();
  });

  it('cpuLoadPerc и usedPerc null — «нет данных» в своей ячейке, соседняя KPI не скрыта', () => {
    const r = makeReport({ cpuLoadPerc: null });
    const ram = r.nodes.find(n => n.key === 'ram-0')!;
    ram.detail.usedPerc = null;
    delete ram.detail.usedGb;
    delete ram.detail.totalGb;
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const cells = Array.from(grids(host)[0].children);
    expect(cells[0].textContent ?? '').toContain('нет данных');
    expect(cells[1].textContent ?? '').toContain('нет данных');
    expect(cells[1].textContent ?? '').toContain('Оперативная память');
    unmount();
  });

  it('два сокета cpu-0/cpu-1 попадают в «Температуры сейчас»', () => {
    const { host, unmount } = mount(createElement(StandCard, { report: makeReport(), isMobile: false }));
    const temps = grids(host)[1].textContent ?? '';
    expect(temps).toContain('Процессор 1');
    expect(temps).toContain('Процессор 2');
    unmount();
  });

  it('GPU без memTempC — значение ядра с явной пометкой', () => {
    const r = makeReport();
    r.gpus[0].memTempC = null;
    (r.nodes.find(n => n.key === 'gpu0')!).tempC = 61;
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const card = (grids(host)[1].textContent ?? '');
    expect(card).toContain('61 °C');
    expect(card).toContain('ядро · память: нет данных');
    unmount();
  });

  it('stale: значения приглушены, тревожные заливки сняты, плашка «узел молчит»', () => {
    const r = makeReport({ status: 'stale' });
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const cells = Array.from(grids(host)[0].children);
    // значения KPI приглушены (C.textMuted)
    expect((cells[0].children[1]?.getAttribute('style') ?? '')).toContain('var(--c-text-muted)');
    // тревожная плитка воды (level warn в фикстуре) — без warning-заливки
    expect(grids(host)[1].innerHTML).not.toContain('var(--c-warning-bg)');
    // плашка уровня — «узел молчит», а не «есть горячие узлы»
    expect(host.innerHTML).toContain('узел молчит');
    unmount();
  });

  it('secondary без спарков; у CPU-KPI нет ни Sparkline, ни плейсхолдера, у RAM — есть', () => {
    const { host, unmount } = mount(createElement(StandCard, { report: makeReport(), isMobile: false }));
    // вторая сетка (Температуры сейчас) — ни одного svg.spark
    expect(grids(host)[1].querySelectorAll('svg.spark').length).toBe(0);
    // CPU-KPI: без спарка и без подложки (border-radius:4px) — истории загрузки нет вовсе
    const cpuCell = grids(host)[0].children[0];
    expect(cpuCell.querySelectorAll('svg.spark').length).toBe(0);
    expect(cpuCell.innerHTML).not.toContain('border-radius:4px');
    // RAM-KPI: спарк занятости на месте
    const ramCell = grids(host)[0].children[1];
    expect(ramCell.querySelectorAll('svg.spark').length).toBe(1);
    unmount();
  });

  it('под KPI «Загрузка CPU» рисуется Sparkline «пики за 10 с»: C.info, null — разрыв', () => {
    const r = makeReport({ cpuLoadSpark15m: [41, null, 43] });
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const cpuCell = grids(host)[0].children[0];
    const sparks = cpuCell.querySelectorAll('svg.spark');
    expect(sparks.length).toBe(1);
    expect(sparks[0]?.getAttribute('aria-label')).toContain('пики за 10 с');
    // null в середине ряда — разрыв: два polyline-сегмента вместо одного
    expect(sparks[0]?.querySelectorAll('polyline').length).toBe(2);
    expect((sparks[0]?.querySelectorAll('polyline')[0]?.getAttribute('stroke') ?? '')).toContain('var(--c-info)');
    unmount();
  });

  it('загрузка 0 — «0 %» и линия (0 не теряется); null в ряду — разрыв, не ноль', () => {
    const r = makeReport({ cpuLoadPerc: 0, cpuLoadSpark15m: [0, null, 0] });
    const { host, unmount } = mount(createElement(StandCard, { report: r, isMobile: false }));
    const cpuCell = grids(host)[0].children[0];
    expect((cpuCell.children[1]?.textContent ?? '').trim()).toBe('0 %');
    const sparks = cpuCell.querySelectorAll('svg.spark');
    expect(sparks.length).toBe(1);
    expect(sparks[0]?.querySelectorAll('polyline').length).toBe(2);
    unmount();
  });

  it('isMobile=true — обе сетки остаются в двух колонках', () => {
    const { host, unmount } = mount(createElement(StandCard, { report: makeReport(), isMobile: true }));
    for (const g of grids(host)) {
      expect(g.getAttribute('style') ?? '').toContain('repeat(2, minmax(0, 1fr))');
    }
    unmount();
  });

  it('подпись «Загрузка CPU» переносится вместо усечения; у RAM усечение сохранено', () => {
    const { host, unmount } = mount(createElement(StandCard, { report: makeReport(), isMobile: false }));
    const cells = Array.from(grids(host)[0].children);
    // у «Загрузка CPU» нет ellipsis-механизма — усечение невозможно в принципе
    expect(cells[0].querySelector('span[style*="ellipsis"]')).toBeNull();
    // у RAM усечение сохранено (допускается только у «Оперативная память»)
    const ramLabel = cells[1].querySelector('span[style*="ellipsis"]');
    expect(ramLabel).not.toBeNull();
    expect(ramLabel?.textContent ?? '').toBe('Оперативная память');
    unmount();
  });
});

describe('OtherSensorsSection', () => {
  it('open=false: aria-expanded=false, тело скрыто; порядок строк — как в отчёте', () => {
    const { host, unmount } = mount(createElement(OtherSensorsSection, { report: makeReport(), open: false, onToggle: () => {}, isMobile: false }));
    const head = host.querySelector('button');
    expect(head?.getAttribute('aria-expanded')).toBe('false');
    const body = host.querySelector('div[style*="border-bottom"]')!.parentElement!.parentElement!;
    const names = Array.from(body.querySelectorAll('div[style*="border-bottom"]')).map(d => (d.querySelector('span:last-child')?.textContent ?? ''));
    expect(names).toEqual([
      'Питание процессора 1', 'Плата', 'Чипсет платы',
      'Вода СЖО', 'Диск кэша', 'Системный диск', 'Вентиляторы корпуса', 'Аптайм',
    ]);
    unmount();
  });

  it('мета при тревоге содержит имя и значение датчика', () => {
    const { host, unmount } = mount(createElement(OtherSensorsSection, { report: makeReport(), open: false, onToggle: () => {}, isMobile: false }));
    // вода в warn: имя и значение видны в meta закрытой секции
    expect(host.innerHTML).toContain('Вода СЖО 51 °C');
    unmount();
  });

  it('мета без тревог — «N показателей», с nodata — « · M без данных»', () => {
    const r0 = makeReport();
    (r0.nodes.find(n => n.key === 'water')!).level = 'ok';
    const rows = sensorRows(r0);
    expect(rows.length).toBe(8);
    expect(sensorsMeta(rows, false)).toBe('8 показателей');
    const r = makeReport();
    const water = r.nodes.find(n => n.key === 'water')!;
    water.tempC = null; water.sensor = 'nodata'; water.level = 'none';
    const rows2 = sensorRows(r);
    expect(sensorsMeta(rows2, false)).toBe('8 показателей · 1 без данных');
  });

  it('«датчика нет» и «нет данных» — разные состояния строки', () => {
    const rNone = makeReport();
    const wNone = rNone.nodes.find(n => n.key === 'water')!;
    wNone.tempC = null; wNone.sensor = 'none'; wNone.level = 'none';
    const rNodata = makeReport();
    const wNodata = rNodata.nodes.find(n => n.key === 'water')!;
    wNodata.tempC = null; wNodata.sensor = 'nodata'; wNodata.level = 'none';
    const m1 = mount(createElement(OtherSensorsSection, { report: rNone, open: true, onToggle: () => {}, isMobile: false }));
    const waterNone = Array.from(m1.host.querySelectorAll('div[style*="border-bottom"]'))
      .find(d => (d.textContent ?? '').includes('Вода СЖО'));
    expect(waterNone?.textContent ?? '').toContain('датчика нет');
    m1.unmount();
    const m2 = mount(createElement(OtherSensorsSection, { report: rNodata, open: true, onToggle: () => {}, isMobile: false }));
    const waterNodata = Array.from(m2.host.querySelectorAll('div[style*="border-bottom"]'))
      .find(d => (d.textContent ?? '').includes('Вода СЖО'));
    expect(waterNodata?.textContent ?? '').toContain('нет данных');
    expect(waterNodata?.textContent ?? '').not.toContain('датчика нет');
    m2.unmount();
  });

  it('спарк в теле секции только у диска кэша', () => {
    const { host, unmount } = mount(createElement(OtherSensorsSection, { report: makeReport(), open: true, onToggle: () => {}, isMobile: false }));
    const body = host.querySelector('div[role="region"]')!;
    const sparks = body.querySelectorAll('svg.spark');
    expect(sparks.length).toBe(1);
    expect(sparks[0]?.getAttribute('aria-label')).toBe('Заполненность диска кэша за 15 минут');
    unmount();
  });

  it('stale: мета «последние данные · уровни не окрашены», строки без точек и тревожных цветов', () => {
    const { host, unmount } = mount(createElement(OtherSensorsSection, { report: makeReport({ status: 'stale' }), open: true, onToggle: () => {}, isMobile: false }));
    expect(host.innerHTML).toContain('последние данные · уровни не окрашены');
    // warn-строка воды без тревожной окраски
    const waterRow = Array.from(host.querySelectorAll('div[style*="border-bottom"]')).find(d => (d.textContent ?? '').includes('Вода СЖО'));
    expect(waterRow?.innerHTML ?? '').not.toContain('var(--c-warning-text)');
    expect(waterRow?.innerHTML ?? '').not.toContain('var(--c-warning)');
    unmount();
  });
});