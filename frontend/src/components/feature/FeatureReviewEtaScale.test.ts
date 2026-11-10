// Тесты блока «Шкала срока» секции «Время и срок» вкладки «Разбор» (FS-FR1b): четыре строки
// в порядке записки §5, общая шкала 0…max из показанных значений, «обещали» — риска, имя
// оценщика в строке «Сейчас осталось», «Согласие оценок» через ReviewKv, без данных — блока
// нет. Окружение node: renderToStaticMarkup.
//
// Мутации (краснеющие тесты — по фактическим прогонам в копии дерева, 11.11):
// (1) «сейчас» взят из brief.estimate.remainingMinutes вместо note.estimate.etaMinutes —
//     краснеют 8 тестов: все «четыре строки…», оба теста «Сейчас осталось» (число «6 дн 22 ч»
//     вместо «2 дн»), «Обещали — риска» (шкала сдвигается) и «проценты полос»;
// (2) у «Обещали на старте» tick:false (риска становится полосой 0…initial) — краснеют ровно
//     «Обещали — риска на точке шкалы» и «шкала по одним «обещали»»;
// (3) max шкалы взят из левых краёв полос (from) вместо правых (to) — краснеют «Обещали —
//     риска», «проценты полос» и «знак шкалы».
// После отката каждой мутации — зелёные (15/15).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewEtaScale } from './FeatureReviewEtaScale';
import { formatDuration } from './FeatureStatsBits';
import type { FeatureBrief, FeatureBriefEstimate, FeatureBriefNote } from '../../lib/featurePage';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

const nameOf = (id: string): string | null => (id === 'p-sofya' ? 'Софья' : null);

// Числа фикстуры различаются во всех четырёх источниках: подмена источника строки меняет
// текст и проценты (мутация (1) краснеет), а max шкалы задан правым краем истории (8640).
function mkEstimate(over: Partial<FeatureBriefEstimate> = {}): FeatureBriefEstimate {
  return { remainingMinutes: 9999, p25: 4320, p75: 8640, source: 'history', sampleSize: 44, ...over };
}

function mkNote(over: Partial<FeatureBriefNote> = {}): FeatureBriefNote {
  return {
    text: 'голос', at: '2026-11-10T10:00:00Z', byPersonaId: 'p-sofya', byUser: false,
    stageAtWrite: 'step', stale: false,
    estimate: { remaining: ['шаг 4б'], etaMinutes: 2880, why: null, agreement: 'above', etaEndsAt: null },
    ...over,
  };
}

function mkBrief(over: Partial<FeatureBrief> = {}): FeatureBrief {
  return {
    kind: 'running', now: null, reason: null,
    done: { stepsAccepted: 3, stepsTotal: 10, loops: 1, planState: 'partial' },
    next: [], elapsedMinutes: 500,
    estimate: mkEstimate(), queue: null, note: mkNote(),
    initialEtaMinutes: 1440, computedAt: '2026-11-10T12:00:00Z',
    ...over,
  };
}

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  const totals: FeatureTimeline['totals'] = {
    elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
    waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
    errorCount: null, errorMinutes: null,
  };
  return {
    source: 'journal', truncated: false,
    timeline: { from: '2026-10-20T10:00:00Z', to: '2026-10-20T16:00:00Z', stages: [], segments: [], current: null, totals },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null, spend: null,
    typical: {
      count: 5, waitingCount: 0, duration: { median: 1440, p25: 720, p75: 2880 }, waiting: null,
      loops: { median: 1, p25: 0, p75: 2 }, stepCount: 4, stepMinutes: null, tailMinutes: null,
    },
    ...over,
  };
}

const html = (brief: FeatureBrief | null, stats: FeatureStats = mkStats()): string =>
  renderToStaticMarkup(createElement(FeatureReviewEtaScale, { brief, stats, nameOf }));

