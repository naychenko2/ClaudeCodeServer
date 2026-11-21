// Ф1h: шапка урока ребёнка (решение Андрея 19.11, разрез §16).
// 1) Строка кураторов — точные тексты: «Уроки видит Папа» / «Уроки видят Папа и Мама» /
//    «Уроки видят Папа, Мама и Бабушка»; пустой список — строки нет вовсе.
// 2) Мобильная ширина: строка остаётся в DOM и видима, даже когда остальная шапка
//    свёрнута (для неё исключение: раньше на мобиле шапка урока скрывалась целиком).
// 3) ChatPanel при childFeedKinds: рисуется детская шапка, взрослая ChatHeaderBar
//    не монтируется вовсе; без childFeedKinds — взрослая шапка как раньше.
// 4) Десктопная геометрия (ревью d52edd91): коробка шапки = колонка ленты — CHAT_MAX_W
//    по центру без бокового отступа, содержимое стоит у её краёв, ровно над сообщениями.
// 5) Обрезка длинного текста (хвост того же ревью): метка предмета, строка кураторов и
//    заголовок урока лежат в элементах с ellipsis-стилями (без них текст резался на
//    полбукве без «…» или вылезал за край), полный текст — в title.
// 6) isMobile — один источник: проп вызывающего (Х-W4-4), внутреннего useIsMobile нет.
// Окружение — node + linkedom, как в ChildWorkspace.test: тяжёлые дети (TutorMark —
// ChildHome-граф; ChatHeaderBar — маркер-заглушка для отрицательной проверки) подменены
// стендами; signalr/api/personas/git — no-op-моки, чтобы не тянуть сеть и сторы.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import { CHAT_MAX_W } from '../../lib/design';
import type { ChildSubjectView } from './types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// useIsMobile/useIsTouch читают matchMedia при mount; useWindowWidth — innerWidth.
// Переключатель mobile ниже; по умолчанию — широкий экран.
let mobile = false;
const nodeSetTimeout = globalThis.setTimeout.bind(globalThis);
const nodeClearTimeout = globalThis.clearTimeout.bind(globalThis);
Object.assign(win, {
  innerWidth: 1280,
  matchMedia: () => ({ matches: mobile, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {} }),
  // Таймеры и кадры: эффект спиннера истории (window.setTimeout) и переход ленты
  // (requestAnimationFrame) в ChatPanel зовут их при монтировании.
  // Оригинальные функции берём до подмены (const вне win — their own names):
  // через globalThis после подстановки до них не дойти
  setTimeout: (fn: () => void, ms?: number) => nodeSetTimeout(fn, ms),
  clearTimeout: (id: number) => { nodeClearTimeout(id); },
  requestAnimationFrame: (cb: (t: number) => void) => { cb(0); return 1; },
  cancelAnimationFrame: () => {},
  // useChatScroll.hasSelectionIn читает выделение в ленте: выделения нет — прилипание
  // к низу работает как обычно (null — тот же путь, что «никто ничего не держит»)
  getSelection: () => null,
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('IntersectionObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', (cb: (t: number) => void) => { cb(0); return 1; });
vi.stubGlobal('cancelAnimationFrame', () => {});
const store: Record<string, string> = {};
vi.stubGlobal('localStorage', { getItem: (k: string) => store[k] ?? null, setItem: (k: string, v: string) => { store[k] = v; }, removeItem: (k: string) => { delete store[k]; } });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// ── моки для случая ChatPanel (тот же набор, что в workspaceFeatureCenter, сжатый до
//     того, что зовёт сам ChatPanel): signalr — no-op подписки/действия, api — Proxy
//     с no-op-ответами, personas/git/subsystems/projectCapabilities — сохранение
//     исходника с no-op хуков ──
vi.mock('../../lib/signalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve();
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    joinProject: act, leaveProject: act, joinSession: act, leaveSession: act,
    joinUser: act, leaveUser: act, sendMessage: act, respondPermission: act,
    interruptSession: act, compactSession: act, answerQuestion: act, respondPlan: act,
    respondTeamPlan: act, respondTeamEscalation: act, setMode: act, resetConnection: act,
    joinPreviewLog: act, leavePreviewLog: act,
  };
});
vi.mock('../../lib/api', () => {
  const deep = (val: any): any => {
    const fn: any = () => Promise.resolve(val ?? null);
    return new Proxy(fn, {
      get: (_t, prop) => {
        if (prop === 'then' || prop === Symbol.toPrimitive || prop === Symbol.iterator) return undefined;
        if (prop === 'catch' || prop === 'finally') return () => Promise.resolve(val ?? null);
        return deep(val);
      },
      apply: () => Promise.resolve(val ?? null),
    });
  };
  // tasks.featuresIndex — пустой срез формы FeaturesIndex ({features:[], chats:{}}):
  // стор применяет ответ как есть, без null-guard (featuresIndex.ts:137) — null или []
  // в ответе дали бы срез без поля chats, и featureOf упал бы на нём
  const ns = (overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'tasks') return ns({ featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: () => Promise.resolve([]) });
      if (prop === 'chats') return ns({ get: () => Promise.resolve(null) });
      if (prop === 'sessions') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      return deep(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonasVersion: () => 0, getPersonaById: () => null,
    getPersonasSnapshot: () => [], ensurePersonasLoaded: () => Promise.resolve(), personaLabel: () => '',
  };
});
vi.mock('../../lib/git', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/git')>();
  return {
    ...orig,
    useGitState: () => ({ status: null, unpushed: [], changedBy: new Map() }),
    ensureGit: () => Promise.resolve(),
    loadUnpushedLog: () => Promise.resolve([]), setActiveSessionForChangedBy: () => {},
  };
});
vi.mock('../../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems')>();
  return { ...orig, SUBSYSTEMS: {}, useSubsystem: () => ({ id: 'x', enabled: false }) };
});
vi.mock('../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems/registry')>();
  return { ...orig, useSlot: () => [], useSlotItem: () => null };
});
vi.mock('../../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => false, isLocalProject: () => false, featureReason: () => null };
});
// Взрослая шапка — маркер-заглушка: её присутствие/отсутствие в DOM и есть проверка
// «ребёнку ChatHeaderBar не рисуется» (отрицательное утверждение без маркера — вакуум)
vi.mock('../../components/chat/ChatHeaderBar', async () => {
  const React = await import('react');
  return { ChatHeaderBar: () => React.createElement('div', { 'data-testid': 'chat-header-bar-stub' }) };
});

