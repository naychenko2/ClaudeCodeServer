import { useEffect, useState, type RefObject } from 'react';

// Оглавление markdown-документа, снятое с РЕАЛЬНОГО DOM после рендера MarkdownViewer.
//
// Источник намеренно один — DOM, а не строковый парсер: список TOC и цель скролла тогда
// физически один и тот же узел. Строковый парсер разъезжается с рендером remark (Setext,
// заголовки внутри blockquote, разметка внутри текста заголовка).
//
// Логика перенесена из components/artifacts/PlanSection и используется обеими панелями —
// «План» и «Документы», чтобы оглавление вело себя одинаково.

export interface Heading { level: number; text: string; el: HTMLElement }

// contentRef — контейнер, внутри которого отрисован markdown; deps — то, при смене чего
// оглавление пересобирается (обычно текст документа)
export function useHeadings(contentRef: RefObject<HTMLElement | null>, dep: unknown): Heading[] {
  const [headings, setHeadings] = useState<Heading[]>([]);

  useEffect(() => {
    const root = contentRef.current;
    if (!root) { setHeadings([]); return; }
    const list: Heading[] = [];
    root.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(n => {
      const el = n as HTMLElement;
      const text = (el.textContent ?? '').trim();
      if (text) list.push({ level: Number(el.tagName[1]), text, el });
    });
    setHeadings(list);
    // contentRef стабилен между рендерами, пересбор нужен только при смене содержимого
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dep]);

  return headings;
}

export function scrollToHeading(h: Heading): void {
  h.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
}
