import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { VideoScene } from '../api';
import { scene, version } from '../mocks';
import { quietView, QuietLineView, sceneCardView, SceneCardView, type SceneCardActions } from './SceneCard';

// Дополнение плана 2026-10-02: действие агента рисуется ТОЙ ЖЕ разметкой, что действие человека, —
// карточку строят запись ленты и стор, отличие только в «✦ Claude»

const noop = () => {};
const A: SceneCardActions = { onPick: noop, onPrev: noop, onNext: noop, onTake: noop, onSave: noop, onDownload: noop, onAddToFilm: noop, onReshoot: noop, onOpenFilm: noop };

const BY = /<span data-by-claude=""[^>]*>.*?<\/span>/;
const render = (s: VideoScene, who: 'human' | 'agent', o: { jobId?: string; record?: Record<string, unknown>; running?: boolean } = {}) => {
  const v = sceneCardView({
    scene: s, jobId: o.jobId, record: o.record ? { ...o.record, initiator: who } : undefined, personal: false, focused: false,
    jobs: o.running ? [{ jobId: 'job-7', sessionId: 'c1', sceneId: s.sceneId, stage: 'running', queuePosition: null, etaSeconds: null, variant: 1, count: 2 }] : [],
    pos: null,
  });
  return { v, html: renderToStaticMarkup(createElement(SceneCardView, { v, src: '/clip.mp4', busy: false, a: A })) };
};

const shot = (who: 'human' | 'agent', running = false) => scene('s1', {
  versions: running ? [] : [version(1, { initiator: who }), version(2, { initiator: who })],
  currentVersionId: running ? undefined : 'ver-2',
  launches: [{ jobId: 'job-7', at: '2026-10-02T15:00:00Z', status: running ? 'running' : 'done', interrupted: false, initiator: who, provider: 'fal', model: 'veo-3.1', count: 2 }],
});
const launchRecord = { sceneId: 's1', jobId: 'job-7', provider: 'fal', model: 'Veo 3.1', count: 2, durationSec: 8, price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing' } };

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
    expect(h.html).toContain('data-video-card-progress');
    expect(h.html).toContain('снимаем 2 варианта');
    expect(a.html).toMatch(BY);
    expect(a.html.replace(BY, '')).toBe(h.html);
  });

  it('готовый запуск агента: варианты в той же карточке', () => {
    const h = render(shot('human'), 'human', { jobId: 'job-7', record: launchRecord });
    const a = render(shot('agent'), 'agent', { jobId: 'job-7', record: launchRecord });
    expect(h.html).toContain('data-video-player');
    expect(a.html.replace(BY, '')).toBe(h.html);
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
});
