import { beforeEach, describe, expect, it, vi } from 'vitest';

const requestMock = vi.hoisted(() => vi.fn());
vi.mock('aihome_shell/kit', async orig => ({ ...(await orig<Record<string, unknown>>()), request: requestMock }));

import { upscaleRetryError, videoApi } from './api';
import { FILM_PATH } from './mocks';

const urlOf = (i: number) => String(requestMock.mock.calls[i][0]);
const sessionOf = (i: number) => new URL(urlOf(i), 'http://x').searchParams.get('sessionId');

beforeEach(() => { requestMock.mockReset(); requestMock.mockResolvedValue({}); });

describe('ручки фильма: чат-вызыватель', () => {
  it('patchFilm, buildFilm, buildStatus и cancelBuild передают sessionId — иначе у человека нет строки ленты', async () => {
    await videoApi.patchFilm('p1', 'c1', FILM_PATH, { expectedRevision: 'r', ops: [] });
    await videoApi.buildFilm('p1', 'c1', FILM_PATH);
    await videoApi.buildStatus('p1', 'c1', FILM_PATH);
    await videoApi.cancelBuild('p1', 'c1', FILM_PATH);
    for (let i = 0; i < 4; i++) {
      expect(sessionOf(i), urlOf(i)).toBe('c1');
      expect(urlOf(i)).toContain('path=');
    }
  });
});

describe('загрузка кадра в личном чате', () => {
  it('идёт на ручку чата multipart-полем file', async () => {
    const file = new File(['x'], 'a.png', { type: 'image/png' });
    await videoApi.uploadFrame('c1', file);
    expect(urlOf(0)).toBe('/video-editor/chats/c1/frames/upload');
    const opts = requestMock.mock.calls[0][1] as { method: string; body: FormData };
    expect(opts.method).toBe('POST');
    expect(opts.body.get('file')).toBeInstanceOf(File);
  });
});

describe('«Повторить увеличение»: понятный текст отказа по коду', () => {
  const err = (status: number, body: Record<string, unknown> = {}) => ({ status, body });
  it('409 upscale_retry_unavailable, 429 и 400 с телом — свои тексты, остальное — общий', () => {
    expect(upscaleRetryError(err(409, { code: 'upscale_retry_unavailable' }))).toBe('Повторить увеличение уже нельзя — переснимите сцену');
    expect(upscaleRetryError(err(429))).toBe('Видеокарта занята');
    expect(upscaleRetryError(err(400, { error: 'Версия не 768p' }))).toBe('Версия не 768p');
    expect(upscaleRetryError(err(409, { code: 'other' }))).toBe('Не удалось повторить увеличение');
    expect(upscaleRetryError(err(500))).toBe('Не удалось повторить увеличение');
  });
});
