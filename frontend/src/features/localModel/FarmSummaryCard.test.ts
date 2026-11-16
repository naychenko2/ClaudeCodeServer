// Тесты карточки «Ферма» (макет А): роли словами («CCS», «LLM», «медиа и LLM»),
// молчащий узел (статусная строка «не отвечает с HH:MM:SS», точка-кольцо, плашка
// «узел молчит» вместо «всё в норме»), «версия агента не подходит» (incompatible),
// строка «горячо» без молчащих узлов, окраска выбранного узла (accent), aria-метки,
// раскладка 320/десктоп, выбор узла касанием (onSelect).
//
// Окружение node (без jsdom): SSR — renderToStaticMarkup (паттерн hardware.test.ts:135),
// клики — минимальный фейк-DOM + вызов onClick из reactProps (паттерн fakeDom).
// Время «HH:MM:SS» проверяем тем же разбором локального часового пояса, что и компонент
// (платформонезависимо).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { HardwareFarm, HardwareFarmNode } from './hardwareTypes';
import { FarmSummaryCard } from './FarmSummaryCard';
import { farmHottestLine, farmRolesLabel, farmSilentCount } from './hardwareFormat';

const { installFakeDom, mount, findNodeByAttr, reactProps } = await import('../../components/chat/__tests__/fakeDom');

// Фейк-DOM нужен только прокликиваемому тесту; ставится один раз, идемпотентен.
installFakeDom();

// Фиксированный момент нештатности (unix-мс).
const SINCE = 1763270000000;