// Импорт после глобалов: модульный код графа (в т.ч. ChatPanel) не должен читать
// DOM/сторы до их подстановки (тот же приём, что в ChildWorkspace.test)
const { ChildLessonHeader, curatorsLine } = await import('./ChildLessonHeader');
const { ChatPanel } = await import('../../components/ChatPanel');

const subject = (over: Partial<ChildSubjectView> = {}): ChildSubjectView => ({
  id: 'math', label: 'Математика', glyph: '×÷', color: 'info',
  tutor: { personaId: 'p1', name: 'Сова Ульяна', hasAvatar: false }, ...over,
});

async function renderHeader(props: { subject: ChildSubjectView | null; viewers?: string[]; title: string; isMobile: boolean }) {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => { root.render(createElement(ChildLessonHeader, props)); });
  await act(async () => {});
  return root;
}
const lineNode = () => doc.querySelector('[data-testid="child-curators-line"]');
const lineText = () => (lineNode()?.textContent ?? '').trim();

// Глубочайший элемент, в тексте которого есть данный текст: querySelectorAll обходит
// дерево в документном порядке (родитель раньше потомков) — последний совпавший и есть
// внутренний, тот, что непосредственно несёт текст
const textHolder = (full: string): Element | null => {
  let best: Element | null = null;
  for (const el of Array.from(doc.querySelectorAll('*'))) {
    if ((el.textContent ?? '').includes(full)) best = el;
  }
  return best;
};
// Обрезка текста «с многоточием»: сам элемент overflow:hidden + nowrap + ellipsis;
// minWidth0 обязателен только flex-элементам (без него текст не сжимается и вылезает
// за край) — h1 не flex, его сужает родитель-flex c minWidth:0, поэтому для него false
const expectEllipsis = (el: Element | null, what: string, minWidth0: boolean) => {
  const st = el?.getAttribute('style') ?? '';
  expect(el, `${what}: текста в DOM нет`).not.toBeNull();
  expect(st, `${what}: нет overflow:hidden`).toMatch(/overflow:\s*hidden/);
  expect(st, `${what}: нет white-space:nowrap`).toMatch(/white-space:\s*nowrap/);
  expect(st, `${what}: нет text-overflow:ellipsis`).toMatch(/text-overflow:\s*ellipsis/);
  if (minWidth0) expect(st, `${what}: нет min-width:0 — в flex текст не сожмётся`).toMatch(/min-width:\s*0/);
};
// Полный текст доступен по title — на самом текстовом элементе или на предке (метку
// несёт внутренний span Badge, title законно лежит на самом Badge)
const titleFor = (el: Element | null, full: string): boolean => {
  for (let cur = el as Element | null; cur; cur = cur.parentElement) {
    if (cur.getAttribute('title') === full) return true;
  }
  return false;
};

