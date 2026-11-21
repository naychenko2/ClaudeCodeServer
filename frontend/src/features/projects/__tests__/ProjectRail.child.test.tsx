// Ф1e: детский вариант дока проектов-предметов (ADR-041 ред. 8, решение 84).
// Окружение — node + linkedom, как в ChildHome.test: проверяются состав ряда и его порядок,
// глифы на значках, текст плашки и вызовы childApi. Цвета — не здесь; вертикаль плашки —
// только по фейковой раскладке (ниже подменён getBoundingClientRect), не по реальной вёрстке.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildSubjectWithViewers } from '../../childMode/api';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Фейковая раскладка ряда: прямоугольник значка = его место в ряду (позиция × POS).
// В linkedom getBoundingClientRect по умолчанию нулевой — без подмены перестановка
// предметов не сдвинула бы плашку и тест про порядок был бы недостижим.
const POS = 100;
win.HTMLElement.prototype.getBoundingClientRect = function (this: Element) {
  if (this.getAttribute('data-child-subject') === null) return { top: 0, height: 0 };
  const idx = Array.from(doc.querySelectorAll('[data-child-subject]')).indexOf(this);
  return { top: idx * POS, height: 40 };
};

const h = {
  createLesson: vi.fn(),
  // Взрослое создание чата мокается рядом: «ни одного взрослого вызова» — проверенный
  // факт, а не предположение о том, что док до него не дошёл бы.
  chatsCreate: vi.fn(),
  toasts: [] as { title: string; body: string }[],
};

vi.mock('../../childMode/api', () => ({
  childApi: { createLesson: h.createLesson },
  ChildAttachmentRejectedError: class extends Error {},
}));
// Путь от этого файла до lib/ на уровень дальше, чем от ProjectRail.tsx: мокается тот же
// модуль, что импортит компонент, а не имя, написанное в его импорте.
vi.mock('../../../lib/api', () => ({
  api: { chats: { create: h.chatsCreate }, projects: { list: vi.fn(async () => []) } },
}));
vi.mock('../../../lib/toast', () => ({
  showToast: (title: string, body: string) => { h.toasts.push({ title, body }); },
}));

const { ProjectRail } = await import('../ProjectRail');
import type { ChildSubjectsDock } from '../ProjectRail';

const subject = (over: Partial<ChildSubjectWithViewers> = {}): ChildSubjectWithViewers => ({
  id: 'mat', label: 'Математика', glyph: '∑', color: 'info',
  tutor: { personaId: 'p1', name: 'Сова Ульяна', hasAvatar: false },
  viewers: ['Папа'],
  ...over,
});

// Три предмета профиля: два включённых и один выключенный (enabled:false — с полным
// списком, который отдаёт вызывающий; детская ручка таких не присылает вовсе).
const threeSubjects = (): ChildSubjectWithViewers[] => [
  subject(),
  subject({ id: 'rus', label: 'Русский', glyph: 'А', color: 'rose' }),
  subject({ id: 'mus', label: 'Музыка', glyph: '♪', color: 'accent', enabled: false }),
];

// Снятие — в afterEach, а не «возвратом из mount»: упавший expect раньше off() оставил бы
// смонтированный док и его портал в document.body для следующего теста.
let unmount: (() => void) | null = null;
let rerender: ((child: ChildSubjectsDock) => void) | null = null;
function mount(child: ChildSubjectsDock) {
  const root = createRoot(doc.getElementById('root')!);
  const draw = (c: ChildSubjectsDock) => act(() => { root.render(createElement(ProjectRail, { child: c, onOpenSettings: () => {} })); });
  draw(child);
  // Тот же корень, новый props: перестановка проверяется на ЖИВОМ компоненте — новый
  // mount всегда измерил бы заново и не поймал бы залипшую зависимость.
  rerender = draw;
  unmount = () => act(() => { root.unmount(); });
}

const icons = () => Array.from(doc.querySelectorAll('[data-child-subject]'));
const newLessonBtn = () => doc.querySelector('[data-testid="child-new-lesson"]') as HTMLElement | null;