// Ожиданное «HH:MM:SS» из SINCE — тот же локальный разбор, что formatHms в компоненте.
function hms(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// === Фикстуры: ферма из трёх узлов (форма — record'ы бэка, camelCase; уровни — бэковые) ===

function node(over: Partial<HardwareFarmNode>): HardwareFarmNode {
  return {
    key: 'x', title: 'x', roles: [], engines: [], media: false,
    status: 'ok', since: null, level: 'ok',
    hottest: null, gpus: [], uptimeS: null, cpuLoadPerc: null, ramUsedPerc: null,
    ...over,
  };
}

// Три уровня (ok/warn/danger): worker без ролей (фолбэк-узел без движков и без секции),
// ai — текстовый (warn), video — текстовый и медиа (danger).
function farmThree(): HardwareFarm {
  return {
    at: 1763280000000,
    level: 'danger',
    farmNodes: [
      node({
        key: 'worker', title: 'worker', status: 'ok', level: 'ok',
        hottest: { label: 'Процессор', tempC: 60, level: 'ok' },
        uptimeS: 100000, cpuLoadPerc: 5, ramUsedPerc: 30,
      }),
      node({
        key: 'ai', title: 'ai', roles: ['text'], engines: ['main'], level: 'warn',
        hottest: { label: 'Карта 2', tempC: 98, level: 'warn' },
        gpus: [{ uuid: 'gpu-b', memTempC: 98, level: 'warn', powerCap: false, thermal: false }],
      }),
      node({
        key: 'video', title: 'video', roles: ['text', 'media'], engines: ['comfy'], media: true, level: 'danger',
        hottest: { label: 'Диск кэша', tempC: 83, level: 'danger' },
      }),
    ],
  };
}

// Тот же состав, но ai молчит (down): его уровня нет (level 'ok' — нештатность говорит
// status/since), опасности на ферме нет (video — warn). Плашка фермы не может сказать
// «всё в норме».
function farmSilent(): HardwareFarm {
  const f = farmThree();
  const ai = f.farmNodes[1];
  ai.status = 'down'; ai.since = SINCE; ai.level = 'ok';
  ai.hottest = { label: 'Карта 2', tempC: 98, level: 'ok' }; // последнее значение
  const video = f.farmNodes[2];
  video.level = 'warn';
  video.hottest = { label: 'Диск кэша', tempC: 76, level: 'warn' };
  f.level = 'warn';
  return f;
}

function render(farm: HardwareFarm, selected: string | null = null, isMobile = false, engineLines?: Record<string, string>): string {
  return renderToStaticMarkup(
    createElement(FarmSummaryCard, { farm, selected, onSelect: () => {}, isMobile, engineLines }),
  );
}

// === 1. Роли словами (чистая функция и разметка карточек) ===

describe('farmRolesLabel — роли словами', () => {
  it('ролей нет (worker без движков и без секции) — «CCS»', () => {
    expect(farmRolesLabel([])).toBe('CCS');
  });
  it('text → «LLM», media → «медиа», обе → «медиа и LLM»', () => {
    expect(farmRolesLabel(['text'])).toBe('LLM');
    expect(farmRolesLabel(['media'])).toBe('медиа');
    expect(farmRolesLabel(['text', 'media'])).toBe('медиа и LLM');
  });
});

describe('карточка «Ферма» — состав и роли', () => {
  it('ферма ok/warn/danger: имена узлов и роли словами в разметке', () => {
    const html = render(farmThree());
    expect(html).toContain('Ферма');
    expect(html).toContain('worker');
    expect(html).toContain('CCS');
    expect(html).toContain('LLM');
    expect(html).toContain('медиа и LLM');
  });
  it('строка «горячо» — по худшему уровню отвечающих, имя узла жирным', () => {
    const html = render(farmThree());
    expect(html).toContain('Опасно — ');
    expect(html).toContain('video');
    expect(html).toContain('· диск кэша: 83 °C');
  });
});

// === 2. Молчащий узел: статус, кольцо, плашка «узел молчит» ===

describe('молчащий узел (down/stale/incompatible)', () => {
  it('down: «не отвечает с HH:MM:SS» (локальное время из since), точка-кольцо, плашка «узел молчит», «всё в норме» нет', () => {
    const html = render(farmSilent());
    expect(html).toContain(`не отвечает с ${hms(SINCE)}`);
    // точка-кольцо: заполнение C.track, обводка C.textMuted
    expect(html).toContain('fill="var(--c-track)"');
    expect(html).toContain('stroke="var(--c-text-muted)"');
    expect(html).toContain('узел молчит');
    expect(html).not.toContain('всё в норме');
    // тревогой не красится: карточка ai без warning/danger-рамки
    expect(html).not.toContain('var(--c-danger-bg)');
  });
  it('incompatible: «версия агента не подходит», не «не отвечает»', () => {
    const farm: HardwareFarm = {
      at: 1763280000000, level: 'ok',
      farmNodes: [node({ key: 'ai', title: 'ai', roles: ['text'], status: 'incompatible', since: SINCE })],
    };
    const html = render(farm);
    expect(html).toContain('версия агента не подходит');
    expect(html).not.toContain('не отвечает');
  });
  it('несколько молчащих — «узлы молчат»', () => {
    const f = farmSilent();
    f.farmNodes[0].status = 'stale'; f.farmNodes[0].since = SINCE;
    f.farmNodes[0].level = 'ok'; f.farmNodes[0].hottest = null;
    const html = render(f);
    expect(html).toContain('узлы молчат');
    expect(html).not.toContain('всё в норме');
  });
  it('farmSilentCount считает down/stale/incompatible', () => {
    expect(farmSilentCount(farmThree())).toBe(0);
    expect(farmSilentCount(farmSilent())).toBe(1);
  });
});

// === 3. Строка «горячо» не берёт значения молчащих узлов ===

describe('farmHottestLine — молчащие исключены', () => {
  it('у молчащего последнее значение 110, у отвечающего 98 — строка с 98, без 110', () => {
    const farm: HardwareFarm = {
      at: 1763280000000, level: 'ok',
      farmNodes: [
        // down: последнее измеренное 110 (level 'ok' — так отдаёт бэк: красится только измеренное)
        node({ key: 'worker', title: 'worker', status: 'down', since: SINCE, hottest: { label: 'Процессор', tempC: 110, level: 'ok' } }),
        node({ key: 'ai', title: 'ai', roles: ['text'], hottest: { label: 'Карта 2', tempC: 98, level: 'ok' } }),
      ],
    };
    const line = farmHottestLine(farm);
    expect(line).toContain('98');
    expect(line).not.toContain('110');
    // Мутация «не исключать молчащие» (убрать !farmNodeSilent(n) в farmHottest):
    // 110 > 98 при равных уровнях → строка с 110 → этот тест краснеет.
  });
  it('при равных уровнях — более высокая температура', () => {
    const farm: HardwareFarm = {
      at: 1763280000000, level: 'warn',
      farmNodes: [
        node({ key: 'ai', title: 'ai', roles: ['text'], level: 'warn', hottest: { label: 'Карта 2', tempC: 98, level: 'warn' } }),
        node({ key: 'video', title: 'video', roles: ['media'], level: 'warn', hottest: { label: 'Ядро карты 1', tempC: 101, level: 'warn' } }),
      ],
    };
    expect(farmHottestLine(farm)).toContain('101');
  });
  it('все молчат — строки нет', () => {
    const farm: HardwareFarm = {
      at: 1763280000000, level: 'ok',
      farmNodes: [node({ key: 'worker', title: 'worker', status: 'down', since: SINCE })],
    };
    expect(farmHottestLine(farm)).toBe('');
    expect(render(farm)).not.toContain('Ближе всех к пределу');
  });
});

// === 4. Выбор узла: окраска и метки ===

describe('выбор узла', () => {
  it('выбранный узел — accentLight/accentMuted ровно на одной карточке; невыбранный — без них', () => {
    const none = render(farmThree());
    expect(none).not.toContain('var(--c-accent-light)');
    expect(none).not.toContain('var(--c-accent-muted)');
    const html = render(farmThree(), 'ai');
    expect((html.match(/var\(--c-accent-light\)/g) ?? []).length).toBe(1);
    expect((html.match(/var\(--c-accent-muted\)/g) ?? []).length).toBe(1);
  });
  it('у каждой карточки узла есть aria-label с именем узла', () => {
    const html = render(farmThree());
    expect(html).toContain('aria-label="Узел worker: CCS"');
    expect(html).toContain('aria-label="Узел ai: LLM"');
    expect(html).toContain('aria-label="Узел video: медиа и LLM"');
    // молчащий — с именем и статусом
    const silent = render(farmSilent());
    expect(silent).toContain(`aria-label="Узел ai: не отвечает с ${hms(SINCE)}"`);
  });
  it('касание карточки вызывает onSelect(key)', async () => {
    const onSelect = vi.fn();
    const { container } = mount(
      createElement(FarmSummaryCard, { farm: farmThree(), selected: null, onSelect, isMobile: false }),
    );
    const card = findNodeByAttr(container, 'aria-label', 'Узел video: медиа и LLM');
    expect(card).not.toBeNull();
    const props = reactProps(card!);
    (props!.onClick as (e: unknown) => void)({});
    expect(onSelect).toHaveBeenCalledWith('video');
  });
});

// === 5. Раскладка: десктоп — сетка в ряд, мобила (320) — список строк ===

describe('раскладка', () => {
  it('десктоп: карточки в ряд по числу узлов', () => {
    expect(render(farmThree())).toContain('grid-template-columns:repeat(3, minmax(0, 1fr))');
  });
  it('мобила: список строк (одна колонка), содержимое то же', () => {
    const html = render(farmThree(), null, true);
    expect(html).toContain('grid-template-columns:1fr');
    expect(html).toContain('медиа и LLM');
    expect(html).toContain('Опасно — ');
  });
});

// === 6. Строка движка приходит из engineLines (её составляет Ф3) ===

describe('engineLines', () => {
  it('строка есть — в карточке; её нет — строки движка нет', () => {
    const withLine = render(farmThree(), null, false, { ai: '41 ток/с · KV 73 %' });
    expect(withLine).toContain('41 ток/с · KV 73 %');
    expect(render(farmThree())).not.toContain('41 ток/с');
  });
});
