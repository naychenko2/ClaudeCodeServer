// Ось времени «Хронологии» (GC-F5b). Рендер-тесты на fakeDom: подписи каждые 6 ч
// плюс «сейчас» у открытой фичи, на 360 — только «начало … сейчас», у закрытой —
// последняя подпись дата мержа, слой липкой шапки (Z.panelEdge — ниже поповера),
// время в локальной зоне (та же, что в ленте).
//
// Мутации (проверяются прогоном): (1) TICK_TAIL_GAP_MS → 0 — краснеет «31 ч → 5
// подписей» (хвостовая отметка в 30 ч попадает на «сейчас»); (2) now → axis.to в
// закрытой фиче — краснеет «у закрытой нет “сейчас”»; (3) C.textMuted → C.text —
// краснеет тест токенов; (4) Z.panelEdge → Z.modal — краснеет тест слоя.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { FeatureLaneAxis, buildAxisLabels } from './FeatureLaneAxis';
import { formatClock, formatDateTime } from './FeatureStatsBits';
import type { LaneAxis } from './FeatureLaneModel';
import { C, FONT, FS, Z } from '../../lib/design';

// Окно в 31 ч: от 20.10 00:00 до 21.10 07:00.
const FROM = '2026-10-20T00:00:00.000Z';
const TO = '2026-10-21T07:00:00.000Z';

const LIVE_AXIS: LaneAxis = { from: FROM, to: TO, timeFrac: 0.86 };
const CLOSED_AXIS: LaneAxis = { from: FROM, to: TO, timeFrac: 1 };

// Зона МАШИНЫ не годится: на CI (TZ=UTC) подмена «локальная зона → UTC» неотличима
// от правильного поведения. Фиксируем московскую зону явно — Node подхватывает
// смену process.env.TZ в рантайме.
const ORIGINAL_TZ = process.env.TZ;

beforeEach(() => {
  process.env.TZ = 'Europe/Moscow';
  installFakeDom();
});
afterEach(() => {
  uninstallFakeDom();
  if (ORIGINAL_TZ === undefined) delete process.env.TZ;
  else process.env.TZ = ORIGINAL_TZ;
});

// Минимальный узел фейк-DOM: нужны только атрибуты и потомки.
type FakeNode = {
  getAttribute(name: string): string | null;
  childNodes: FakeNode[];
};

