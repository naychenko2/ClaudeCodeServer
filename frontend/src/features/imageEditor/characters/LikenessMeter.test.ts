// Подпись третьего деления шкалы: во время обучения — «Обучается · N %» (спека §10.2), иначе — из METER.stops.
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { characterCardView, type CardViewInput } from './characterCardView';
import { LikenessMeter } from './LikenessMeter';
import type { TrainSectionView } from './loraTrainView';

const form = { kind: 'form', label: '', disabled: false, reason: null, stopped: false, photosChanged: false } as TrainSectionView;
const html = (view: TrainSectionView) => {
  const i: CardViewInput = { photos: 5, train: 25, view, versions: [], own: null };
  return renderToStaticMarkup(createElement(LikenessMeter, { stops: characterCardView(i).stops }));
};
const running = (percent: number | null): TrainSectionView => ({ kind: 'running', text: '', percent, etaText: null });

describe('LikenessMeter', () => {
  it('идёт обучение: «Обучается · 40%», «Заметно похож» нет', () => {
    const h = html(running(40));
    expect(h).toContain('Обучается · 40%');
    expect(h).not.toContain('Заметно похож');
  });
  it('процент неизвестен — «Обучается» без числа', () => {
    const h = html(running(null));
    expect(h).toContain('Обучается');
    expect(h).not.toContain('Обучается ·');
  });
  it('вне обучения третье деление — «Заметно похож»', () => {
    expect(html(form)).toContain('Заметно похож');
  });
});
