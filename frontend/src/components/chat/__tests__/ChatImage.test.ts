// Регрессия: картинка проекта в markdown чата вечно висела на «Загрузка изображения…».
// ref из useInView стоял только на финальном <a>, а до загрузки в DOM лишь плейсхолдер без
// ref → IntersectionObserver ничего не наблюдал → inView=false → fetch не стартовал никогда.
// Рефы в renderToStaticMarkup не вызываются, поэтому проверяем сам элемент, который вернул
// ChatImage: в React 19 ref — обычный prop.
import { describe, it, expect, vi } from 'vitest';
import { createElement, type ReactElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

vi.mock('react-markdown', () => ({ default: () => null, defaultUrlTransform: (u: string) => u }));
vi.mock('remark-gfm', () => ({ default: () => undefined }));
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../../lib/api', () => ({ api: {} }));

const viewRef = vi.hoisted(() => () => {});
vi.mock('../../../lib/useInView', () => ({ useInView: () => [viewRef, false] }));

import { ChatImage } from '../MarkdownContent';
import { ChatProjectContext } from '../contexts';

function renderChatImage(src: string): ReactElement<{ ref?: unknown; children?: unknown }> {
  let out: ReactElement | null = null;
  const Probe = () => { out = ChatImage({ src, alt: 'кот' }) as ReactElement; return null; };
  renderToStaticMarkup(createElement(ChatProjectContext.Provider,
    { value: { id: 'p1', rootPath: '/home/u/proj' } }, createElement(Probe)));
  return out!;
}

describe('ChatImage', () => {
  it('плейсхолдер до загрузки несёт ref наблюдателя видимости', () => {
    const el = renderChatImage('.cc-attachments/cat.png');
    expect(el.type).toBe('span');
    expect(el.props.children).toBe('Загрузка изображения…');
    expect(el.props.ref).toBe(viewRef);
  });
});
