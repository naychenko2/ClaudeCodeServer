// Выбор фичи (FeaturePicker): только при ≥ 2 фичах; секция «Фичи чата · N»;
// role="radiogroup"/"radio"; точка `●` (C.accent) — у фичи, которую показывает пилюля (runs[0]).
// Рендер — renderToStaticMarkup (паттерн соседних тестов; окружение node, useState — initial).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import { FeaturePicker } from './FeaturePicker';

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

function run(id: string, title: string, over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: id,
    epic: task({ id, title, worktreeBranch: `feat/${id}` }),
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  };
}

const render = (runs: FeatureRun[], selected: number, isMobile = false) =>
  renderToStaticMarkup(createElement(FeaturePicker, { runs, selected, onSelect: () => {}, isMobile }));

describe('FeaturePicker', () => {
  it('одна фича → пикера нет (null)', () => {
    const html = render([run('a', 'Фича A')], 0);
    // null рендерится как пустая строка
    expect(html).toBe('');
  });

  it('две фичи → секция «Фичи чата · 2» + radiogroup', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0);
    expect(html).toContain('Фичи чата · 2');
    expect(html).toContain('role="radiogroup"');
    expect(html).toContain('role="radio"');
  });

  it('точка ● — у runs[0] (её показывает пилюля), с title «Эту фичу показывает пилюля»', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0);
    // ● (U+25CF) присутствует ровно один раз — у runs[0].
    expect((html.match(/●/g) ?? []).length).toBe(1);
    expect(html).toContain('Эту фичу показывает пилюля');
  });

  it('выбранный чип: aria-checked=true, рамка accentMuted, 600', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 1);
    // Выбранная (index 1) — aria-checked="true".
    expect(html).toContain('aria-checked="true"');
    // Рамка выбранного — C.accentMuted (var(--c-accent-muted)).
    expect(html).toContain('var(--c-accent-muted)');
  });

  it('имя фичи — title = ветка', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0);
    expect(html).toContain('title="feat/a"');
    expect(html).toContain('title="feat/b"');
  });

  it('мобила (360 px): одна строка (nowrap), overflowX auto, скроллбар скрыт, чип maxWidth 220', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0, true);
    expect(html).toContain('flex-wrap:nowrap');
    expect(html).toContain('overflow-x:auto');
    expect(html).toContain('scrollbar-width:none');
    expect(html).toContain('max-width:220px');
  });

  it('глиф по состоянию: wait → ромб, closed → круг с чертой', () => {
    // runs[0] — wait (ромб), runs[1] — closed (круг с чертой).
    const html = render([
      run('a', 'Фича A', { attention: { kind: 'wait', reason: 'мерж' } }),
      run('b', 'Фича B', { closed: true }),
    ], 0);
    // Ромб (wait): path d="M8 1.5 14.5 8 8 14.5 1.5 8Z".
    expect(html).toContain('M8 1.5 14.5 8 8 14.5 1.5 8Z');
    // Круг с чертой (closed): circle + path d="M5 8h6".
    expect(html).toContain('M5 8h6');
  });
});
