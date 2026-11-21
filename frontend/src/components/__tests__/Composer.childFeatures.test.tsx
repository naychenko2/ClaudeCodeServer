// Ф1c (ADR-041 ред. 8, разрез §14.5): штатный Composer по детскому набору childFeatures.
// Ключи — ChildModeAllowList.ComposerFeatures: "text", "attachFromSubject", "voiceInput",
// "askUserAnswer". Default-deny: при наличии набора показаны только перечисленные фичи,
// неизвестный ключ — тоже скрыто; без набора — взрослый композер без изменений.
// askUserAnswer проверяется здесь только как «не ломает композер»: свои кнопки он несёт
// в ленте (AskQuestionView), не в композере.
// Окружение — node + linkedom, как в ChildLessonChat.test. useVoiceInput мокирован с
// hasSpeech=true: кнопка голосового ввода рендерится только при доступном движке, в node
// движка нет. useHandsFree/useMicLevel — заглушки: петля разговора этим тестам не нужна.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
// React решает, навешивать ли слушателя «input», по isEventSupported: 'oninput' in document.
// В linkedom его нет — без этого шима onChange не срабатывает ни на одном поле (тот же шим,
// что в ChildTutorCard.test.tsx:43)
(doc as unknown as { oninput?: unknown }).oninput = null;
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
// localStorage пуст → useActionVisibility берёт дефолт: цикл/дерево/скилл свёрнуты
// в меню «⋯» — взрослый кейс проверяет их по содержимому открытого меню, как они
// стоят у человека по умолчанию. Для ребёнка это не важно: его кнопки null по childSet
// — ни в ряду, ни в меню
// Открытое меню «⋯» ищет прокручиваемых предков через getComputedStyle (ToolbarOverflowMenu):
// в linkedom его нет — заглушка говорит «не прокручивается», что и есть правда для node
vi.stubGlobal('getComputedStyle', () => ({ overflowY: 'visible', getPropertyValue: () => '' }));
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
// Замер полосы для кейса «узкой ширины»: ResizeObserver в linkedom нет, и без него
// stripWidth всегда 0 — pickLayout отдаёт «середину», арифметика сворачивания не
// считается никогда. Shim сообщает полосе фиксированную ширину: 0 — «ещё не померили»
// (остальные кейсы ведут себя как раньше), число — реальная узкая полоса
let stubbedStripWidth = 0;
// Колбэк получает записи как настоящий: наблюдатель высоты поля (Composer:1569) читает
// entries[0].target — без записи он падает. offsetHeight в linkedom нет → undefined,
// сравнение с нужной высотой ложно — «столбца» не появляется, как и было
vi.stubGlobal('ResizeObserver', class {
  cb: (entries: unknown[]) => void;
  constructor(cb: (entries: unknown[]) => void) { this.cb = cb; }
  observe(el: unknown) { this.cb([{ target: el }]); }
  unobserve() {}
  disconnect() {}
});
Object.defineProperty((win as unknown as { HTMLElement: { prototype: object } }).HTMLElement.prototype, 'clientWidth', { get: () => stubbedStripWidth });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// react/react-dom импортируются ПОСЛЕ стабов: canUseDOM и isInputEventSupported вычисляются
// на загрузке react-dom — при статическом импорте они были бы false (window ещё не.stubbed),
// и React не диспатчил бы onChange на «input» (то же предупреждение, что в ChildTutorCard.test.tsx:52)
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');

let attachCalls = 0;

// Стор контекста чата подписывается на SignalR на уровне модуля: в node относительный
// '/hubs/session' не резолвится (HttpConnection бросает при импорте) — транспорт заглушён,
// эти тесты его не касаются
vi.mock('../../lib/signalr', () => ({
  onMessage: () => () => {},
  onReconnected: () => () => {},
}));

vi.mock('../../hooks/useVoiceInput', () => ({
  // hasSpeech=true — «движок есть»: кнопка голосового ввода видна там, где её дал набор
  useVoiceInput: () => ({ hasSpeech: true, isListening: false, recSeconds: 0, startMic: () => {}, stopMic: () => {} }),
}));
vi.mock('../../hooks/useHandsFree', () => ({
  useHandsFree: () => ({
    phase: 'off', active: false, buffer: '', barren: 0, warned: false, seq: 0,
    start: () => {}, stop: () => {}, abort: () => {},
  }),
}));
vi.mock('../../hooks/useMicLevel', () => ({
  useMicLevel: () => ({ reportMicDead: () => {}, reportCycleEnd: () => {}, reportEngineHeard: () => {}, reportCycleStart: () => {} }),
}));

