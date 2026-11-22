// Тесты чистой иерархии показателей вкладки «Стенды» (макет v4): hwMetricGroups
// распределяет узлы по трём уровням ветвлением по kind/role (не по key — ключи
// multi-socket 'cpu-0'/'cpu-1' и 'vrm-1' обязательны в фикстуре), upsMetricGroups
// собирает ИБП: заряд крупным, остальное строками, четыре ряда динамики с
// делением десятых вольта на 10.
//
// Окружение node: чистые функции — прямыми вызовами (паттерн hardware.test.ts).

import { describe, expect, it } from 'vitest';
import type { HardwareReport, HardwareUps } from './hardwareTypes';
import { hwMetricGroups, hwNoDataCount, upsMetricGroups } from './hardwareFormat';

// === Фикстуры ===

function spark(n: number, base: number, withNull = false): (number | null)[] {
  const arr: (number | null)[] = [];
  for (let i = 0; i < n; i++) {
    if (withNull && i === Math.floor(n / 2)) arr.push(null);
    else arr.push(base + (i % 3) - 1);
  }
  return arr;
}

// Снимок стенда с ДВУМЯ сокетами: ключи 'cpu-0'/'cpu-1'/'vrm-1' обязательны:
// при ветвлении по key они никуда не попадут — тест краснит мутацию «ветвиться по key».
function makeReport(over: Partial<HardwareReport> = {}): HardwareReport {
  return {
    at: Date.now(),
    status: 'ok',
    level: 'ok',
    cpuLoadPerc: 41,
    gpus: [
      { index: 0, uuid: 'gpu-a', name: 'RTX 3090', roles: ['text'], utilPerc: 100, memUsedMb: 24022, memTotalMb: 24576, memBusPerc: 44, coreTempC: 61, memTempC: 68, hotspotTempC: 75, fanPerc: 0, clockMhz: 1590, clockMaxMhz: 2100, memClockMhz: 9501, powerW: 218, powerLimitW: 220, throttle: { powerCap: true, thermal: false }, pstate: 'P2', pcie: { gen: 4, width: 8 }, busId: '0000:0b:00.0', level: 'ok', levels: { mem: 'ok', core: 'ok', hotspot: 'ok' } },
      { index: 1, uuid: 'gpu-b', name: 'RTX 3090', roles: ['text', 'media'], utilPerc: 99, memUsedMb: 23022, memTotalMb: 24576, memBusPerc: 44, coreTempC: 64, memTempC: 81, hotspotTempC: 76, fanPerc: 52, clockMhz: 1485, clockMaxMhz: 2100, memClockMhz: 9501, powerW: 217, powerLimitW: 220, throttle: { powerCap: true, thermal: false }, pstate: 'P2', pcie: { gen: 4, width: 8 }, busId: '0000:0c:00.0', level: 'ok', levels: { mem: 'ok', core: 'ok', hotspot: 'ok' } },
    ],
    nodes: [
      { key: 'gpu0', kind: 'gpu', name: 'Карта 1', tempC: 68, sensor: 'ok', level: 'ok', warnC: 95, dangerC: 105, detail: {}, spark15m: spark(90, 68) },
      { key: 'gpu1', kind: 'gpu', name: 'Карта 2', tempC: 81, sensor: 'ok', level: 'ok', warnC: 95, dangerC: 105, detail: {}, spark15m: spark(90, 81) },
      { key: 'cpu-0', kind: 'cpu', name: 'Процессор 1', tempC: 75, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [71, 58] }, spark15m: spark(90, 75) },
      { key: 'cpu-1', kind: 'cpu', name: 'Процессор 2', tempC: 74, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [70, 57] }, spark15m: spark(90, 74) },
      { key: 'vrm-0', kind: 'vrm', name: 'Питание процессора 1', tempC: 53, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: spark(90, 53) },
      { key: 'vrm-1', kind: 'vrm', name: 'Питание процессора 2', tempC: 52, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: spark(90, 52) },
      { key: 'board', kind: 'board', name: 'Плата', tempC: 42, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { peripheralC: 39 }, spark15m: spark(90, 42) },
      { key: 'chipset', kind: 'chipset', name: 'Чипсет платы', tempC: 78, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { fanRpm: 3846 }, spark15m: spark(90, 78) },
      { key: 'water', kind: 'water', name: 'Вода СЖО', tempC: 51, sensor: 'ok', level: 'warn', warnC: 50, dangerC: 55, detail: {}, spark15m: spark(90, 51) },
      { key: 'disk-0', kind: 'disk', name: 'Диск кэша', tempC: null, sensor: 'none', level: 'none', warnC: null, dangerC: null, detail: { role: 'kv', critC: null, usedGb: 370, totalGb: 492, usedPerc: 75 }, spark15m: spark(90, 75) },
      { key: 'disk-1', kind: 'disk', name: 'Системный диск', tempC: 59.9, sensor: 'ok', level: 'ok', warnC: 70, dangerC: 80, detail: { role: 'system', critC: null }, spark15m: spark(90, 59) },
      { key: 'ram-0', kind: 'ram', name: 'Оперативная память', tempC: null, sensor: 'none', level: 'none', warnC: null, dangerC: null, detail: { usedGb: 83, totalGb: 121, usedPerc: 69 }, spark15m: spark(90, 69) },
    ],
    caseFans: [{ label: 'fan1', rpm: 1159 }, { label: 'fan2', rpm: 4821 }],
    series: [],
    stepMs: 10000,
    ...over,
  };
}

