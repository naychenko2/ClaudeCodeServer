import { useEffect, useState, type RefObject } from 'react';
import { slugify } from '../lib/docsLinks';

// Оглавление markdown-документа, снятое с РЕАЛЬНОГО DOM после рендера MarkdownViewer.
//
// Источник намеренно один — DOM, а не строковый парсер: список TOC и цель скролла тогда
// физически один и тот же узел. Строковый парсер разъезжается с рендером remark (Setext,
// заголовки внутри blockquote, разметка внутри текста заголовка).
//
// Логика перенесена из components/artifacts/PlanSection и используется обеими панелями —
// «План» и «Документы», чтобы оглавление вело себя одинаково.

export interface Heading { level: number; text: string; el: HTMLElement }

// Оглавление документа, отданное НАРУЖУ тем, кто этот документ показывает (сейчас —
// FileViewer центральной области, читает панель «Оглавление»).
//
// Вместе с заголовками отдаются и действия над ними: прокрутка — забота самого
// просмотрщика (у него свой скроллер и свои поправки на шапку), а нарезка раздела —
// его же, потому что резать надо ИСХОДНЫЙ markdown, а не текст из DOM. Панель знает
// только про список и про то, что по строке можно кликнуть.
export interface DocToc {
  // Путь документа: панель показывает его в подсказке и различает смену файла
  path: string;
  headings: Heading[];
  // Прокрутить документ к заголовку
  jump: (h: Heading) => void;
  // Исходный markdown раздела для цитаты в чат; null — раздел не нашёлся
  sectionOf: (h: Heading) => string | null;
}

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

// Живой узел заголовка в ТЕКУЩЕМ документе.
//
// Снятый с DOM узел долго не живёт: markdown перерисовывается и без смены текста
// (подгрузились комментарии к документу, доехала подсветка кода), и все собранные
// ранее элементы разом уходят из документа — `getBoundingClientRect` у такого даёт
// нули, а прокрутка «к заголовку» молча уезжает в начало. Пока оглавление
// использовали сразу после рендера (якорь ссылки), это не замечалось; панель
// «Оглавление» живёт рядом с документом часами, и там протухает всё.
//
// Поэтому храним заголовок как ТЕКСТ, а узел ищем в момент перехода: тексты при
// перерисовке те же, слаг совпадает. Прежний узел берём, только пока он в документе.
export function resolveHeadingEl(root: HTMLElement | null, h: Heading): HTMLElement | null {
  if (h.el.isConnected) return h.el;
  if (!root) return null;
  const slug = slugify(h.text);
  const live = [...root.querySelectorAll<HTMLElement>('h1,h2,h3,h4,h5,h6')]
    .find(n => slugify((n.textContent ?? '').trim()) === slug);
  return live ?? null;
}