const { Composer } = await import('../Composer');

// Данные такие, чтобы взрослая проверка была невакуумной: со скиллом и персоной взрослый
// композер реально открывает «/»-меню и «@»-список — ребёнок нет.
const SKILLS = [{ name: 'my-skill', description: 'тестовый скилл', argumentHint: '', filePath: 'x.md' }];
const PERSONAS = [{ id: 'p1', handle: 'marina', name: 'Марина' }] as unknown as Parameters<typeof Composer>[0]['personas'];

type Props = Parameters<typeof Composer>[0];
function render(props: Partial<Props> & { sessionId: string }) {
  const root = createRoot(doc.getElementById('root')!);
  act(() => {
    root.render(createElement(Composer, {
      onSend: () => {}, onStop: () => {},
      onAttach: () => { attachCalls += 1; },
      isGenerating: false,
      mode: 'default',
      onModeChange: () => {},
      attachments: [],
      skills: SKILLS,
      personas: PERSONAS,
      model: 'sonnet', onModelChange: () => {},
      onToggleWorkLoop: () => {},
      onToggleVoiceMode: () => {},
      onToggleWorktree: () => {},
      ...props,
    }));
  });
  return root;
}

// Доступные имена контролов: title/aria-label — то же, что читает человек и диктор
const byTitle = (prefix: string) =>
  Array.from(doc.querySelectorAll('button[title]')).filter(b => (b.getAttribute('title') ?? '').startsWith(prefix));
const byAria = (label: string) => Array.from(doc.querySelectorAll(`button[aria-label="${label}"]`));

// Ввод и клик — через await act(async …), как в эталонном ChildTutorCard.test.tsx:150:
// синхронный act не дожидает очередь React, и состояние после события не видно
async function typeInto(value: string) {
  const ta = doc.querySelector('textarea') as HTMLTextAreaElement;
  // Сеттер прототипа, а не ta.value: React оборачивает аксессор экземпляра своим
  // value-tracker (trackValueOnNode) и прямую запись считает «значением с тем же
  // значением» — onChange не срабатывает. Тот же приём, что в ChildTutorCard.test.tsx:151
  const desc = Object.getOwnPropertyDescriptor((win as unknown as { HTMLTextAreaElement: { prototype: object } }).HTMLTextAreaElement.prototype, 'value');
  if (desc?.set) desc.set.call(ta, value); else ta.value = value;
  await act(async () => { ta.dispatchEvent(new win.Event('input', { bubbles: true })); });
}
async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
  await act(async () => {});
}

beforeEach(() => { attachCalls = 0; });

