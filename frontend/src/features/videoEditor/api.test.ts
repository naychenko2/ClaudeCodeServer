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

describe('личный чат: «Сохранить сцену» и фильмы «Личного» (ADR-040 Ф2-3/Ф2-6)', () => {
  it('сохранение сцены — ручка сцены на чате, filmPath/folder уходят как есть', async () => {
    await videoApi.save('personal', 'c 1', 's1', { versionId: 'v1' });
    await videoApi.save('personal', 'c 1', 's1', { filmPath: FILM_PATH });
    await videoApi.save('personal', 'c 1', 's1', { folder: 'video/утро' });
    expect(urlOf(0)).toBe('/video-editor/chats/c%201/scenes/s1/save');
    expect(JSON.parse(String(requestMock.mock.calls[1][1].body))).toMatchObject({ filmPath: FILM_PATH, sessionId: 'c 1', sceneId: 's1' });
    expect(JSON.parse(String(requestMock.mock.calls[2][1].body))).toMatchObject({ folder: 'video/утро' });
  });

  it('фильмы: список, состояние, создание, правка, сборка и статус — те же ручки на чате', async () => {
    await videoApi.films('personal', 'c1');
    await videoApi.filmState('personal', 'c1', FILM_PATH);
    await videoApi.createFilm('personal', 'c1', FILM_PATH);
    await videoApi.patchFilm('personal', 'c1', FILM_PATH, { expectedRevision: 'r', ops: [] });
    await videoApi.buildFilm('personal', 'c1', FILM_PATH);
    await videoApi.buildStatus('personal', 'c1', FILM_PATH);
    for (let i = 0; i < 6; i++) expect(urlOf(i), urlOf(i)).toMatch(/^\/video-editor\/chats\/c1\/films/);
    expect(urlOf(0)).toBe('/video-editor/chats/c1/films');
    expect(urlOf(1)).toContain('/films/state?path=');
    expect(requestMock.mock.calls[2][1].method).toBe('POST');
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

describe('«Увеличить ×2/×4»: ручки версии', () => {
  it('котировка — GET, запуск — POST с масштабом в теле, версия кодируется в пути', async () => {
    await videoApi.enhanceQuote('p1', 'c1', 's1', 'v/1');
    await videoApi.enhance('p1', 'c1', 's1', 'v/1', 4);
    expect(urlOf(0)).toBe('/projects/p1/video-editor/sessions/c1/scenes/s1/versions/v%2F1/enhance');
    expect(urlOf(1)).toBe(urlOf(0));
    const opts = requestMock.mock.calls[1][1] as { method: string; body: string };
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body)).toEqual({ scale: 4 });
  });
});
