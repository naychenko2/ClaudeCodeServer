// Фото лица: проверка файлов и «согласие перед каждой загрузкой» (спека §8.1.1, решение по §12).
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { CharacterFacePhotos } from './CharacterFacePhotos';
import { FACES } from './characterTexts';
import { MAX_PHOTOS, MAX_PHOTO_MB, MIN_PHOTOS, addGate, missingText, photoSizeError, pickFaceFiles, uploadWithConsent } from './photos';

const img = (name: string) => ({ name, type: 'image/jpeg' });

describe('pickFaceFiles', () => {
  it('не-картинки отбрасываются молча', () => {
    const r = pickFaceFiles([img('a.jpg'), { name: 'b.pdf', type: 'application/pdf' }], 0);
    expect(r.picked.map(f => f.name)).toEqual(['a.jpg']);
    expect(r.error).toBeNull();
  });
  it('сверх предела — обрезка и подсказка', () => {
    const files = Array.from({ length: 4 }, (_, i) => img(`${i}.jpg`));
    const r = pickFaceFiles(files, MAX_PHOTOS - 2);
    expect(r.picked).toHaveLength(2);
    expect(r.error).toContain('Добавлено не всё');
  });
  it('десять фото уже есть — ничего не берём', () => {
    const r = pickFaceFiles([img('a.jpg')], MAX_PHOTOS);
    expect(r.picked).toHaveLength(0);
    expect(r.error).toContain('загрузите заново');
  });
});

describe('photoSizeError', () => {
  it('ровно предел — можно, байтом больше — ошибка с именем', () => {
    expect(photoSizeError('a.jpg', MAX_PHOTO_MB * 1024 * 1024)).toBeNull();
    expect(photoSizeError('a.jpg', MAX_PHOTO_MB * 1024 * 1024 + 1)).toBe(`a.jpg больше ${MAX_PHOTO_MB} МБ`);
  });
});

describe('uploadWithConsent', () => {
  it('без галочки загрузить нельзя', async () => {
    const onAdd = vi.fn(async () => {});
    const reset = vi.fn();
    expect(await uploadWithConsent(false, [1], onAdd, reset)).toBe(false);
    expect(onAdd).not.toHaveBeenCalled();
    expect(reset).not.toHaveBeenCalled();
  });
  it('после загрузки галочка сбрасывается — уже после onAdd', async () => {
    const order: string[] = [];
    await uploadWithConsent(true, [1], async () => { order.push('add'); }, () => order.push('reset'));
    expect(order).toEqual(['add', 'reset']);
  });
  it('сбой загрузки галочку не сбрасывает', async () => {
    const reset = vi.fn();
    await expect(uploadWithConsent(true, [1], async () => { throw new Error('x'); }, reset)).rejects.toThrow('x');
    expect(reset).not.toHaveBeenCalled();
  });
});

describe('CharacterFacePhotos (разметка)', () => {
  const props = { photos: [{ key: 'a', url: '/a.jpg', primary: true }], onAdd: async () => {}, onRemove: async () => {} };
  const html = renderToStaticMarkup(createElement(CharacterFacePhotos, props));
  it('без галочки кнопка «Ещё фото» заблокирована с подсказкой', () => {
    expect(html).toContain(FACES.consentFirst);
    expect(html).toMatch(/<button[^>]*disabled[^>]*>(?:(?!<\/button>).)*Ещё фото/s);
  });
  it('на минимуме фото «Убрать» заблокирована', () => {
    const three = Array.from({ length: 3 }, (_, i) => ({ key: `k${i}`, url: `/${i}.jpg`, primary: i === 0 }));
    const h = renderToStaticMarkup(createElement(CharacterFacePhotos, { ...props, photos: three }));
    expect(h).toContain(FACES.minReached);
  });
});

describe('addGate', () => {
  it('без согласия не грузит ни выбор, ни перетаскивание', () => {
    expect(addGate(false, 3)).toBe('consent');
  });
  it('с согласием и файлами — можно, без файлов — пусто', () => {
    expect(addGate(true, 2)).toBe('ok');
    expect(addGate(true, 0)).toBe('empty');
  });
});

describe('missingText', () => {
  it('перечисляет всё недостающее', () => {
    expect(missingText(' ', 1, false)).toBe('имя, ещё 2 фото, галочка согласия');
  });
  it('пусто, когда всё есть', () => {
    expect(missingText('Аня', MIN_PHOTOS, true)).toBe('');
  });
});
