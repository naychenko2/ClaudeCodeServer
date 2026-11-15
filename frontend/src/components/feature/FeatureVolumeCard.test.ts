// Тесты карточки «Объём» (FS-FV): число «N из M», сегменты полосы по корзинам,
// пустой знаменатель, честность плана, ссылка «из чего ›» и отсутствие процентов.
// Плюс строки «план расширен: было N, стало M» и «Доработки: N из M» (FS-FVp) —
// появление только по факту роста/доработок и порядок: число → расширен → доработки → хвост.
// Окружение node: разметка — renderToStaticMarkup, клик — fakeDom.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FeatureVolumeCard } from './FeatureVolumeCard';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';
import type { StepProgress } from '../../lib/featureProgress';

const HERE = dirname(fileURLToPath(import.meta.url));

function progress(over: Partial<StepProgress> = {}): StepProgress {
  return {
    planState: 'cut', total: 12, accepted: 7, review: 1, work: 3, notCreated: 1,
    withdrawn: 0, unlinkedReworks: 0, ...over,
  };
}

const html = (p: StepProgress, isMobile = false, onOpenSteps?: () => void): string =>
  renderToStaticMarkup(createElement(FeatureVolumeCard, { progress: p, isMobile, onOpenSteps }));

const segCount = (h: string): number => (h.match(/data-testid="volume-seg"/g) ?? []).length;

describe('FeatureVolumeCard', () => {
  it('7 из 12: крупное число и четыре сегмента полосы', () => {
    const h = html(progress());
    expect(h).toContain('7 из 12');
    expect(h).toContain('шагов приняты');
    expect(segCount(h)).toBe(4);
    expect(h).toContain('ещё 4 в работе, 1 не заведён');
  });

  it('нулевой сегмент не рисуется', () => {
    const h = html(progress({ accepted: 5, review: 0, work: 0, notCreated: 0, total: 5 }));
    expect(segCount(h)).toBe(1);
    expect(h).not.toContain('volume-caption');
  });

  it('склонение: несколько не заведено', () => {
    expect(html(progress({ notCreated: 2, total: 13 }))).toContain('2 не заведено');
  });

  it('total = 0: «шагов пока нет», полосы и ссылки нет', () => {
    const h = html(progress({ total: 0, accepted: 0, review: 0, work: 0, notCreated: 0 }), false, () => {});
    expect(h).toContain('шагов пока нет');
    expect(segCount(h)).toBe(0);
    expect(h).not.toContain('volume-bar');
    expect(h).not.toContain('volume-open-steps');
  });

  it('planState не разрезан: строка про неполный план', () => {
    expect(html(progress({ planState: 'partial' }))).toContain('план разрезан не весь');
    expect(html(progress({ planState: 'pending' }))).toContain('план разрезан не весь');
    expect(html(progress({ planState: 'cut' }))).not.toContain('план разрезан не весь');
    expect(html(progress({ planState: 'done' }))).not.toContain('план разрезан не весь');
  });

  it('ссылка «из чего N ›» — только при onOpenSteps; мобила — тач-цель ≥40', () => {
    expect(html(progress())).not.toContain('volume-open-steps');
    expect(html(progress(), true, () => {})).toContain('из чего 12 ›');
    expect(html(progress(), true, () => {})).toContain('min-height:40px');
  });

  it('мобила: число крупностью FS.xl (18), десктоп — FS.h2 (22)', () => {
    expect(html(progress(), true)).toContain('font-size:18px');
    expect(html(progress(), false)).toContain('font-size:22px');
  });

  it('план расширен: «было N, стало M» — только когда знаменатель вырос (FS-FVp)', () => {
    const grew = html(progress({ total: 15, baselineTotal: 12 }));
    expect(grew).toContain('план расширен: было 12, стало 15');
    expect(grew).toContain('volume-plan-grew');
    expect(grew).toContain('var(--c-text-muted)');
    // план не рос (total == baseline) — строки нет
    expect(html(progress({ total: 12, baselineTotal: 12 }))).not.toContain('план расширен');
    // базовая не зафиксирована (null) или поля нет (старый ответ) — строки нет
    expect(html(progress({ total: 15, baselineTotal: null }))).not.toContain('план расширен');
    expect(html(progress())).not.toContain('план расширен');
    // знаменатель не пересчитан: число осталось по total бэка
    expect(grew).toContain('7 из 15');
  });

  it('доработки после мержа: «Доработки: N из M» отдельной строкой (FS-FVp)', () => {
    const h = html(progress({ followUpTotal: 3, followUpAccepted: 1 }));
    expect(h).toContain('Доработки: 1 из 3');
    expect(h).toContain('volume-followups');
    // нет доработок (0) или поля нет (старый ответ) — строки нет
    expect(html(progress({ followUpTotal: 0, followUpAccepted: 0 }))).not.toContain('Доработки');
    expect(html(progress())).not.toContain('Доработки');
    // числа без склонений
    expect(html(progress({ followUpTotal: 1, followUpAccepted: 1 }))).toContain('Доработки: 1 из 1');
  });

  it('порядок строк: «N из M» → «план расширен» → «Доработки» → хвост (FS-FVp)', () => {
    const h = html(progress({ total: 15, accepted: 7, baselineTotal: 12, followUpTotal: 3, followUpAccepted: 1 }));
    const at = (s: string) => h.indexOf(s);
    expect(at('7 из 15')).toBeGreaterThanOrEqual(0);
    expect(at('план расширен: было 12, стало 15')).toBeGreaterThan(at('7 из 15'));
    expect(at('Доработки: 1 из 3')).toBeGreaterThan(at('план расширен: было 12, стало 15'));
    expect(at('ещё 4 в работе, 1 не заведён')).toBeGreaterThan(at('Доработки: 1 из 3'));
  });

  it('исходник: нет процентов и вычислений доли', () => {
    const src = readFileSync(join(HERE, 'FeatureVolumeCard.tsx'), 'utf8')
      .split('\n').filter(l => !l.trim().startsWith('//')).join('\n');
    expect(src).not.toContain('%');
    expect(src).not.toMatch(/Math\.round/);
  });
});

describe('FeatureVolumeCard: клик', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('«из чего ›» вызывает onOpenSteps', () => {
    const onOpenSteps = vi.fn();
    const { container } = mount(createElement(FeatureVolumeCard, { progress: progress(), isMobile: false, onOpenSteps }));
    const btn = findNodeByAttr(container, 'data-testid', 'volume-open-steps');
    expect(btn).not.toBeNull();
    (reactProps(btn!)!.onClick as (e: unknown) => void)({});
    expect(onOpenSteps).toHaveBeenCalledTimes(1);
  });
});
