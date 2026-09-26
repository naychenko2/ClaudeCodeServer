import { describe, expect, it } from 'vitest';
import { pendingSendArgs } from './pendingSend';

describe('pendingSendArgs', () => {
  it('строка — как раньше: только текст, без вложений и без чата картинки', () => {
    expect(pendingSendArgs('привет', 'default', true)).toEqual(['привет', [], 'default', undefined]);
  });

  it('объект несёт свои вложения и пометку снимка в SendImageChatMessage', () => {
    const snap = { revision: 'r1', attached: true };
    expect(pendingSendArgs({ text: 'убери лампу', attachedPaths: ['.cc-attachments/a/hero-снимок.png'], imageSnapshot: snap }, 'plan', true))
      .toEqual(['убери лампу', ['.cc-attachments/a/hero-снимок.png'], 'plan', { imageChat: { snapshot: snap } }]);
  });

  it('объект без пометки в чате картинки — снимок null, вне чата картинки — обычная отправка', () => {
    expect(pendingSendArgs({ text: 'т', attachedPaths: [] }, 'default', true)[3]).toEqual({ imageChat: { snapshot: null } });
    expect(pendingSendArgs({ text: 'т', attachedPaths: ['x.md'] }, 'default', false)).toEqual(['т', ['x.md'], 'default', undefined]);
  });
});
