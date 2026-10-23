// Выбор фичи (FeaturePicker): только при ≥ 2 фичах; секция «Фичи чата · N»;
// role="radiogroup"/"radio". F-pop-a: точка `●` «эту фичу показывает пилюля» убрана
// (порядок фич — с бэка).
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

const render = (runs: FeatureRun[], selected: number, isMobile = false, inMenu = false) =>
  renderToStaticMarkup(createElement(FeaturePicker, { runs, selected, onSelect: () => {}, isMobile, inMenu }));

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

  it('F-pop-a: точки ● «эту фичу показывает пилюля» больше нет (порядок — с бэка)', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0);
    // ● (U+25CF) отсутствует; и title-подсказки тоже.
    expect(html.match(/●/g) ?? []).toHaveLength(0);
    expect(html).not.toContain('Эту фичу показывает пилюля');
  });

  it('выбранный чип: aria-checked=true, рамка accentMuted, 600', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 1);
    // Выбранная (index 1) — aria-checked="true".
    expect(html).toContain('aria-checked="true"');
    // Рамка выбранного — C.accentMuted (var(--c-accent-muted)).
    expect(html).toContain('var(--c-accent-muted)');
  });

  it('имя фичи — title = полное название (текст чипа обрезается многоточием)', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0);
    expect(html).toContain('title="Фича A"');
    expect(html).toContain('title="Фича B"');
  });

  it('мобила (360 px): одна строка (nowrap), overflowX auto, скроллбар скрыт, чип maxWidth 220', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B')], 0, true);
    expect(html).toContain('flex-wrap:nowrap');
    expect(html).toContain('overflow-x:auto');
    expect(html).toContain('scrollbar-width:none');
    expect(html).toContain('max-width:220px');
  });

  // r-1: меню (десктоп, 380) — чип 160 (два в ряд) + потолок пикера два ряда (maxHeight 58)
  // с вертикальной прокруткой. Мутация: верни maxWidth 220 / убери maxHeight → краснеет.
  it('r-1: меню (десктоп) — чип maxWidth 160 (два в ряд) + потолок два ряда (maxHeight 58, overflowY auto)', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B'), run('c', 'Фича C')], 0, false, true);
    // Чип в меню — 160 (не 220): два влезут в ряд при 380.
    expect(html).toContain('max-width:160px');
    // Потолок пикера — два ряда (58) + вертикальная прокрутка внутри.
    expect(html).toContain('max-height:58px');
    expect(html).toContain('overflow-y:auto');
  });

  it('карточка (десктоп, не меню): чип maxWidth 220, без потолка (все чипы видны)', () => {
    const html = render([run('a', 'Фича A'), run('b', 'Фича B'), run('c', 'Фича C')], 0, false, false);
    expect(html).toContain('max-width:220px');
    expect(html).not.toContain('max-height:58px');
    expect(html).not.toContain('overflow-y:auto');
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

  // state (бэк Б1a/Б1b) — источник правды глифа: merge_pending → ромб (wait),
  // merging → кольцо (run), merged → галочка (done). Мутация «глиф по attention.kind
  // вместо state» краснит: runs без attention, но с state=merge_pending.
  it('глиф по state: merge_pending → ромб, merging → кольцо, merged → галочка', () => {
    const html = render([
      run('a', 'Фича A', { state: 'merge_pending' }),
      run('b', 'Фича B', { state: 'merging' }),
      run('c', 'Фича C', { state: 'merged' }),
    ], 0);
    // Ромб (wait, merge_pending): path d="M8 1.5 14.5 8 8 14.5 1.5 8Z".
    expect(html).toContain('M8 1.5 14.5 8 8 14.5 1.5 8Z');
    // Кольцо (run, merging): два circle (r 6.5 stroke + r 3.5 fill).
    expect(html).toContain('r="3.5"');
    // Галочка (done, merged): path d="M4.8 8.2 7 10.3l4.3-4.6".
    expect(html).toContain('M4.8 8.2 7 10.3l4.3-4.6');
  });
});
