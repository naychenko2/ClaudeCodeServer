// Тесты карточки «Срок» (FS-FE): остаток словами, дата и автор оценки (source=note),
// «по похожим фичам» (source=history), «часы стоят» (wait), «оценка до остановки» (stopped),
// пустое состояние без оценки, «прошло N», «устарела» и отсутствие полосы времени.
// Окружение node: разметка — renderToStaticMarkup.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureEtaCard, type FeatureEtaCardProps } from './FeatureEtaCard';
import type { FeatureEta } from '../../lib/featurePage';

// iso(-60) — час назад: readyAtText даёт «к ЧЧ:ММ» (тот же путь, что у живой оценки,
// идущей меньше 12 ч), agoText — «N ч назад»; ни один вывод не зависит от TZ машины.
const iso = (min: number): string => new Date(Date.now() + min * 60000).toISOString();
const DATE_RE = /к \d{2}:\d{2}/;

const nameOf = (id: string): string | null => (id === 'gleb' ? 'Глеб' : null);

const html = (p: Pick<FeatureEtaCardProps, 'eta'> & Partial<FeatureEtaCardProps>): string =>
  renderToStaticMarkup(createElement(FeatureEtaCard, { nameOf, isMobile: false, ...p }));

const eta = (over: Partial<FeatureEta> = {}): FeatureEta => ({
  minutes: 240, endsAt: null, source: 'history', clockStopped: null, ...over,
});

describe('FeatureEtaCard', () => {
  it('source=note: «≈ 4 ч осталось», дата и «оценка Глеба 2 ч назад»', () => {
    const h = html({
      eta: eta({ endsAt: iso(-60), source: 'note', clockStopped: null }),
      noteAt: iso(-120), noteByPersonaId: 'gleb',
    });
    expect(h).toContain('≈ 4 ч');
    expect(h).toContain('осталось');
    expect(h).toMatch(DATE_RE);
    expect(h).toContain('оценка Глеба 2 ч назад');
    expect(h).not.toContain('по похожим фичам');
  });

  it('source=history: «по похожим фичам», без автора и даты', () => {
    const h = html({ eta: eta() });
    expect(h).toContain('≈ 4 ч');
    expect(h).toContain('по похожим фичам');
    expect(h).not.toContain('оценка');
    expect(h).not.toMatch(DATE_RE);
  });

  it('clockStopped=wait: «часы стоят, пока ждём тебя · даты нет», даты нет даже при endsAt', () => {
    const h = html({ eta: eta({ endsAt: iso(-60), clockStopped: 'wait' }) });
    expect(h).toContain('часы стоят, пока ждём тебя · даты нет');
    expect(h).not.toMatch(DATE_RE);
  });

  it('clockStopped=stopped: дата прежняя и «оценка до остановки»', () => {
    const h = html({ eta: eta({ endsAt: iso(-60), clockStopped: 'stopped' }) });
    expect(h).toMatch(DATE_RE);
    expect(h).toContain('оценка до остановки');
    expect(h).not.toContain('часы стоят');
  });

  it('eta = null: «срок —» с title «мало похожих фич», без числа', () => {
    const h = html({ eta: null });
    expect(h).toContain('срок —');
    expect(h).toContain('title="мало похожих фич"');
    expect(h).not.toContain('≈');
    expect(h).not.toContain('осталось');
  });

  it('noteStale: «· устарела» жёлтым; без stale — нет', () => {
    const stale = html({ eta: eta({ source: 'note' }), noteAt: iso(-120), noteByPersonaId: 'gleb', noteStale: true });
    expect(stale).toContain('· устарела');
    expect(stale).toContain('var(--c-warning-text)');
    expect(html({ eta: eta({ source: 'note' }), noteAt: iso(-120), noteByPersonaId: 'gleb' })).not.toContain('устарела');
  });

  it('«прошло N» — отдельной строкой только при положительном elapsedMinutes', () => {
    expect(html({ eta: eta(), elapsedMinutes: 300 })).toContain('прошло 5 ч');
    expect(html({ eta: eta(), elapsedMinutes: 0 })).not.toContain('прошло');
    expect(html({ eta: eta(), elapsedMinutes: null })).not.toContain('прошло');
  });

  it('полосы времени и строки «из ≈ 3 д» нет ни при каком eta', () => {
    for (const e of [eta(), eta({ source: 'note', clockStopped: 'stopped' }), null] as (FeatureEta | null)[]) {
      const h = html({ eta: e });
      expect((h.match(/data-testid="eta-(time-)?bar"/g) ?? []).length).toBe(0);
      expect(h).not.toContain('из ≈');
    }
  });

  it('мобила: число крупностью FS.xl (18), десктоп — FS.h2 (22)', () => {
    expect(html({ eta: eta(), isMobile: true })).toContain('font-size:18px');
    expect(html({ eta: eta(), isMobile: false })).toContain('font-size:22px');
  });
});