function makeUps(over: Partial<HardwareUps> = {}): HardwareUps {
  return {
    title: 'ИБП фермы', status: 'ok', since: null, level: 'ok', power: 'line',
    powerLevel: 'ok', loadLevel: 'ok',
    loadPerc: 22, loadW: 220, chargePerc: 100,
    batteryV: 27.3, batteryNominalV: 24, inputV: 231, inputHz: 50, outputV: 231, tempC: 28,
    loadSpark15m: spark(90, 22),
    chargeSpark15m: spark(90, 100),
    inputVSpark15m: spark(90, 231),
    batteryDeciVSpark15m: spark(90, 273),
    ...over,
  };
}

// === hwMetricGroups ===

describe('hwMetricGroups', () => {
  it('ключи cpu-0/cpu-1/vrm-1 классифицируются по kind, не по key', () => {
    const g = hwMetricGroups(makeReport());
    // cpu-0/cpu-1 (kind cpu) — в secondary, vrm-1 (kind vrm) — в details;
    // при ветвлении по key ('cpu-0' !== 'cpu') они бы сюда не попали
    expect(g.secondary.map(v => v.key)).toEqual(['gpu0', 'gpu1', 'cpu-0', 'cpu-1']);
    expect(g.primary.map(v => v.key)).toEqual(['cpu-load', 'ram-0']);
    expect(g.details.map(v => v.key)).toEqual(['vrm-0', 'vrm-1', 'board', 'chipset', 'water', 'disk-0', 'disk-1']);
  });

  it('KPI «Загрузка CPU»: значение из отчёта, spark пустой', () => {
    const g = hwMetricGroups(makeReport({ cpuLoadPerc: 41 }));
    const kpi = g.primary[0];
    expect(kpi.name).toBe('Загрузка CPU');
    expect(kpi.value).toBe(41);
    expect(kpi.unit).toBe('%');
    expect(kpi.sensor).toBe('ok');
    expect(kpi.spark).toEqual([]);
  });

  it('CPU load 0 не заменяется заглушкой', () => {
    const g = hwMetricGroups(makeReport({ cpuLoadPerc: 0 }));
    expect(g.primary[0].value).toBe(0);
    expect(g.primary[0].sensor).toBe('ok');
  });

  it('cpuLoadPerc null или отсутствует — KPI «нет данных» (sensor nodata), счётчик не растёт', () => {
    const gNull = hwMetricGroups(makeReport({ cpuLoadPerc: null }));
    expect(gNull.primary[0].sensor).toBe('nodata');
    expect(gNull.nodataCount).toBe(0);
    // старые ответы без поля — optional в HardwareReport
    const r = makeReport();
    delete (r as { cpuLoadPerc?: number | null }).cpuLoadPerc;
    const gAbsent = hwMetricGroups(r);
    expect(gAbsent.primary[0].value).toBeNull();
    expect(gAbsent.primary[0].sensor).toBe('nodata');
    expect(gAbsent.nodataCount).toBe(0);
  });

  it('RAM с sensor=none — валидный KPI: значение 69, spark15m с KPI, в счётчик не входит', () => {
    const g = hwMetricGroups(makeReport());
    const ram = g.primary[1];
    expect(ram.key).toBe('ram-0');
    // занятость валидна несмотря на отсутствие температурного датчика
    expect(ram.value).toBe(69);
    expect(ram.unit).toBe('%');
    // sensor узла сохраняется: рендер по value — 69 ≠ null → выводится число
    expect(ram.sensor).toBe('none');
    // spark15m RAM идёт с KPI
    expect(ram.spark).toEqual(spark(90, 69));
    // none-узлы в счётчик nodata не входят
    expect(g.nodataCount).toBe(0);
    expect(hwNoDataCount(makeReport())).toBe(0);
  });

  it('RAM usedPerc=0 — значение 0 (числом), не «нет данных»', () => {
    const r = makeReport();
    const ramNode = r.nodes.find(n => n.kind === 'ram')!;
    ramNode.detail.usedPerc = 0;
    const g = hwMetricGroups(r);
    expect(g.primary[1].value).toBe(0);
    expect(g.primary[1].sensor).toBe('none');
    expect(g.nodataCount).toBe(0);
  });

  it('RAM usedPerc=null — значение null, в nodataCount не входит (none не считается)', () => {
    const r = makeReport();
    const ramNode = r.nodes.find(n => n.kind === 'ram')!;
    ramNode.detail.usedPerc = null;
    // без чисел деталь «занято N из M ГБ» не собирается
    delete ramNode.detail.usedGb;
    delete ramNode.detail.totalGb;
    const g = hwMetricGroups(r);
    // null сохраняется null
    expect(g.primary[1].value).toBeNull();
    // unit '%' → рендер покажет «нет данных», а не «датчика нет»
    expect(g.primary[1].sensor).toBe('none');
    expect(g.nodataCount).toBe(0);
  });

  it('nodataCount считает только датчики nodata (none не считается), совпадает с hwNoDataCount', () => {
    const r = makeReport();
    // датчик температуры воды не ответил
    const water = r.nodes.find(n => n.kind === 'water')!;
    water.tempC = null;
    water.sensor = 'nodata';
    water.level = 'none';
    const g = hwMetricGroups(r);
    // единственный nodata-датчик — вода; RAM и диск кэша (none) не считаются
    expect(g.nodataCount).toBe(1);
    expect(hwNoDataCount(r)).toBe(1);
  });

  it('у secondary спарков нет; в details спарк только у диска кэша', () => {
    const g = hwMetricGroups(makeReport());
    // историю температур даёт TempChart
    expect(g.secondary.every(v => v.spark.length === 0)).toBe(true);
    // спарк остаётся только у диска кэша — его ряда в TempChart нет
    expect(g.details.every(v => (v.key === 'disk-0' ? v.spark.length === 90 : v.spark.length === 0))).toBe(true);
  });

  it('thresholdTitle у secondary/details — как у плиток hwTileView; KPI без порогов', () => {
    const g = hwMetricGroups(makeReport());
    const cpuSec = g.secondary.find(v => v.key === 'cpu-0')!;
    expect(cpuSec.thresholdTitle).toBe('Предупреждение с 85 °C, опасность с 95 °C');
    const vrmDetail = g.details.find(v => v.key === 'vrm-1')!;
    expect(vrmDetail.thresholdTitle).toBe('Предупреждение с 90 °C, опасность с 105 °C');
    expect(g.primary[0].thresholdTitle).toBe('');
  });
});

