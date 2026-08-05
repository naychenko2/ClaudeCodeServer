import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
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
  // Раздел, который сейчас перед глазами, — ПОДПИСКОЙ, а не полем оглавления.
  // Активный раздел меняется на каждом кадре прокрутки, и будь он полем, объект
  // оглавления пересобирался бы так же часто: каждое движение колеса перерисовывало
  // бы весь экран (чат, файлы, соседние панели) ради подсветки одной строки.
  // Подписка держит эту рябь внутри самой панели. cb зовётся сразу с текущим.
  subscribeActive: (cb: (index: number) => void) => () => void;
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

// Линия отсчёта: раздел считается текущим, когда его заголовок поднялся выше этой
// отметки от верха зоны просмотра. Не сам верх — иначе заголовок, стоящий вплотную
// под кромкой, считался бы «ещё не наступившим», хотя читают уже его.
const ACTIVE_LINE = 72;

// Сколько ждать окончания прокрутки после прыжка, если браузер не шлёт scrollend
// (он есть не везде). Дольше плавная прокрутка всё равно не длится.
const SETTLE_MS = 700;

// Слежение за тем, какой раздел читают.
//
// Живёт рядом с оглавлением, а не в панели: панель не знает ни скроллера документа,
// ни его геометрии, а просмотрщик знает. Наружу отдаётся подписка (см. DocToc).
//
// Узлы заголовков берутся ЖИВЫЕ на каждом пересчёте, а не из собранного списка: те
// протухают при перерисовке markdown (см. resolveHeadingEl), и слежение молча
// залипало бы на первом разделе.
export function useHeadingSpy(
  contentRef: RefObject<HTMLElement | null>,
  headings: Heading[],
): { subscribe: DocToc['subscribeActive']; pin: (h: Heading) => void } {
  const activeRef = useRef(0);
  const subsRef = useRef(new Set<(index: number) => void>());
  // Прыжок по оглавлению: пока документ едет к разделу, слежение молчит — иначе
  // подсветка пробегала бы по всем разделам, мимо которых летит прокрутка, и
  // приезжала в цель уже после того, как глаз её потерял
  const pinnedRef = useRef(false);
  const settleRef = useRef<number | null>(null);
  const headingsRef = useRef(headings);
  headingsRef.current = headings;

  const emit = useCallback((i: number) => {
    if (i === activeRef.current) return;
    activeRef.current = i;
    subsRef.current.forEach(cb => cb(i));
  }, []);

  // Индекс живого узла → индекс в оглавлении. Обычно они совпадают (один селектор,
  // один порядок), но между перерисовкой документа и пересбором списка есть кадры,
  // где составы разъехались, — там сверяемся по слагу.
  const toHeadingIndex = useCallback((live: HTMLElement[], i: number): number => {
    const list = headingsRef.current;
    if (live.length === list.length) return i;
    const slug = slugify((live[i].textContent ?? '').trim());
    const found = list.findIndex(h => slugify(h.text) === slug);
    return found >= 0 ? found : activeRef.current;
  }, []);

  const compute = useCallback(() => {
    const root = contentRef.current;
    if (!root) return;
    const live = [...root.querySelectorAll<HTMLElement>('h1,h2,h3,h4,h5,h6')];
    if (live.length === 0) return;
    const line = root.getBoundingClientRect().top + ACTIVE_LINE;
    // Текущий — ПОСЛЕДНИЙ заголовок, поднявшийся выше линии. Ни один не поднялся —
    // читают начало документа, до первого заголовка: тогда активен он же.
    let idx = 0;
    for (let i = 0; i < live.length; i++) {
      if (live[i].getBoundingClientRect().top <= line) idx = i;
      else break;
    }
    // Документ домотан до конца: последний раздел может быть короче экрана и до линии
    // не дотянуться никогда — без этого его невозможно подсветить в принципе
    if (root.scrollTop + root.clientHeight >= root.scrollHeight - 4) idx = live.length - 1;
    emit(toHeadingIndex(live, idx));
  }, [contentRef, emit, toHeadingIndex]);

  useEffect(() => {
    const root = contentRef.current;
    if (!root) return;
    let raf = 0;
    const onScroll = () => {
      // Никто не смотрит (панель закрыта) или документ едет к разделу после клика
      if (subsRef.current.size === 0 || pinnedRef.current || raf) return;
      raf = requestAnimationFrame(() => { raf = 0; compute(); });
    };
    root.addEventListener('scroll', onScroll, { passive: true });
    // Смена документа обнуляет позицию — считаем сразу, не дожидаясь прокрутки
    compute();
    return () => {
      root.removeEventListener('scroll', onScroll);
      if (raf) cancelAnimationFrame(raf);
    };
  }, [contentRef, compute, headings]);

  const subscribe = useCallback<DocToc['subscribeActive']>(cb => {
    subsRef.current.add(cb);
    cb(activeRef.current);
    return () => { subsRef.current.delete(cb); };
  }, []);

  // Прыжок к разделу: подсвечиваем цель СРАЗУ (клик обязан отзываться мгновенно) и
  // держим её, пока документ не доедет
  const pin = useCallback((h: Heading) => {
    const i = headingsRef.current.indexOf(h);
    if (i >= 0) emit(i);
    pinnedRef.current = true;
    const root = contentRef.current;
    if (settleRef.current) window.clearTimeout(settleRef.current);
    const release = () => {
      pinnedRef.current = false;
      root?.removeEventListener('scrollend', release);
      if (settleRef.current) { window.clearTimeout(settleRef.current); settleRef.current = null; }
    };
    root?.addEventListener('scrollend', release);
    settleRef.current = window.setTimeout(release, SETTLE_MS);
  }, [contentRef, emit]);

  useEffect(() => () => { if (settleRef.current) window.clearTimeout(settleRef.current); }, []);

  // Объект СТАБИЛЬНЫЙ, а не свежий на каждый рендер: он уходит в оглавление, которое
  // просмотрщик отдаёт наружу эффектом. Новый объект на каждом рендере пересобирал бы
  // оглавление, оглавление — состояние экрана, состояние — рендер: «Maximum update
  // depth exceeded» и упавший просмотрщик. Обе функции внутри уже стабильны.
  return useMemo(() => ({ subscribe, pin }), [subscribe, pin]);
}