// Боковой отступ из inline-стиля узла: максимум из горизонтальных компонент всех
// объявлений padding (1 значение — все стороны, 2 — вертикаль/горизонталь,
// 4 — T R B L). Нет объявлений или нулевые — 0: содержимое стоит у краёв коробки.
const horizontalPad = (style: string): number => {
  let max = 0;
  for (const m of style.matchAll(/(?:^|;)\s*padding(?:-left|-right)?\s*:\s*([^;]+)/gi)) {
    const vals = m[1].trim().split(/\s+/).map(parseFloat);
    if (vals.length === 0 || vals.some(Number.isNaN)) continue;
    // padding-top/-bottom регуляркой не матчатся (после «padding» идёт «-top»/«-bottom»,
    // а не «:») — сюда попадают только горизонтальные: shorthand 1/2/3/4 значения и
    // longhand padding-left/-right
    const h = vals.length === 1 ? vals[0] : vals.length === 2 ? vals[1] : vals.length === 3 ? vals[1] : Math.max(vals[1], vals[3]);
    max = Math.max(max, h);
  }
  return max;
};

describe('curatorsLine: точные тексты строки (решение Андрея 19.11)', () => {
  it('один куратор — «Уроки видит Папа»', () => {
    expect(curatorsLine(['Папа'])).toBe('Уроки видит Папа');
  });
  it('двое — «Уроки видят Папа и Мама»', () => {
    expect(curatorsLine(['Папа', 'Мама'])).toBe('Уроки видят Папа и Мама');
  });
  it('трое и больше — «Папа, Мама и Бабушка»', () => {
    expect(curatorsLine(['Папа', 'Мама', 'Бабушка'])).toBe('Уроки видят Папа, Мама и Бабушка');
  });
  it('кураторов нет — строки нет вовсе', () => {
    expect(curatorsLine([])).toBeNull();
  });
});

