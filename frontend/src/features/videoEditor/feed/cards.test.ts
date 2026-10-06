import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { VideoScene } from '../api';
import { CATALOG, scene, version } from '../mocks';
import { quietView, QuietLineView, LaunchRowView, sceneCardView, SceneCardView, type SceneCardActions } from './SceneCard';

// Дополнение плана 2026-10-02: действие агента рисуется ТОЙ ЖЕ разметкой, что действие человека, —
// карточку строят запись ленты и стор, отличие только в «✦ Claude»

const noop = () => {};
const A: SceneCardActions = { onPick: noop, onPrev: noop, onNext: noop, onTake: noop, onSave: noop, onDownload: noop, onAddToFilm: noop, onReshoot: noop, onWork: noop, onOpenFilm: noop };

const BY = /<span data-by-claude=""[^>]*>.*?<\/span>/;
const render = (s: VideoScene, who: 'human' | 'agent', o: { jobId?: string; record?: Record<string, unknown>; running?: boolean } = {}) => {
  const v = sceneCardView({
    scene: s, jobId: o.jobId, record: o.record ? { ...o.record, initiator: who } : undefined, personal: false, focused: false,
    jobs: o.running ? [{ jobId: 'job-7', sessionId: 'c1', sceneId: s.sceneId, stage: 'running', queuePosition: null, etaSeconds: null, variant: 1, count: 2 }] : [],
    pos: null, catalog: CATALOG,
  });
  const html = o.jobId
    ? renderToStaticMarkup(createElement(LaunchRowView, { v, a: A }))
    : renderToStaticMarkup(createElement(SceneCardView, { v, src: '/clip.mp4', busy: false, a: A }));
  return { v, html };
};

