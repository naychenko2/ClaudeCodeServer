// Мини-рельса фичи (FeatureRail) — «конвейер, положенный набок»: 8 сегментов
// по rail[] индекса. Рельса «глупая»: состояния (state/tone) и признак «закрыта»
// даёт бэк, фронт только раскладывает.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR). Компонент чистый
// (без хуков/контекста), поэтому достаточно статического рендера и проверки HTML.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRailSegment, FeatureRailKey } from '../../../types/featureIndex';
import { FeatureRail } from './FeatureRail';

// 8 стадий в порядке жизненного цикла (cut → design → plan → … → merge); design —
// необязательна (без задач бэк отдаёт skipped). `over` — точечные переопределения
// по ключу стадии.
const KEYS: FeatureRailKey[] = ['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function rail(over: Partial<Record<FeatureRailKey, Partial<FeatureRailSegment>>> = {}): FeatureRailSegment[] {
  const base: Record<FeatureRailKey, FeatureRailSegment> = {
    cut:    { key: 'cut',    state: 'done',    tone: null },
    design: { key: 'design', state: 'pending', tone: null },
    plan:   { key: 'plan',   state: 'done',    tone: null },
    prod:   { key: 'prod',   state: 'active',  tone: 'run' },
    review: { key: 'review', state: 'pending', tone: null },
    qa:     { key: 'qa',     state: 'pending', tone: null },
    final:  { key: 'final',  state: 'pending', tone: null },
    merge:  { key: 'merge',  state: 'pending', tone: null },
  };
  return KEYS.map(k => ({ ...base[k], ...over[k] }));
}

function render(r: FeatureRailSegment[], closed = false): string {
  return renderToStaticMarkup(createElement(FeatureRail, { rail: r, closed }));
}

// Число сегментов: каждый <i> несёт ровно один title; span-контейнер title не имеет.
function segCount(html: string): number {
  return (html.match(/title="/g) ?? []).length;
}

describe('FeatureRail', () => {
  it('рисует ровно 8 сегментов', () => {
    expect(segCount(render(rail()))).toBe(8);
  });

  it('активный сегмент длиннее (15px), остальные 8px', () => {
    const html = render(rail()); // prod — active
    // ровно один сегмент шириной 15px (активный), остальные 8px.
    expect((html.match(/width:15px/g) ?? []).length).toBe(1);
    expect((html.match(/width:8px/g) ?? []).length).toBe(7);
  });

  it('цвет активного сегмента — по tone', () => {
    expect(render(rail()).includes('var(--c-text-heading)')).toBe(true); // run → textHeading
    expect(render(rail({ prod: { tone: 'wait' } })).includes('var(--c-info)')).toBe(true); // wait → info
    expect(render(rail({ prod: { tone: 'stopped' } })).includes('var(--c-danger)')).toBe(true); // stopped → danger
    expect(render(rail({ prod: { tone: 'loop' } })).includes('var(--c-warning)')).toBe(true); // loop → warning
  });

  it('пройденный — success, впереди — progressTrack, «нет в трекере» — пунктирный контур', () => {
    const html = render(rail({ review: { state: 'absent' } }));
    // done (cut/plan) → success
    expect((html.match(/background:var\(--c-success\)/g) ?? []).length).toBe(2);
    // pending (design/qa/final/merge) → progressTrack
    expect((html.match(/background:var\(--c-progress-track\)/g) ?? []).length).toBe(4);
    // absent (review) → прозрачный + inset пунктирный контур
    expect(html.includes('box-shadow:inset 0 0 0 1px var(--c-dashed)')).toBe(true);
  });

  it('закрытая фича: вся рельса приглушена (textMuted, opacity .5)', () => {
    const html = render(rail(), true);
    // все 8 сегментов — textMuted + opacity .5 (absent — тоже, только контур).
    expect((html.match(/background:var\(--c-text-muted\)/g) ?? []).length).toBe(8);
    expect((html.match(/opacity:0\.5/g) ?? []).length).toBe(8);
  });

  it('skipped (необязательная design без задач) — «не было», нейтральная заливка', () => {
    const html = render(rail({ design: { state: 'skipped' } }));
    // title: «Дизайн — не было» — не «впереди» и не «пройден».
    expect(html.includes('title="Дизайн — не было"')).toBe(true);
    // нейтральная заливка C.bgInset: не progressTrack (pending — review/qa/final/merge)
    // и не success.
    expect(html.includes('background:var(--c-bg-inset)')).toBe(true);
    expect((html.match(/background:var\(--c-progress-track\)/g) ?? []).length).toBe(4);
    expect((html.match(/background:var\(--c-success\)/g) ?? []).length).toBe(2);
  });

  it('закрытая фича со skipped: «не было», а не «пройден» (не textMuted)', () => {
    // Бэк держит skipped и у закрытой фичи (FeatureProjector.cs:1626).
    const html = render(rail({ design: { state: 'skipped' } }), true);
    expect(html.includes('title="Дизайн — не было"')).toBe(true);
    // design остаётся нейтральной (bgInset), остальные 7 — textMuted; все под opacity .5.
    expect((html.match(/background:var\(--c-bg-inset\)/g) ?? []).length).toBe(1);
    expect((html.match(/background:var\(--c-text-muted\)/g) ?? []).length).toBe(7);
    expect((html.match(/opacity:0\.5/g) ?? []).length).toBe(8);
  });

  it('aria-label: активный этап / закрыта / не начат', () => {
    // активный prod → «Этап: Производство»
    expect(render(rail()).includes('aria-label="Этап: Производство"')).toBe(true);
    // закрыта → «Этап: закрыта»
    expect(render(rail(), true).includes('aria-label="Этап: закрыта"')).toBe(true);
    // активного нет (все done/pending/absent) → «Этап: не начат»
    const noActive = rail({ prod: { state: 'done', tone: null } });
    expect(render(noActive).includes('aria-label="Этап: не начат"')).toBe(true);
  });

  it('опция label: «Этап: смержена» вместо «Этап: закрыта» у merged', () => {
    const html = renderToStaticMarkup(createElement(FeatureRail, { rail: rail(), closed: true, label: 'Этап: смержена' }));
    expect(html.includes('aria-label="Этап: смержена"')).toBe(true);
    expect(html.includes('aria-label="Этап: закрыта"')).toBe(false);
  });

  it('пустой и короткий rail не падают', () => {
    // Пустой: 0 сегментов, aria-label «не начат», не бросает.
    expect(() => render([])).not.toThrow();
    expect(segCount(render([]))).toBe(0);
    expect(render([]).includes('aria-label="Этап: не начат"')).toBe(true);
    // Короткий (3 сегмента): ровно 3, не падает.
    expect(() => render(rail().slice(0, 3))).not.toThrow();
    expect(segCount(render(rail().slice(0, 3)))).toBe(3);
  });
});
