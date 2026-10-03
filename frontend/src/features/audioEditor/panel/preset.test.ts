import { describe, expect, it } from 'vitest';
import { parseSoundPreset } from './preset';

describe('заготовка «Звука» от «Видео»', () => {
  it('черновик вызвавшей панели едет в заготовке: на него ляжет музыка фильма', () => {
    expect(parseSoundPreset({ mode: 'music', op: 'song', duration: 32, thread: ' t-9 ', from: 'под фильм «утро»' }))
      .toEqual({ mode: 'music', op: 'song', durationSec: 32, threadId: 't-9', from: 'под фильм «утро»' });
  });
  it('без черновика — новый звук, как раньше', () => {
    expect(parseSoundPreset({ mode: 'music' })?.threadId).toBeUndefined();
  });
});