const shot = (who: 'human' | 'agent', running = false) => scene('s1', {
  versions: running ? [] : [version(1, { initiator: who }), version(2, { initiator: who })],
  currentVersionId: running ? undefined : 'ver-2',
  launches: [{ jobId: 'job-7', at: '2026-10-02T15:00:00Z', status: running ? 'running' : 'done', interrupted: false, initiator: who, provider: 'fal', model: 'veo-3.1', count: 2 }],
});
const launchRecord = { sceneId: 's1', jobId: 'job-7', provider: 'fal', model: 'Veo 3.1', count: 2, durationSec: 8, price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing' } };

describe('карточка сцены: «В работе» и «Работать с этой» вместо панели «Видео»', () => {
  const card = (focused: boolean, s: VideoScene) => {
    const v = sceneCardView({ scene: s, personal: false, focused, jobs: [], pos: null, catalog: CATALOG });
    return renderToStaticMarkup(createElement(SceneCardView, { v, src: null, busy: false, a: A }));
  };
  it('сцена без клипа не говорит про панель', () => {
    const html = card(false, (() => { const b = scene('s1', { versions: [], launches: [] }); return { ...b, settings: { ...b.settings, text: '' } }; })());
    expect(html).toContain('в редакторе сцены');
    expect(html).toContain('Работать с этой');
    expect(html).not.toMatch(/в панели|Открыть в панели/);
  });
  it('основная сцена: бейдж «В работе», кнопки «Работать с этой» нет', () => {
    const html = card(true, shot('human'));
    expect(html).toContain('В работе');
    expect(html).not.toContain('Работать с этой');
    expect(html).toContain('Переснять');
  });
});

describe('карточки «Видео»: агент — та же разметка плюс «✦ Claude»', () => {
  it('карточка сцены: плеер, версии, «Сохранить сцену», «В фильм →»', () => {
    const h = render(shot('human'), 'human');
    const a = render(shot('agent'), 'agent');
    expect(h.html).toContain('data-video-player');
    expect(h.html).toContain('2 из 2');
    expect(h.html).toContain('Сохранить сцену');
    expect(h.html).toContain('В фильм →');
    expect(h.v.byClaude).toBe(false);
    expect(a.v.byClaude).toBe(true);
    expect(h.html).not.toMatch(BY);
    expect(a.html).toMatch(BY);
    expect(a.html.replace(BY, '')).toBe(h.html);
  });

  it('карточка запуска: ход съёмки и цена из записи ленты', () => {
    const h = render(shot('human', true), 'human', { jobId: 'job-7', record: launchRecord, running: true });
    const a = render(shot('agent', true), 'agent', { jobId: 'job-7', record: launchRecord, running: true });
    expect(h.html).toContain('Veo 3.1 · 2 вар. · ≈ $3.20');
    // Полоса хода живёт в карточке сцены, строка запуска её не дублирует
    expect(h.html).not.toContain('data-video-card-progress');
    expect(render(shot('human', true), 'human', { running: true }).html).toContain('снимаем 2 варианта');
    expect(a.html).toMatch(BY);
    expect(a.html.replace(BY, '')).toBe(h.html);
  });

  it('готовый запуск агента: строка без плеера, варианты — в карточке сцены', () => {
    const h = render(shot('human'), 'human', { jobId: 'job-7', record: launchRecord });
    const a = render(shot('agent'), 'agent', { jobId: 'job-7', record: launchRecord });
    expect(h.html).not.toContain('data-video-player');
    expect(h.html).toContain('Готово: 2 варианта');
    expect(a.html.replace(BY, '')).toBe(h.html);
  });

  it('одна съёмка — одна полная карточка: плеер только у сцены, у запуска компактная строка', () => {
    const s = shot('human');
    const full = render(s, 'human');
    const row = render(s, 'human', { jobId: 'job-7', record: launchRecord });
    const players = (full.html + row.html).match(/data-video-player/g) ?? [];
    expect(players).toHaveLength(1);
    expect(full.html).toContain('data-video-card="scene"');
    expect(row.html).toContain('data-video-card="launch"');
    expect(row.html).not.toContain('Сохранить сцену');
  });

  it('отменённый запуск: строка «Отменено · деньги не списаны», а не пустая карточка', () => {
    const s = scene('s1', { launches: [{ jobId: 'job-7', at: '2026-10-02T15:00:00Z', status: 'cancelled', interrupted: false, initiator: 'human', provider: 'fal', model: 'veo-3.1', count: 2 }] });
    const row = render(s, 'human', { jobId: 'job-7', record: launchRecord });
    expect(row.html).toContain('Отменено · деньги не списаны');
  });

  it('тихие строки сохранения и сборки фильма', () => {
    for (const [type, data, text] of [
      ['video_saved', { sceneId: 's1', path: 'video/утро/scene-01.mp4' }, 'Сцена сохранена: video/утро/scene-01.mp4'],
      ['video_film_built', { path: 'video/утро/утро.film', file: 'video/утро/film.mp4' }, 'Фильм собран: video/утро/film.mp4'],
    ] as const) {
      const hv = quietView(type, { ...data, initiator: 'human' }, text);
      const av = quietView(type, { ...data, initiator: 'agent' }, text);
      const h = renderToStaticMarkup(createElement(QuietLineView, { v: hv }));
      const a = renderToStaticMarkup(createElement(QuietLineView, { v: av }));
      expect(h).toContain(text);
      expect(h).not.toMatch(BY);
      expect(a).toMatch(BY);
      expect(a.replace(BY, '')).toBe(h);
    }
    expect(quietView('video_film_built', { path: 'video/утро/утро.film' }, '').filmPath).toBe('video/утро/утро.film');
  });

  it('карточка человека = карточка агента: подпись модели из каталога и цена, а не сырой id', () => {
    // запись ленты как её пишет бэкенд: id модели и цена, одинаково у обоих
    const rec = { sceneId: 's1', jobId: 'job-7', provider: 'fal', model: 'veo-3.1', count: 2, durationSec: 8, price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing' } };
    const h = render(shot('human'), 'human', { jobId: 'job-7', record: rec });
    const a = render(shot('agent'), 'agent', { jobId: 'job-7', record: rec });
    expect(h.html).toContain('Veo 3.1 · 2 вар. · ≈ $3.20');
    expect(h.html).not.toContain('veo-3.1');
    expect(a.html).not.toContain('veo-3.1');
    expect(h.html).not.toMatch(BY);
    expect(a.html).toMatch(BY);
    expect(a.html.replace(BY, '')).toBe(h.html);
  });

  it('в записи нет цены — обоим берётся стоимость готовых вариантов', () => {
    const rec = { sceneId: 's1', jobId: 'job-7', provider: 'fal', model: 'veo-3.1', count: 2 };
    const h = render(shot('human'), 'human', { jobId: 'job-7', record: rec });
    const a = render(shot('agent'), 'agent', { jobId: 'job-7', record: rec });
    expect(h.html).toContain('Veo 3.1 · 2 вар. · $3.20');
    expect(a.html.replace(BY, '')).toBe(h.html);
  });
});