// Ключ строки и её текст (label + hint + value) — по фрагменту html строки.
function rowTexts(h: string): { key: string; text: string }[] {
  return h.split('data-testid="review-eta-row" data-row="').slice(1).map((chunk) => {
    const key = chunk.slice(0, chunk.indexOf('"'));
    // Текст строки = всё содержимое строки без разметки: подпись, hint, значение
    // (заполнение дорожки — пустой span, в текст не попадает).
    // От первого '>' после data-row= — до закрывающего div строки (внутри только span'ы).
    const body = chunk.slice(chunk.indexOf('>') + 1, chunk.indexOf('</div>'));
    return { key, text: body.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim() };
  });
}

// Отрезок строки шкалы: риска (tick) или полоса (bar) с left/width в процентах.
function segOf(h: string, key: string): { kind: 'tick' | 'bar'; left: number; width: number | null } | null {
  const i = h.indexOf(`data-row="${key}"`);
  if (i < 0) return null;
  const m = h.slice(i).match(/data-testid="review-eta-(tick|bar)" style="[^"]*?left:(\d+)%(?:;width:(\d+)%)?/);
  return m ? { kind: m[1] as 'tick' | 'bar', left: Number(m[2]), width: m[3] === undefined ? null : Number(m[3]) } : null;
}

describe('четыре строки (c1)', () => {
  it('полные данные → ровно четыре строки в порядке «Обещали», «Сейчас», «По истории», «Типичная»', () => {
    expect(rowTexts(html(mkBrief()))).toEqual([
      { key: 'first', text: 'Обещали на старте ≈ 24 ч' },
      { key: 'now', text: 'Сейчас осталось оценил Софья ≈ 2 дн' },
      { key: 'hist', text: 'По истории проекта по 44 фичам 3 дн – 6 дн' },
      { key: 'typ', text: 'Типичная фича разброс 12 ч – 2 дн 24 ч' },
    ]);
  });

  it('без initialEtaMinutes и без typical → две строки: «Сейчас», «По истории»', () => {
    expect(rowTexts(html(mkBrief({ initialEtaMinutes: undefined }), mkStats({ typical: null })))).toEqual([
      { key: 'now', text: 'Сейчас осталось оценил Софья ≈ 2 дн' },
      { key: 'hist', text: 'По истории проекта по 44 фичам 3 дн – 6 дн' },
    ]);
  });

  it('без всех источников → блока нет в DOM (null)', () => {
    const h = html(mkBrief({ initialEtaMinutes: null, estimate: null, note: null }), mkStats({ typical: null }));
    expect(h).toBe('');
  });

  it('note без estimate (старый ответ) — строки «Сейчас» нет', () => {
    expect(rowTexts(html(mkBrief({ note: mkNote({ estimate: null }) })))).toEqual([
      { key: 'first', text: 'Обещали на старте ≈ 24 ч' },
      { key: 'hist', text: 'По истории проекта по 44 фичам 3 дн – 6 дн' },
      { key: 'typ', text: 'Типичная фича разброс 12 ч – 2 дн 24 ч' },
    ]);
  });

  it('0 и null — не значение: нулевых строк нет', () => {
    expect(rowTexts(html(mkBrief({ initialEtaMinutes: 0, estimate: mkEstimate({ p75: 0 }) })))).toEqual([
      { key: 'now', text: 'Сейчас осталось оценил Софья ≈ 2 дн' },
      { key: 'typ', text: 'Типичная фича разброс 12 ч – 2 дн 24 ч' },
    ]);
  });
});