describe('ChildLessonHeader: шапка урока со строкой кураторов', () => {
  beforeEach(() => {
    doc.getElementById('root')!.innerHTML = '';
    mobile = false; win.innerWidth = 1280;
  });

  it('десктоп: репетитор, метка предмета и строка «Уроки видит Папа»', async () => {
    await renderHeader({ subject: subject(), viewers: ['Папа'], title: 'Дроби', isMobile: false });
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'шапки урока нет').not.toBeNull();
    expect(doc.querySelector('[data-testid="tutor-mark"]'), 'знака репетитора нет').not.toBeNull();
    const h1 = doc.querySelector('h1');
    expect(h1?.textContent, 'имени репетитора в шапке нет').toBe('Сова Ульяна');
    expect((doc.querySelector('[data-testid="child-lesson-header"]')?.textContent ?? '').includes('Математика'), 'метки предмета нет').toBe(true);
    expect(lineText()).toBe('Уроки видит Папа');
  });

  it('десктоп, двое кураторов: «Уроки видят Папа и Мама»', async () => {
    await renderHeader({ subject: subject(), viewers: ['Папа', 'Мама'], title: 'Дроби', isMobile: false });
    expect(lineText()).toBe('Уроки видят Папа и Мама');
  });

  // Геометрия (ревью d52edd91): лента ChatPanel — колонка CHAT_MAX_W по центру, её
  // содержимое стоит у краёв колонки (поле CHAT_GUTTER_L — вне колонки, paddingLeft
  // коробки прокрутки, ChatPanel.tsx:2863/2878). Шапка обязана занять ту же колонку
  // без бокового отступа — иначе её заголовок стоит правее сообщений (было +24px).
  it('десктоп: шапка стоит ровно над лентой — колонка CHAT_MAX_W без бокового отступа', async () => {
    await renderHeader({ subject: subject(), viewers: ['Папа'], title: 'Дроби', isMobile: false });
    const head = doc.querySelector('[data-testid="child-lesson-header"]');
    expect(head, 'шапки урока нет').not.toBeNull();
    const style = head!.getAttribute('style') ?? '';
    expect(style, 'шапка не ограничена колонкой ленты').toContain(`max-width:${CHAT_MAX_W}px`);
    expect(style, 'шапка не центрирована по колонке').toMatch(/margin:\s*0(?:px)?\s+auto/);
    expect(horizontalPad(style), 'у шапки есть боковой отступ — она правее ленты').toBe(0);
  });

  it('кураторов нет — строки нет; остальная шапка на месте', async () => {
    await renderHeader({ subject: subject(), viewers: [], title: 'Дроби', isMobile: false });
    expect(lineNode(), 'строки кураторов быть не должно').toBeNull();
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'шапки урока нет').not.toBeNull();
  });

  it('до-предметный/закрытый чат: метки предмета нет, заголовок — имя чата', async () => {
    await renderHeader({ subject: null, viewers: ['Папа'], title: 'Старый урок', isMobile: false });
    expect(doc.querySelector('h1')?.textContent).toBe('Старый урок');
    expect((doc.querySelector('[data-testid="child-lesson-header"]')?.textContent ?? '').includes('Математика'), 'метки предмета быть не должно').toBe(false);
    expect(lineText()).toBe('Уроки видит Папа');
  });

  it('мобильная ширина: строка кураторов в DOM и видима, остальная шапка свёрнута', async () => {
    mobile = true; win.innerWidth = 360;
    await renderHeader({ subject: subject(), viewers: ['Папа'], title: 'Дроби', isMobile: true });
    const line = lineNode();
    expect(line, 'на мобиле строки кураторов нет').not.toBeNull();
    expect(lineText()).toBe('Уроки видит Папа');
    // «видима» = не скрыта стилем и не вне шапки: display:none/hidden у неё или родителя нет
    const hiddenStyle = (n: Element | null): boolean => {
      for (let el = n as Element | null; el; el = el.parentElement) {
        const st = (el as { getAttribute?: (a: string) => string | null }).getAttribute?.('style') ?? '';
        if (/display:\s*none/.test(st) || el.hasAttribute('hidden')) return true;
      }
      return false;
    };
    expect(hiddenStyle(line), 'строка скрыта стилем').toBe(false);
    expect(doc.querySelector('h1'), 'на мобиле остальная шапка должна быть свёрнута').toBeNull();
    expect(doc.querySelector('[data-testid="tutor-mark"]'), 'на мобиле аватара быть не должно').toBeNull();
  });

  it('мобильная ширина без кураторов: показывать нечего — шапки нет', async () => {
    mobile = true; win.innerWidth = 360;
    await renderHeader({ subject: subject(), viewers: [], title: 'Дроби', isMobile: true });
    expect(doc.querySelector('[data-testid="child-lesson-header"]')).toBeNull();
  });

  // Хвост ревью d52edd91: длинный текст шапки резался без «…» (textOverflow на
  // flex-контейнере не работает) или вылезал за край. Каждый текст обязан лежать в
  // элементе с ellipsis-стилями, полный текст — доступен по title.
  it('длинное имя предмета: текст в элементе с ellipsis-стилями, полный текст в title', async () => {
    const long = 'Литературное чтение: пересказ очень длинных рассказов и стихов';
    await renderHeader({ subject: subject({ label: long }), viewers: ['Папа'], title: 'Дроби', isMobile: false });
    const holder = textHolder(long);
    expectEllipsis(holder, 'имя предмета', true);
    expect(titleFor(holder, long), 'полного имени предмета нет в title').toBe(true);
  });

  it('длинная строка кураторов: текст в собственном span с ellipsis, полный текст в title', async () => {
    const curators = ['Папа', 'Мама', 'Бабушка', 'Очень Длинное Имя Куратора Для Проверки Обрезки'];
    const full = curatorsLine(curators)!;
    await renderHeader({ subject: subject(), viewers: curators, title: 'Дроби', isMobile: false });
    expect(lineText()).toBe(full);
    const holder = textHolder(full);
    expectEllipsis(holder, 'строка кураторов', true);
    expect(titleFor(holder, full), 'полной строки кураторов нет в title').toBe(true);
  });

  it('длинное название урока: h1 обрезается с ellipsis, полный текст в title', async () => {
    const longTitle = 'Очень длинное название урока, которое целиком не помещается в шапку';
    await renderHeader({ subject: null, viewers: ['Папа'], title: longTitle, isMobile: false });
    const h1 = doc.querySelector('h1');
    expect(h1?.textContent).toBe(longTitle);
    // h1 — не flex-элемент (minWidth не нужен): его сужает родительский div с minWidth:0
    expectEllipsis(h1, 'название урока', false);
    expect(h1?.getAttribute('title')).toBe(longTitle);
  });
});