// Плашка активного предмета — портал в body с position:fixed; её вертикаль — это inline
// top. Ищем её по содержимому (метка предмета · репетитор) и читаем top в px.
const plateTop = (): string | null => {
  const divs = Array.from(doc.querySelectorAll('div'));
  const plate = divs.find(d => (d.textContent ?? '').includes('· Сова Ульяна'));
  return plate ? (plate.getAttribute('style') ?? '').match(/top:\s*([-\d.]+)px/)?.[1] ?? null : null;
};

beforeEach(() => {
  h.createLesson.mockReset();
  h.chatsCreate.mockReset();
  h.toasts.length = 0;
});
afterEach(() => { unmount?.(); unmount = null; rerender = null; });

describe('ProjectRail · детский док проектов-предметов', () => {
  it('3 предмета, один выключен → в доке 2 значка с глифами предметов, порядок — порядок профиля', () => {
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn() });
    const shown = icons();
    expect(shown.length).toBe(2);
    expect(doc.querySelector('[data-child-subject="mus"]')).toBe(null);
    expect(shown.map(el => el.getAttribute('data-child-subject'))).toEqual(['mat', 'rus']);
    // Значок — глиф предмета, а не картинка проекта
    expect(shown[0].textContent).toBe('∑');
    expect(shown[1].textContent).toBe('А');
  });

  it('клик «Новый урок» → createLesson с предметом активным, один раз; взрослое создание чата — ни разу', async () => {
    h.createLesson.mockResolvedValue({ sessionId: 's-new' });
    const onLessonCreated = vi.fn();
    mount({ subjects: threeSubjects(), activeSubjectId: 'rus', onOpenSubject: vi.fn(), onLessonCreated });
    const btn = newLessonBtn();
    expect(btn).toBeTruthy();
    await act(async () => { btn!.click(); });
    expect(h.createLesson).toHaveBeenCalledTimes(1);
    expect(h.createLesson).toHaveBeenCalledWith('rus');
    expect(h.chatsCreate).toHaveBeenCalledTimes(0);
    // id созданного чата уходит вызывающему: открыть урок в его проекте — его решение
    expect(onLessonCreated).toHaveBeenCalledWith('s-new');
  });

  it('viewers=[«Папа»] → в DOM «Эту папку видят: Папа»; пустых зрителей нет вовсе', () => {
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn() });
    expect(doc.body.textContent).toContain('Эту папку видят: Папа');
    // Предмет и его репетитор — в той же плашке (макет .flyout)
    expect(doc.body.textContent).toContain('Математика · Сова Ульяна');

    unmount?.();
    unmount = null;
    mount({
      subjects: threeSubjects().map(s => ({ ...s, viewers: [] })),
      activeSubjectId: 'mat', onOpenSubject: vi.fn(),
    });
    expect(doc.body.textContent).not.toContain('Эту папку видят');
  });

  it('отказ createLesson: урока нет, кнопка жива, текст отказа — серверный как есть', async () => {
    h.createLesson.mockRejectedValue({ status: 409, body: { code: 'time_up', message: 'Время уроков на сегодня вышло.' } });
    const onLessonCreated = vi.fn();
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn(), onLessonCreated });
    const btn = newLessonBtn();
    expect(btn).toBeTruthy();
    await act(async () => { btn!.click(); });
    expect(onLessonCreated).toHaveBeenCalledTimes(0);
    expect(h.toasts).toEqual([{ title: 'Новый урок', body: 'Время уроков на сегодня вышло.' }]);
    expect(newLessonBtn()?.hasAttribute('disabled')).toBe(false);
  });

  it('нет активного предмета — плашки и кнопки нет: ряд только из значков', () => {
    mount({ subjects: threeSubjects(), activeSubjectId: null, onOpenSubject: vi.fn() });
    expect(icons().length).toBe(2);
    expect(newLessonBtn()).toBe(null);
    expect(doc.body.textContent).not.toContain('Новый урок');
  });

  it('перестановка при той же длине: плашка пересчитывает вертикаль по новому месту значка', () => {
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn() });
    // «mat» — первый в ряду: top = 0·100 + 40/2
    expect(plateTop()).toBe('20');
    // Тот же состав и та же длина — изменён только порядок (его задаёт взрослый в профиле)
    const [mat, rus, mus] = threeSubjects();
    rerender!({ subjects: [rus, mat, mus], activeSubjectId: 'mat', onOpenSubject: vi.fn() });
    expect(icons().map(el => el.getAttribute('data-child-subject'))).toEqual(['rus', 'mat']);
    // «mat» — второй: top = 1·100 + 40/2. По старым зависимостям [activeId, shown.length]
    // эффект не перёсбегал бы и плашка осталась бы на 20 — на месте прежнего предмета.
    expect(plateTop()).toBe('120');
  });

  it('«Новый урок» — тач-цель не меньше 40 px по обеим сторонам (Х-W4-25)', () => {
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn() });
    const btn = newLessonBtn();
    expect(btn).toBeTruthy();
    // Размер читается по inline-стилю кнопки (тот же приём, что с plateTop выше: в
    // linkedom именованные свойства CSSStyleDeclaration не открыты, сериализованный
    // style-атрибут — да). Высота — minHeight размера md (Button.tsx:13). Ширина
    // гарантирована явным minWidth:40 в вызывающем (ProjectRail.tsx:699), а не
    // горизонтальным паддингом: min-width есть в style-атрибуте только из-за него,
    // поэтому убрать его в вызывающем — значит краснить этот тест.
    const attr = btn!.getAttribute('style') ?? '';
    const minH = Number(attr.match(/min-height:\s*([\d.]+)px/)?.[1] ?? 0);
    expect(minH, `минимальная высота; style="${attr}"`).toBeGreaterThanOrEqual(40);
    const minW = Number(attr.match(/min-width:\s*([\d.]+)px/)?.[1] ?? 0);
    expect(minW, `минимальная ширина; style="${attr}"`).toBeGreaterThanOrEqual(40);
    // Явный height (тот же приём, что xs в Button.tsx:11) дал бы реальную мишень 24
    // при зелёном min-height-ассерте: в inline-стиле кнопки его быть не должно.
    expect(attr, `явный height в inline-стиле; style="${attr}"`).not.toMatch(/(^|[^-])height:/);
  });

  it('«Новый урок» пока запрос в полёте: второй тап не создаёт второй урок, кнопка закрыта', async () => {
    // createLesson висит до resolve: «запрос в полёте» — состояние между тапом и ответом
    let resolveFirst: (v: { sessionId: string }) => void = () => {};
    h.createLesson.mockImplementation(() => new Promise(r => { resolveFirst = r; }));
    const onLessonCreated = vi.fn();
    mount({ subjects: threeSubjects(), activeSubjectId: 'mat', onOpenSubject: vi.fn(), onLessonCreated });
    const btn = newLessonBtn();
    expect(btn).toBeTruthy();
    await act(async () => { btn.click(); });
    expect(h.createLesson).toHaveBeenCalledTimes(1);
    // Кнопка закрыта уже на следующем кадре — это то, что видит ребёнок
    expect(newLessonBtn()?.hasAttribute('disabled')).toBe(true);
    // Второй тап после коммита: до обработчика он дошёл бы только без защиты — и по
    // disabled-атрибуту (linkedom его не гасит), и по creating в newLesson()
    await act(async () => { newLessonBtn()!.click(); });
    expect(h.createLesson).toHaveBeenCalledTimes(1);
    expect(onLessonCreated).toHaveBeenCalledTimes(0);
    // Ответ пришёл: урок один, кнопка снова активна
    await act(async () => { resolveFirst({ sessionId: 's-1' }); });
    expect(onLessonCreated).toHaveBeenCalledTimes(1);
    expect(newLessonBtn()?.hasAttribute('disabled')).toBe(false);
  });
});