describe('Composer по детскому набору (Ф1c)', () => {
  it('childFeatures=[text, voiceInput]: поле и голос есть; модель, режимы, цикл, разговор, «＋» и «⋯» — нет', () => {
    const root = render({ sessionId: 'kid-1', childFeatures: ['text', 'voiceInput'] });
    expect(doc.querySelector('textarea')).not.toBeNull();
    expect(byTitle('Голосовой ввод').length).toBe(1);
    // Всё вне списка — default-deny, даже когда родитель прислал обработчики
    expect(byTitle('Режим работы').length).toBe(0);
    expect(byTitle('Цикл «до готово»').length).toBe(0);
    expect(byTitle('Модель').length).toBe(0);
    expect(byAria('Режим разговора').length).toBe(0);
    expect(byAria('Быстрые фразы').length).toBe(0);
    expect(byTitle('Прикрепить').length).toBe(0);
    expect(byTitle('Отдельное дерево').length).toBe(0);
    expect(byTitle('Ещё').length).toBe(0);
    root.unmount();
  });

  it('ребёнку: «/» не открывает меню команд, «@» — список персон', async () => {
    const root = render({ sessionId: 'kid-2', childFeatures: ['text', 'voiceInput'] });
    await typeInto('/my-s');
    expect(doc.querySelectorAll('[data-skill-item]').length).toBe(0);
    await typeInto('@mar');
    expect(doc.querySelectorAll('[data-mention-item]').length).toBe(0);
    root.unmount();
  });

  it('полный набор: «＋» есть, прикреплляет сразу (без меню ярлыков), кликом зовёт onAttach', async () => {
    const root = render({ sessionId: 'kid-3', childFeatures: ['text', 'attachFromSubject', 'voiceInput', 'askUserAnswer'] });
    const plus = byTitle('Прикрепить');
    expect(plus.length).toBe(1);
    expect(plus[0].getAttribute('title')).toBe('Прикрепить файл'); // без «Картинка/Звук/Видео» — вложения только из папки предмета
    expect(plus[0].getAttribute('aria-haspopup')).toBeNull();
    await click(plus[0] as HTMLElement);
    expect(attachCalls).toBe(1);
    // Возврат ревью 9dc5dfe8: ребёнку не строится ни «⋯», ни ctx-меню «Все действия» —
    // в них был бы дубль «Прикрепить файл» и глазик видимости, ключей этих контролов
    // в детском allow-list нет
    expect(byTitle('Ещё').length).toBe(0);
    // и при полном наборе взрослое остаётся скрытым
    expect(byTitle('Режим работы').length).toBe(0);
    expect(byTitle('Модель').length).toBe(0);
    expect(byAria('Режим разговора').length).toBe(0);
    root.unmount();
  });

  // Пункт 2 возврата ревью (9dc5dfe8): в бюджет полосы ребёнку закладывалась ширина
  // правой группы «модель · собеседник», которой у него нет вовсе — на узкой полосе это
  // выталкивало «＋» из ряда. Замер — shim начала файла: 220 px — ширина, при которой
  // фантомный бюджет (левый блок 112 + паддинги 16 + зазоры 8 + правая группа формы C
  // 100 = 268) не влезает вовсе, и без правки «＋» уезжает в «⋯»
  it('узкая полоса ребёнка: «＋» остаётся в ряду, «⋯» нет', async () => {
    stubbedStripWidth = 220;
    try {
      const root = render({ sessionId: 'kid-5', childFeatures: ['text', 'attachFromSubject'] });
      await act(async () => {});
      expect(byTitle('Прикрепить').length).toBe(1);
      expect(byTitle('Ещё').length).toBe(0);
      root.unmount();
    } finally {
      stubbedStripWidth = 0;
    }
  });

  it('unknown key default-deny: [text, teleport] — ни голоса, ни вложения', () => {
    const root = render({ sessionId: 'kid-4', childFeatures: ['text', 'teleport'] });
    expect(doc.querySelector('textarea')).not.toBeNull();
    expect(byTitle('Голосовой ввод').length).toBe(0);
    expect(byTitle('Прикрепить').length).toBe(0);
    root.unmount();
  });

  it('без childFeatures взрослый композер не сломан: модель, режимы, фразы, «＋», голос на месте; цикл и дерево — в меню «⋯», как были', async () => {
    const root = render({ sessionId: 'adult-1' });
    expect(doc.querySelector('textarea')).not.toBeNull();
    expect(byTitle('Режим работы').length).toBe(1);
    expect(byTitle('Модель').length).toBe(1);
    expect(byAria('Режим разговора').length).toBe(1);
    expect(byAria('Быстрые фразы').length).toBe(1);
    expect(byTitle('Прикрепить').length).toBe(1);
    // adult regression: «＋» и микрофон живут и без детского набора (childHas без набора — false)
    expect(byTitle('Голосовой ввод').length).toBe(1);
    // цикл и дерево у взрослого по умолчанию свёрнуты в «⋯» (defaultHidden useActionVisibility) —
    // проверяем их наличие по содержимому открытого меню; у ребёнка нет ни кнопок, ни самого меню (тест 1)
    const more = byTitle('Ещё');
    expect(more.length).toBe(1);
    await click(more[0] as HTMLElement);
    const menuLabels = Array.from(doc.querySelectorAll('[role="menuitem"], [role="menuitemcheckbox"]'))
      .map(el => (el.textContent ?? '').trim());
    expect(menuLabels.some(t => t.startsWith('Цикл «до готово»'))).toBe(true);
    expect(menuLabels.some(t => t.startsWith('Отдельное дерево'))).toBe(true);
    root.unmount();
  });

  // Автокомплит взрослого — в свежем рендере, без открытого меню: «/»-меню и «@»-список
  // работают как раньше; ребёнку они закрыты (тест 2)
  it('без childFeatures «/» открывает меню команд, «@» — список персон', async () => {
    const root = render({ sessionId: 'adult-2' });
    await typeInto('/my-s');
    expect(doc.querySelectorAll('[data-skill-item]').length).toBeGreaterThan(0);
    await typeInto('@mar');
    expect(doc.querySelectorAll('[data-mention-item]').length).toBeGreaterThan(0);
    root.unmount();
  });
});