// ── случай ChatPanel: при childFeedKinds шапка целиком детская, взрослая не монтируется ──
// mode обязателен: ChatPanel инициализирует им состояние режима (ChatPanel.tsx:850),
// без него композер падает на MODE_META[undefined]
const chatSession = { id: 's1', name: 'Дроби', projectId: null, mode: 'default', createdAt: '', updatedAt: '' } as never;

async function renderChatPanel(props: Record<string, unknown>) {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => {
    root.render(createElement(ChatPanel, {
      session: chatSession,
      attachedFiles: [],
      onAttachedFilesChange: () => {},
      ...props,
    }));
  });
  await act(async () => {});
  return root;
}

describe('ChatPanel: детская шапка вместо взрослой (Ф1h)', () => {
  beforeEach(() => {
    doc.getElementById('root')!.innerHTML = '';
    mobile = false; win.innerWidth = 1280;
  });

  it('childFeedKinds + childHeader: ChildLessonHeader с строкой кураторов, ChatHeaderBar не монтирован', async () => {
    await renderChatPanel({
      childFeedKinds: ['assistant', 'user'],
      childHeader: { subject: subject(), viewers: ['Папа', 'Мама'] },
    });
    const head = doc.querySelector('[data-testid="child-lesson-header"]');
    expect(head, 'детской шапки урока нет').not.toBeNull();
    expect((head!.textContent ?? '').includes('Сова Ульяна'), 'имени репетитора нет').toBe(true);
    expect(lineText()).toBe('Уроки видят Папа и Мама');
    expect(doc.querySelector('[data-testid="chat-header-bar-stub"]'), 'взрослая шапка ребёнку не должна монтироваться').toBeNull();
  });

  it('без childFeedKinds: взрослая шапка как раньше, детской нет', async () => {
    await renderChatPanel({});
    expect(doc.querySelector('[data-testid="chat-header-bar-stub"]'), 'взрослой шапки нет').not.toBeNull();
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской шапки быть не должно').toBeNull();
  });
});
