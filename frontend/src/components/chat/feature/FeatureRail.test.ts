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
import type { FeatureQueue } from '../../../types/featureRun';
import { queueLabelParts } from '../../../lib/chatGroups';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
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

// Рендер с waiting (фича в очереди) и местом в очереди с бэка.
function renderQ(r: FeatureRailSegment[], waiting: boolean, queue?: FeatureQueue | null): string {
  return renderToStaticMarkup(createElement(FeatureRail, { rail: r, waiting, queue }));
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

// Очередь фич (макет feature-queue-1): при waiting активный сегмент — C.textMuted
// вместо цвета тона; к aria-label добавляется «· в очереди N из M» (позицию считает
// бэк). Без waiting поведение прежнее (регрессия тестов выше).
describe('FeatureRail: очередь фич (waiting)', () => {
  const Q: FeatureQueue = { position: 2, length: 3 };

  it('waiting: активный сегмент — textMuted вместо цвета тона, длина 15 сохранена', () => {
    const html = renderQ(rail(), true, Q); // prod — active, tone run
    // active → textMuted, не textHeading (run).
    expect(html).toContain('width:15px;background:var(--c-text-muted)');
    expect(html).not.toContain('var(--c-text-heading)');
    // остальные сегменты без изменений: done → success (cut/plan), pending → progressTrack (design/review/qa/final/merge).
    expect((html.match(/background:var\(--c-success\)/g) ?? []).length).toBe(2);
    expect((html.match(/background:var\(--c-progress-track\)/g) ?? []).length).toBe(5);
  });

  it('waiting=false/без пропа: активный — по-прежнему в цвете тона (регрессия)', () => {
    const html = renderQ(rail(), false, Q);
    expect(html).toContain('var(--c-text-heading)'); // run → textHeading, как раньше
    expect(html).not.toContain('width:15px;background:var(--c-text-muted)');
  });

  it('waiting: aria-label «Этап: {этап} · в очереди N из M»', () => {
    expect(renderQ(rail(), true, Q)).toContain('aria-label="Этап: Производство · в очереди 2 из 3"');
  });

  it('waiting без queue (бэк не отдал) или с нулями: «· в очереди» без цифр', () => {
    // Нет queue (null/undefined) и нули — цифры не показываем (макет: «Нули не показываем»).
    for (const q of [null, undefined, { position: 0, length: 3 }, { position: 2, length: 0 }]) {
      expect(renderQ(rail(), true, q)).toContain('aria-label="Этап: Производство · в очереди"');
    }
  });

  it('без waiting: прежний aria-label, суффикса нет (регрессия)', () => {
    expect(renderQ(rail(), false, Q)).toContain('aria-label="Этап: Производство"');
    // активного нет — «не начат» без суффикса
    expect(renderQ(rail({ prod: { state: 'done', tone: null } }), false, Q)).toContain('aria-label="Этап: не начат"');
  });

  it('waiting и активного нет: «Этап: не начат · в очереди N из M»', () => {
    // Фича в очереди, ещё не начатая: очередь видна в подписи, даже когда этапа нет.
    expect(renderQ(rail({ prod: { state: 'pending', tone: null } }), true, Q)).toContain('aria-label="Этап: не начат · в очереди 2 из 3"');
  });

  it('closed важнее waiting: «Этап: закрыта», приглушение прежнее', () => {
    // Закрытая фича в очереди быть не может (макет: «В очередь» нет у merged/closed) —
    // closed держит и подпись, и приглушение всей рельсы.
    const html = renderToStaticMarkup(createElement(FeatureRail, { rail: rail(), closed: true, waiting: true, queue: Q }));
    expect(html).toContain('aria-label="Этап: закрыта"');
    expect((html.match(/background:var\(--c-text-muted\)/g) ?? []).length).toBe(8);
  });
});

// === R-Q5D-E: рельса берёт aria-суффикс из структурного API хелпера ===
// Потребитель не разбирает строку подписи и не держит своей копии формата «N из M»:
// суффикс приходит готовым полем queueLabelParts.ariaSuffix (единственный источник —
// chatGroups). Мутация (в отчёте): вернуть в рельсу разбор строки по разделителю →
// сторож краснеет; сдвинуть суффикс на свой текст → краснеют оба теста.

describe('FeatureRail: R-Q5D-E — подпись из queueLabelParts', () => {
  it('aria-суффикс рельсы = queueLabelParts(queue).ariaSuffix', () => {
    const q: FeatureQueue = { position: 2, length: 3 };
    const html = renderQ(rail(), true, q);
    expect(html).toContain(`aria-label="Этап: Производство${queueLabelParts(q).ariaSuffix}"`);
    // Без цифр — тоже из хелпера (null/нули).
    const htmlNull = renderQ(rail(), true, null);
    expect(htmlNull).toContain(`aria-label="Этап: Производство${queueLabelParts(null).ariaSuffix}"`);
  });

  it('в исходнике рельсы нет разбора строки подписи и своей копии «N из M»', () => {
    const src = readFileSync(fileURLToPath(new URL('./FeatureRail.tsx', import.meta.url)), 'utf8');
    expect(src, 'рельса разбирает строку подписи по разделителю').not.toContain("indexOf(' · ')");
    expect(src, 'рельса берёт строку подписи вместо частей').not.toMatch(/\bqueueLabel\(/);
    expect(src, 'рельса собирает своё «N из M»').not.toMatch(/\$\{[^}]*position/);
    expect(src, 'рельса собирает своё «N из M»').not.toMatch(/\$\{[^}]*length/);
  });
});
