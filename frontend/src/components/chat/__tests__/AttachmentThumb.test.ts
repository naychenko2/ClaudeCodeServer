// KM-F13a: миниатюра картинки-вложения в пузыре сообщения (ADR-041, строка №12 таблицы ручек).
// Картинка (расширение из allow-list, есть sessionId, не агентный маршрут) — <img> с URL ручки;
// всё остальное и любая ошибка загрузки — ярлык ровно как в ленте до миниатюр.
// Мутация «убрать onError» роняет тест «ошибка → ярлык»: без него битая <img> остаётся.
import { describe, it, expect } from 'vitest';
import { act, createElement } from 'react';
import { AttachmentThumb } from '../AttachmentThumb';
import { chatsApi } from '../../../features/chat/api';
import { installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './fakeDom';

type ThumbProps = { sessionId: string | null; path: string; label: string; agentRoute?: boolean };

// <img> в смонтированном дереве (тег-селектор фейк-DOM — как в других тестах ленты)
function imgsOf(container: { querySelectorAll: (s: string) => { length: number; [i: number]: { getAttribute: (a: string) => string | null } } }) {
  return container.querySelectorAll('img');
}

function renderThumb(props: ThumbProps) {
  installFakeDom();
  return mount(createElement(AttachmentThumb, props));
}

// Вызвать обработчик ошибки прямо на <img>: React держит пропсы узла под __reactProps$,
// и без onError в JSX здесь не будет что позвать — мутация «убрать onError» краснеет.
function fireImgError(container: ReturnType<typeof renderThumb>['container']) {
  const img = container.querySelectorAll('img')[0];
  const props = reactProps(img) as { onError?: () => void } | null;
  act(() => { props?.onError?.(); });
}

describe('AttachmentThumb — миниатюра вложения', () => {
  // c1: картинка с sessionId — один <img> с URL ручки и loading="lazy"; расширение без учёта регистра
  it('картинка с sessionId: <img> с src ручки №12 и lazy-загрузкой', () => {
    const { container, root } = renderThumb({ sessionId: 's1', path: '.cc-attachments/photo.PNG', label: 'photo.PNG' });
    try {
      const imgs = container.querySelectorAll('img');
      expect(imgs.length).toBe(1);
      const src = imgs[0].getAttribute('src') ?? '';
      expect(src.startsWith('/api/chats/s1/attachments/raw?path=')).toBe(true);
      expect(src).toContain('path=.cc-attachments%2Fphoto.PNG');
      expect(imgs[0].getAttribute('loading')).toBe('lazy');
      expect(imgs[0].getAttribute('alt')).toBe('photo.PNG');
    } finally {
      act(() => { root.unmount(); });
      uninstallFakeDom();
    }
  });

  // c1: не-картинка — никакого запроса и никакого <img> только ярлык с текстом path
  it('не-картинка (notes.pdf): 0 <img>, ярлык с текстом label', () => {
    const { container, root } = renderThumb({ sessionId: 's1', path: '.cc-attachments/notes.pdf', label: 'notes.pdf' });
    try {
      expect(container.querySelectorAll('img').length).toBe(0);
      expect(textNodes(container).join('')).toContain('notes.pdf');
    } finally {
      act(() => { root.unmount(); });
      uninstallFakeDom();
    }
  });

  // c1: нет sessionId и агентный маршрут — ручки №12 нет (своих файлов на маршруте агента
  // через неё нет), миниатюры не строится
  it('без sessionId или при agentRoute: 0 <img>, ярлык', () => {
    for (const props of [
      { sessionId: null, path: '.cc-attachments/photo.png', label: 'photo.png' },
      { sessionId: 's1', path: '.cc-attachments/photo.png', label: 'photo.png', agentRoute: true },
    ] satisfies ThumbProps[]) {
      const { container, root } = renderThumb(props);
      try {
        expect(container.querySelectorAll('img').length).toBe(0);
        expect(textNodes(container).join('')).toContain(props.label);
      } finally {
        act(() => { root.unmount(); });
        uninstallFakeDom();
      }
    }
  });

  // c2: ошибка загрузки асинхронна — после неё вместо миниатюры ярлык.
  // Мутация «убрать onError» в AttachmentThumb: здесь нечего позвать, <img> остаётся — красный.
  it('img error falls back to label', () => {
    const { container, root } = renderThumb({ sessionId: 's1', path: '.cc-attachments/photo.png', label: 'photo.png' });
    try {
      expect(container.querySelectorAll('img').length).toBe(1);
      fireImgError(container);
      expect(container.querySelectorAll('img').length).toBe(0);
      expect(textNodes(container).join('')).toContain('photo.png');
    } finally {
      act(() => { root.unmount(); });
      uninstallFakeDom();
    }
  });

  // Конкурентность: падение привязано к path — миниатюра другого пути после ошибки соседа показывается
  it('ошибка соседа не прячет миниатюру другого path', () => {
    const { container, root } = renderThumb({ sessionId: 's1', path: '.cc-attachments/a.png', label: 'a.png' });
    try {
      fireImgError(container);
      act(() => { root.render(createElement(AttachmentThumb, { sessionId: 's1', path: '.cc-attachments/b.png', label: 'b.png' })); });
      const imgs = container.querySelectorAll('img');
      expect(imgs.length).toBe(1);
      expect(imgs[0].getAttribute('src') ?? '').toContain('path=.cc-attachments%2Fb.png');
    } finally {
      act(() => { root.unmount(); });
      uninstallFakeDom();
    }
  });
});

describe('chatsApi.chats.attachmentRawUrl — единственная точка URL ручки №12', () => {
  // c3: id и path кодируются, токен — только при наличии
  it('id и path в URL закодированы; access_token — при наличии токена', () => {
    const g = globalThis as { sessionStorage?: { getItem: (k: string) => string | null } };
    g.sessionStorage = { getItem: (k: string) => (k === 'cc_token' ? 'tok-test' : null) };
    try {
      const url = chatsApi.chats.attachmentRawUrl('s 1', '.cc-attachments/a b/x.png');
      expect(url).toContain('chats/s%201/');
      expect(url).toContain('path=.cc-attachments%2Fa+b%2Fx.png');
      expect(url).toContain('access_token=tok-test');
    } finally {
      delete g.sessionStorage;
    }
  });

  // Без токена (незалогиненная вкладка) — параметра нет: бэк отдаёт 401, и это правильный путь
  it('без токена: access_token в URL нет', () => {
    const url = chatsApi.chats.attachmentRawUrl('s1', '.cc-attachments/x.png');
    expect(url).toBe('/api/chats/s1/attachments/raw?path=.cc-attachments%2Fx.png');
  });
});
