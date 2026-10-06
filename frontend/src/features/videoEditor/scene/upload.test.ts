import { beforeEach, describe, expect, it, vi } from 'vitest';

const filesUpload = vi.hoisted(() => vi.fn());
vi.mock('aihome_shell/kit', async orig => ({
  ...(await orig<Record<string, unknown>>()),
  api: { files: { upload: filesUpload } },
  showToast: vi.fn(),
}));

import { videoApi } from '../api';
import { uploadErrorText, uploadFrame } from './actions';

const file = new File(['x'], 'a.png', { type: 'image/png' });

beforeEach(() => { vi.restoreAllMocks(); filesUpload.mockReset(); });

describe('uploadFrame: две ветки', () => {
  it('личный чат — новая ручка, кадр берётся из ответа как есть', async () => {
    const spy = vi.spyOn(videoApi, 'uploadFrame').mockResolvedValue({ kind: 'file', path: 'frames/ab.png' });
    expect(await uploadFrame('personal', 'c1', null, file)).toEqual({ kind: 'file', path: 'frames/ab.png' });
    expect(spy).toHaveBeenCalledWith('c1', file);
    expect(filesUpload).not.toHaveBeenCalled();
  });

  it('проект — files/upload в video/…/кадры/', async () => {
    const spy = vi.spyOn(videoApi, 'uploadFrame');
    filesUpload.mockResolvedValue({});
    expect(await uploadFrame('p1', 'c1', null, file)).toEqual({ kind: 'file', path: 'video/кадры/a.png' });
    expect(filesUpload).toHaveBeenCalledWith('p1', file, 'video/кадры');
    expect(spy).not.toHaveBeenCalled();
  });
});

describe('uploadErrorText', () => {
  it('понятные причины отказа', () => {
    expect(uploadErrorText({ status: 413 })).toBe('Файл больше 20 МБ');
    expect(uploadErrorText({ status: 400, body: { code: 'invalid_request' } })).toBe('Это не картинка');
    expect(uploadErrorText({ status: 404, body: { code: 'chat_not_found', error: 'Чат не найден' } })).toBe('Чат не найден');
  });
});