// Все узлы-подписи оси (data-testid lane-axis-label-*).
function labelNodes(container: unknown): { key: string; node: FakeNode }[] {
  const out: { key: string; node: FakeNode }[] = [];
  const walk = (x: FakeNode) => {
    const id = x.getAttribute('data-testid');
    if (typeof id === 'string' && id.startsWith('lane-axis-label-')) {
      out.push({ key: id.slice('lane-axis-label-'.length), node: x });
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container as FakeNode);
  return out;
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

function styleOf(node: unknown): Record<string, unknown> {
  return (reactProps(node as never)!.style ?? {}) as Record<string, unknown>;
}

function mountAxis(axis: LaneAxis | null, now: string | null, isMobile?: boolean) {
  return mount(createElement(FeatureLaneAxis, { axis, now, isMobile }));
}

// GC-F5c: узкая шкала (dense) и отступы под колонки сетки строки этапа.
function mountAxisFull(axis: LaneAxis | null, now: string | null, props: Record<string, unknown>) {
  return mount(createElement(FeatureLaneAxis, { axis, now, ...props }));
}

describe('подписи оси', () => {
  it('окно в 31 ч → 5 подписей и «сейчас»', () => {
    const { container } = mountAxis(LIVE_AXIS, TO);
    const labels = labelNodes(container);
    // 5 подписей (начало + четыре отметки через 6 ч) и отдельно «сейчас».
    // Хвостовая отметка в 30 ч не рисуется: она налезала бы на «сейчас».
    expect(labels.filter(l => l.key !== 'now')).toHaveLength(5);
    expect(labels).toHaveLength(6);
    const now = labels.find(l => l.key === 'now');
    expect(now).toBeTruthy();
    expect(textOf(now!.node)).toBe(`сейчас ${formatClock(TO)}`);
  });

  it('отметки идут через 6 ч от начала окна', () => {
    const labels = buildAxisLabels(LIVE_AXIS, TO, false);
    const ticks = labels.filter(l => /^t\d+$/.test(l.key)).map(l => l.text);
    expect(ticks).toEqual([6, 12, 18, 24].map(h => formatClock(new Date(Date.parse(FROM) + h * 3600_000).toISOString())));
  });

  it('подписи времени — в локальной зоне (не UTC)', () => {
    // Ожидание считаем сами (геттеры Date — локальное время), а не через formatClock:
    // так тест ловит подмену локальной зоны на UTC.
    const local = (iso: string) => {
      const d = new Date(iso);
      return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    };
    const labels = buildAxisLabels(LIVE_AXIS, TO, false);
    const now = labels.find(l => l.key === 'now')!;
    expect(now.text).toBe(`сейчас ${local(TO)}`);
    const tick = labels.find(l => l.key === 't1')!;
    expect(tick.text).toBe(local(new Date(Date.parse(FROM) + 6 * 3600_000).toISOString()));
  });

  it('на 360 — ровно 2 подписи: «начало … сейчас»', () => {
    const { container } = mountAxis(LIVE_AXIS, TO, true);
    const labels = labelNodes(container);
    expect(labels).toHaveLength(2);
    expect(textOf(labels[0]!.node)).toContain('начало');
    expect(textOf(labels[1]!.node)).toContain('сейчас');
  });

  it('у закрытой фичи нет «сейчас», последняя подпись — дата мержа', () => {
    const { container } = mountAxis(CLOSED_AXIS, null);
    const labels = labelNodes(container);
    expect(labels.find(l => l.key === 'now')).toBeUndefined();
    const last = labels[labels.length - 1]!;
    expect(last.key).toBe('end');
    expect(textOf(last.node)).toBe(formatDateTime(TO));
  });

  it('без окна ось не рисуется', () => {
    const { container } = mountAxis(null, null);
    expect(findNodeByAttr(container, 'data-testid', 'lane-axis')).toBeNull();
  });

  it('подписи встают по axisFrac: «сейчас» — на краю шкалы', () => {
    const labels = buildAxisLabels(LIVE_AXIS, TO, false);
    const now = labels.find(l => l.key === 'now')!;
    // now = конец окна → его доля = timeFrac (0.86), дальше — зона будущего.
    expect(now.leftPct).toBeCloseTo(86, 5);
    const tick = labels.find(l => l.key === 't1')!;
    expect(tick.leftPct).toBeCloseTo((6 / 31) * 86, 5);
  });
});

describe('оформление оси', () => {
  it('липкая шапка: sticky, фон, кромка снизу', () => {
    const { container } = mountAxis(LIVE_AXIS, TO);
    const axis = findNodeByAttr(container, 'data-testid', 'lane-axis')!;
    const st = styleOf(axis);
    expect(st.position).toBe('sticky');
    expect(st.top).toBe(0);
    expect(st.background).toBe(C.bgMain);
    expect(st.borderBottom).toBe(`1px solid ${C.borderLight}`);
  });

  it('слой липкой шапки ниже поповера (Z.panelEdge, не Z.modal)', () => {
    const { container } = mountAxis(LIVE_AXIS, TO);
    const axis = findNodeByAttr(container, 'data-testid', 'lane-axis')!;
    const z = styleOf(axis).zIndex as number;
    expect(z).toBe(Z.panelEdge);
    expect(z).toBeLessThan(Z.modal);
  });

  it('подписи моноширинные мелкие, обычные — приглушённые, «сейчас» — акцентный и жирный', () => {
    const { container } = mountAxis(LIVE_AXIS, TO);
    const labels = labelNodes(container);
    const now = labels.find(l => l.key === 'now')!;
    const nowStyle = styleOf(now.node);
    expect(nowStyle.color).toBe(C.accent);
    expect(nowStyle.fontWeight).toBe(700);

    const tick = labels.find(l => l.key === 't1')!;
    const tickStyle = styleOf(tick.node);
    expect(tickStyle.color).toBeUndefined();

    // Шрифт и приглушённый цвет подписей — на шкале (наследуются подписями).
    const scale = findNodeByAttr(container, 'data-testid', 'lane-axis-scale')!;
    const scaleStyle = styleOf(scale);
    expect(scaleStyle.fontFamily).toBe(FONT.mono);
    expect(scaleStyle.fontSize).toBe(FS.xxs);
    expect(scaleStyle.color).toBe(C.textMuted);
  });
});

// === GC-F5c: узкая шкала и отступы под колонки сетки ===
describe('ось: узкая шкала и отступы (GC-F5c)', () => {
  it('dense: часовых отметок нет, «начало» без времени — подписи не наезжают', () => {
    const { container } = mountAxisFull(LIVE_AXIS, TO, { dense: true });
    const labels = labelNodes(container);
    expect(labels.map(l => l.key)).toEqual(['from', 'now']);
    expect(textOf(labels[0]!.node)).toBe('начало 20.10');
  });

  it('без dense часовые отметки есть (широкая шкала)', () => {
    const { container } = mountAxisFull(LIVE_AXIS, TO, {});
    expect(labelNodes(container).map(l => l.key)).toContain('t1');
  });

  it('отступы шкалы под колонку дорожек (как у строки этапа: 24 + 250 + 12 | 104 + 12)', () => {
    const { container } = mountAxisFull(LIVE_AXIS, TO, { insetLeft: 286, insetRight: 116 });
    const scale = findNodeByAttr(container, 'data-testid', 'lane-axis-scale')!;
    const st = styleOf(scale);
    expect(st.marginLeft).toBe(286);
    expect(st.marginRight).toBe(116);
  });
});