describe('«Сейчас осталось» (c2)', () => {
  it('число — из note.estimate.etaMinutes, не из истории; имя — из nameOf(byPersonaId)', () => {
    const row = rowTexts(html(mkBrief())).find((r) => r.key === 'now')!;
    expect(row.text).toContain(formatDuration(2880));
    expect(row.text).not.toContain(formatDuration(9999));
    expect(row.text).toContain('Софья');
  });

  it('нет резолвера или нет персоны (писал человек) — строки источника нет, число остаётся', () => {
    const h = renderToStaticMarkup(createElement(FeatureReviewEtaScale, { brief: mkBrief(), stats: mkStats() }));
    expect(rowTexts(h).find((r) => r.key === 'now')!.text).toBe('Сейчас осталось ≈ 2 дн');
    expect(rowTexts(html(mkBrief({ note: mkNote({ byPersonaId: null, byUser: true }) })))).toEqual([
      { key: 'first', text: 'Обещали на старте ≈ 24 ч' },
      { key: 'now', text: 'Сейчас осталось ≈ 2 дн' },
      { key: 'hist', text: 'По истории проекта по 44 фичам 3 дн – 6 дн' },
      { key: 'typ', text: 'Типичная фича разброс 12 ч – 2 дн 24 ч' },
    ]);
  });
});

describe('шкала и дорожки', () => {
  it('«Обещали» — риска на точке шкалы (1440/8640 = 17%), не полоса', () => {
    expect(segOf(html(mkBrief()), 'first')).toEqual({ kind: 'tick', left: 17, width: null });
  });

  it('проценты полос — по общей шкале 0…max правых краёв (8640): сейчас 0–33%, история 50–100%, типичная 8–33%', () => {
    const h = html(mkBrief());
    expect(segOf(h, 'now')).toEqual({ kind: 'bar', left: 0, width: 33 });
    expect(segOf(h, 'hist')).toEqual({ kind: 'bar', left: 50, width: 50 });
    expect(segOf(h, 'typ')).toEqual({ kind: 'bar', left: 8, width: 25 });
  });

  it('знак шкалы — под строками: 0…max показанных значений', () => {
    expect(html(mkBrief())).toContain(`шкала: 0 — ${formatDuration(8640)}`);
    expect(html(mkBrief({ estimate: null }), mkStats())).toContain(`шкала: 0 — ${formatDuration(2880)}`);
  });

  it('шкала по одним «обещали»: риска на 100%, знак — по её значению', () => {
    const h = html(mkBrief({ estimate: null, note: null }), mkStats({ typical: null }));
    expect(segOf(h, 'first')).toEqual({ kind: 'tick', left: 100, width: null });
    expect(h).toContain(`шкала: 0 — ${formatDuration(1440)}`);
  });
});

describe('«Согласие оценок» (ReviewKv)', () => {
  it('те же слова, что в «Сроке» сводки (FeatureBriefModel.ts:632-634)', () => {
    for (const [agreement, label] of [
      ['within', 'сходится'], ['above', 'дольше, чем по истории'], ['below', 'быстрее, чем по истории'],
    ] as const) {
      const h = html(mkBrief({ note: mkNote({ estimate: { ...mkNote().estimate!, agreement } }) }));
      expect(h).toContain('Согласие оценок');
      expect(h).toContain(label);
    }
  });

  it('agreement null — строки нет', () => {
    expect(html(mkBrief({ note: mkNote({ estimate: { ...mkNote().estimate!, agreement: null } }) }))).not.toContain('Согласие оценок');
  });
});

describe('мобильная раскладка (360)', () => {
  it('дорожка — своей строкой на всю ширину под подписью (grid-column 1/-1, order 3), поля SP.md', () => {
    const h = renderToStaticMarkup(createElement(FeatureReviewEtaScale, { brief: mkBrief(), stats: mkStats(), nameOf, isMobile: true }));
    expect(h).toContain('grid-column:1 / -1');
    expect(h).toContain('order:3');
    expect(h).toContain('grid-template-columns:1fr 76px');
    expect(h).not.toContain('grid-template-columns:150px 1fr 76px');
  });

  it('без сигнала мобилы — три колонки, дорожка не перенесена', () => {
    const h = html(mkBrief());
    expect(h).toContain('grid-template-columns:150px 1fr 76px');
    expect(h).not.toContain('grid-column:1 / -1');
  });
});