// === upsMetricGroups ===

describe('upsMetricGroups', () => {
  it('primary — заряд: значение, окраска при lowBattery, spark пустой', () => {
    const g = upsMetricGroups(makeUps({ power: 'lowBattery', powerLevel: 'warn', chargePerc: 64 }));
    // primary — одиночный HwTileView, не массив
    expect(g.primary.key).toBe('ups-charge');
    expect(g.primary.value).toBe(64);
    // lowBattery → окраска по powerLevel
    expect(g.primary.level).toBe('warn');
    // ряд заряда живёт в dynamics, под KPI спарка нет
    expect(g.primary.spark).toEqual([]);
  });

  it('secondary — детали и уровни как у плиток upsTiles', () => {
    const g = upsMetricGroups(makeUps({ power: 'battery', powerLevel: 'warn', loadLevel: 'danger', loadPerc: 96, loadW: 960, inputV: null, inputHz: null }));
    const [load, input, batt] = g.secondary;
    // нагрузка: level loadLevel, деталь ≈ Вт
    expect(load.level).toBe('danger');
    expect(load.detail).toBe('≈ 960 Вт');
    // у secondary спарков нет (ряды в dynamics)
    expect(load.spark).toEqual([]);
    // power=battery (offLine) → сеть окрашена powerLevel
    expect(input.level).toBe('warn');
    // inputV=null → «нет данных», герц нет — деталь пустая
    expect(input.sensor).toBe('nodata');
    expect(input.detail).toBe('');
    // батарея: dec, «номинал 24 В», level ok
    expect(batt.dec).toBe(true);
    expect(batt.detail).toBe('номинал 24 В');
    // у ИБП порогов нет: thresholdTitle пуст у всех элементов
    expect([g.primary, ...g.secondary].every(m => m.thresholdTitle === '')).toBe(true);
  });

  it('dynamics: четыре подписанных ряда в порядке заряд/нагрузка/сеть/батарея', () => {
    const g = upsMetricGroups(makeUps());
    expect(g.dynamics.map(s => s.label)).toEqual(['Заряд', 'Нагрузка', 'Сеть', 'Батарея']);
    expect(g.dynamics.map(s => [s.unit, s.dec])).toEqual([['%', false], ['%', false], ['В', false], ['В', true]]);
  });

  it('batteryDeciV [541,null,540] → [54.1,null,54]: деление на 10, null остаётся разрывом', () => {
    const g = upsMetricGroups(makeUps({ batteryDeciVSpark15m: [541, null, 540] }));
    expect(g.dynamics[3].points).toEqual([54.1, null, 54]);
  });

  it('null в ряду батареи остаётся разрывом, non-null делятся', () => {
    const g = upsMetricGroups(makeUps({ batteryDeciVSpark15m: spark(90, 273, true) }));
    const pts = g.dynamics[3].points;
    // середина (i=45) — null (разрыв линии)
    expect(pts[45]).toBeNull();
    // i=0: 273+(0%3)-1 = 272 → 27,2 В
    expect(pts[0]).toBe(27.2);
  });
});
