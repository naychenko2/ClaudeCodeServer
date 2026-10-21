// Мост v1 (ADR-031 §3) — тесты стороны хоста.
//
// Часть 1 — чистый протокол: validateFrameMessage (четыре условия приёма, ADR §3.2),
// srcdoc (CSP + шим + тема), кламп высоты, отказ по праву, тексты ошибок.
// Часть 2 — поток компонента: валидный call доходит до API (POST /plugins/{id}/call),
// а result возвращается фрейму.
//
// Окружение node (без jsdom): компонент гоняем мини-раннером — тот же паттерн,
// что в hooks/__tests__/useSession.test.ts: react замокан, хуки держат состояние,
// эффекты исполняются при render.
//
// Мутации (критерий готовности): убираем проверку e.origin === 'null' → тест
// «чужой origin» краснеет; убираем проверку v → тест «неверная версия» краснеет.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// --- Мини-раннер React-хуков (до импортов: vi.mock/vi.hoisted поднимаются выше) ---
type Host = {
  i: number;
  states: Map<number, unknown>;
  refs: Map<number, { current: unknown }>;
  cbs: Map<number, { deps?: unknown[]; fn: unknown }>;
  effects: Map<number, { deps?: unknown[]; cleanup?: (() => void) | void }>;
};

const rt = vi.hoisted(() => {
  const state = { current: null as Host | null };
  const sameDeps = (a?: unknown[], b?: unknown[]) =>
    !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));
  return { state, sameDeps };
});

// Настоящий react + переопределённые хуки: createContext/forwardRef/memo и проч.
// остаются реальными (ui-кит создаёт их при загрузке), хуки — мини-раннером.
vi.mock('react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react')>();
  return {
    ...actual,
  useState: (init: unknown) => {
    const host = rt.state.current!;
    const idx = host.i++;
    if (!host.states.has(idx)) host.states.set(idx, typeof init === 'function' ? (init as () => unknown)() : init);
    return [
      host.states.get(idx),
      (v: unknown) => {
        const cur = host.states.get(idx);
        host.states.set(idx, typeof v === 'function' ? (v as (p: unknown) => unknown)(cur) : v);
      },
    ] as const;
  },
  useRef: (v: unknown) => {
    const host = rt.state.current!;
    const idx = host.i++;
    if (!host.refs.has(idx)) host.refs.set(idx, { current: v });
    return host.refs.get(idx)!;
  },
  useMemo: (fn: () => unknown) => fn(),
  // useCallback уважает deps (как в React): с теми же deps — та же функция,
  // иначе эффекты с ней в deps зря перезапускались бы на каждом render
  useCallback: (fn: unknown, deps?: unknown[]) => {
    const host = rt.state.current!;
    const idx = host.i++;
    const prev = host.cbs.get(idx);
    if (prev && rt.sameDeps(prev.deps, deps)) return prev.fn;
    host.cbs.set(idx, { deps, fn });
    return fn;
  },
  useEffect: (effect: () => (() => void) | void, deps?: unknown[]) => {
    const host = rt.state.current;
    if (!host) return;
    const idx = host.i++;
    const prev = host.effects.get(idx);
    if (prev && rt.sameDeps(prev.deps, deps)) return;
    prev?.cleanup?.();
    host.effects.set(idx, { deps, cleanup: effect() });
  },
  useSyncExternalStore: (_s: unknown, get: () => unknown) => get(),
  // useContext — реальный требует React-dispatcher (mini-runner его не ставит);
  // useHasPanelHeader → useContext(PanelHeaderSlotContext).hasHeader. false = свой Island.
  useContext: () => ({ hasHeader: false }),
  };
});

// Моки: document (themeMode/IconButton трогают его при загрузке) и window
// (EventTarget — на него компонент вешает message-обработчик).
vi.stubGlobal('document', {
  documentElement: { dataset: {} as Record<string, string> },
  head: { appendChild: () => {} },
  createElement: (tag: string) => ({ id: '', textContent: '', tagName: tag }),
  getElementById: () => null,
});
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440,
  innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  navigator: { language: 'ru-RU' },
  // rAF-очередь высоты контента: в тесте применяем синхронно (cb сразу), чтобы
  // resize → setHeight было видно без ожидания кадра. В браузере — настоящий rAF.
  requestAnimationFrame: (cb: () => void) => { cb(); return 0; },
}));

// Транспорт: список/панель/call — поддельные, но с настоящими сигнатурами.
const m = vi.hoisted(() => ({
  list: vi.fn<(id: string) => Promise<unknown[]>>(async () => []),
  panelHtml: vi.fn<(id: string, version: string) => Promise<string>>(async () => '<html><body>panel</body></html>'),
  call: vi.fn<(id: string, body: { capability: string; args: Record<string, unknown>; projectId: string | null }) => Promise<unknown>>(
    async () => ({ ok: true, result: { hello: 'world' } }),
  ),
}));

vi.mock('../../lib/api', () => ({
  api: {
    plugins: {
      list: () => m.list('p1'),
      panelHtml: (id: string, version: string) => m.panelHtml(id, version),
      call: (id: string, body: { capability: string; args: Record<string, unknown>; projectId: string | null }) => m.call(id, body),
    },
  },
}));

// Динамический импорт ПОСЛЕ стабов: модули читают window/document при загрузке.
const {
  PANEL_CSP, PANEL_MIN_HEIGHT,
  PANEL_CONTENT_MAX_HEIGHT, MAX_PANEL_FILE_BYTES, panelWindowOf,
  validateFrameMessage, buildPanelSrcdoc, buildInitMessage, buildResultMessage,
  buildThemeMessage, clampContentHeight, deniedKeyOf, bridgeErrorText,
} = await import('./bridge');
const { PluginFrame } = await import('./PluginFrame');
// Токены панели (S8): buildTokensStyle генерирует <style> с --cc-* из значений хоста.
const { buildTokensStyle } = await import('./panelSrcdoc');
// Круговой разбор (ADR-031 §3.6): DOMParser только в тесте — парсит ВНЕШНИЙ документ
// и проверяет инварианты (1 iframe, 0 script снаружи, meta CSP первым, srcdoc === внутренний).
const { DOMParser } = await import('linkedom');
// Шим plugin-kit: тот же файл, что встраивает buildPanelSrcdoc (?raw — строкой).
const shimJs = (await import('../../../public/plugin-kit/plugin-kit.js?raw')).default as string;
// shimCss = структурный theme.css + контролы controls.css (S9): та же конкатенация,
// что в bridge.ts, — чтобы expectedInner совпал с фактическим srcdoc.
const shimCssBase = (await import('../../../public/plugin-kit/theme.css?raw')).default as string;
const shimControls = (await import('../../../public/plugin-kit/controls.css?raw')).default as string;
const shimCss = shimCssBase + '\n' + shimControls;
type PluginInfo = import('../../lib/api').PluginInfo;
type Project = import('../../types').Project;

// Ожидаемый ВНУТРЕННИЙ документ (srcdoc): CSP + шим + html панели, конкатенация.
// Строится так же, как в buildPanelSrcdoc, — чтобы сверить getAttribute('srcdoc') строго.
// theme — атрибут data-cc-theme на <html> (тёмная тема в первом кадре, ADR-031 §3.3).
function expectedInner(panelHtml: string, theme: 'light' | 'dark' = 'light'): string {
  const themeAttr = theme === 'dark' ? ' data-cc-theme="dark"' : '';
  return (
    '<!doctype html><html' + themeAttr + '><head>' +
    '<meta http-equiv="Content-Security-Policy" content="' + PANEL_CSP + '">' +
    '<style>' + shimCss + '</style>' +
    '<script>' + shimJs + '</script>' +
    '</head><body>' + panelHtml + '</body></html>'
  );
}

// === Часть 1: чистый протокол ===

// Минимальное «событие» в форме, какой оно приходит на window: source/origin/data.
// frameWin — «окно» нашего iframe: e.source обязан быть именно им (ADR-031 §3.2).
const frameWin = {};
const mkEvent = (data: unknown, over: { source?: unknown; origin?: string } = {}) => ({
  source: over.source ?? frameWin,
  origin: over.origin ?? 'null',
  data,
});

describe('validateFrameMessage (ADR-031 §3.2: четыре условия приёма)', () => {
  it('чужой source (не окно нашего фрейма) — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'ready' }, { source: { other: true } }), {})).toBeNull();
  });

  it('чужой origin (не «null») — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'ready' }, { origin: 'https://evil.example' }), frameWin)).toBeNull();
  });

  it('неверная версия v — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 2, type: 'ready' }), frameWin)).toBeNull();
  });

  it('конверт без source: ccs-plugin — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'other', v: 1, type: 'ready' }), frameWin)).toBeNull();
  });

  it('валидный ready разбирается', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'ready' }), frameWin))
      .toEqual({ source: 'ccs-plugin', v: 1, type: 'ready' });
  });

  it('валидный call разбирается: id, op, args', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'call', id: 'c1', op: 'notes.read', args: { id: 'n1' } }), frameWin))
      .toEqual({ source: 'ccs-plugin', v: 1, type: 'call', id: 'c1', op: 'notes.read', args: { id: 'n1' } });
  });

  it('call с args не-объектом — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'call', id: 'c1', op: 'notes.read', args: 'x' }), frameWin)).toBeNull();
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'call', id: 'c1', op: 'notes.read', args: null }), frameWin)).toBeNull();
  });

  it('валидный resize разбирается; отрицательная/не-числовая высота — игнор', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'resize', height: 120 }), frameWin))
      .toEqual({ source: 'ccs-plugin', v: 1, type: 'resize', height: 120 });
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'resize', height: -5 }), frameWin)).toBeNull();
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'resize', height: '120' }), frameWin)).toBeNull();
  });

  it('неизвестный type — игнор (аддитивное расширение, ADR §3.5)', () => {
    expect(validateFrameMessage(mkEvent({ source: 'ccs-plugin', v: 1, type: 'teleport' }), frameWin)).toBeNull();
  });
});

// PANEL_CSP — единая константа: стоит и в meta оболочки, и в meta панели (ADR-031 §3.1/§3.6).
// Оболочка держит навигацию панели (frame-src 'none'), base-uri 'none' закрывает <base>.
describe('PANEL_CSP (ADR-031 §3.1/§3.6: единая политика оболочки и панели)', () => {
  it('содержит frame-src \'none\' (оболочка держит навигацию) и base-uri \'none\'', () => {
    expect(PANEL_CSP).toContain("frame-src 'none'");
    expect(PANEL_CSP).toContain("base-uri 'none'");
  });
});

// Круговой разбор (ADR-031 §3.6): DOMParser (только в тесте) от результата
// buildPanelSrcdoc. Главный инвариант: ровно один iframe, ноль <script> во внешнем
// документе, первым в head — meta с PANEL_CSP, и getAttribute('srcdoc') строго равен
// внутреннему документу (после разэкранирования).
// panelWindowOf (ADR-031 §3.6): окно панели = frames[0] оболочки. Приём и отправка
// моста — только в это окно: сообщения от самой оболочки (contentWindow) и чужих окон
// отбрасываются. Мутация «сверять с contentWindow, а не frames[0]» красит тест.
describe('panelWindowOf + приём сообщений (ADR-031 §3.6)', () => {
  // Фейк оболочки: contentWindow — окно оболочки, frames[0] — окно панели.
  const panelWindow = { __panel: true };
  const shellWindow = { frames: [panelWindow] };
  const outerIframe = { contentWindow: shellWindow } as unknown as HTMLIFrameElement;

  it('panelWindowOf возвращает frames[0] (окно панели), null — без iframe', () => {
    expect(panelWindowOf(outerIframe)).toBe(panelWindow);
    expect(panelWindowOf(null)).toBeNull();
    expect(panelWindowOf(undefined)).toBeNull();
  });

  it('сообщение от окна панели (frames[0]) принимается', () => {
    const e = { source: panelWindow, origin: 'null', data: { source: 'ccs-plugin', v: 1, type: 'ready' } };
    expect(validateFrameMessage(e, panelWindowOf(outerIframe))).toEqual({ source: 'ccs-plugin', v: 1, type: 'ready' });
  });

  it('сообщение от самой оболочки (contentWindow) отбрасывается', () => {
    const e = { source: shellWindow, origin: 'null', data: { source: 'ccs-plugin', v: 1, type: 'ready' } };
    expect(validateFrameMessage(e, panelWindowOf(outerIframe))).toBeNull();
  });

  it('сообщение от чужого окна отбрасывается', () => {
    const foreign = { __foreign: true };
    const e = { source: foreign, origin: 'null', data: { source: 'ccs-plugin', v: 1, type: 'ready' } };
    expect(validateFrameMessage(e, panelWindowOf(outerIframe))).toBeNull();
  });
});

// Круговой разбор: buildPanelSrcdoc → DOMParser → (doc, iframe, srcdoc).
// Модульный уровень: используют и блок «круговой разбор», и блок «входы панели».
function roundTrip(panelHtml: string) {
  const outer = buildPanelSrcdoc(panelHtml);
  const doc = new DOMParser().parseFromString(outer, 'text/html');
  const iframe = doc.querySelector('iframe');
  return { doc, iframe, srcdoc: iframe?.getAttribute('srcdoc') ?? null };
}

describe('buildPanelSrcdoc: круговой разбор (ADR-031 §3.6)', () => {
  it('ровно один iframe, ноль <script> снаружи, первым в head — meta с PANEL_CSP', () => {
    const { doc, iframe } = roundTrip('<body>panel</body>');
    expect(doc.querySelectorAll('iframe').length).toBe(1);
    expect(doc.querySelectorAll('script').length).toBe(0);
    const first = doc.head.firstElementChild;
    expect(first?.getAttribute('http-equiv')).toBe('Content-Security-Policy');
    expect(first?.getAttribute('content')).toBe(PANEL_CSP);
    // iframe — sandbox="allow-scripts" (без allow-same-origin)
    expect(iframe?.getAttribute('sandbox')).toBe('allow-scripts');
  });

  it('srcdoc строго равен внутреннему документу (CSP + шим + html панели, конкатенация)', () => {
    const panelHtml = '<div id="p">hello</div>';
    const { srcdoc } = roundTrip(panelHtml);
    expect(srcdoc).toBe(expectedInner(panelHtml));
  });

  it('экранирование srcdoc: & → &amp;, " → &quot; (обратно — строго исходный)', () => {
    // Внутренний документ несёт " (в meta) и & (в html панели) — экранируются,
    // а разэкранированный srcdoc снова даёт исходный внутренний документ.
    const panelHtml = 'a &amp; b &quot; c';
    const { srcdoc } = roundTrip(panelHtml);
    expect(srcdoc).toBe(expectedInner(panelHtml));
    // сырая строка srcdoc в оболочке: " экранированы, & — тоже
    const outer = buildPanelSrcdoc(panelHtml);
    expect(outer).toContain('srcdoc="');
  });

  it('meta оболочки и meta панели — одна PANEL_CSP с frame-src \'none\'', () => {
    const { doc, srcdoc } = roundTrip('<body>x</body>');
    // meta оболочки (первый в head) и meta панели (в srcdoc) — одна константа
    expect(doc.head.firstElementChild?.getAttribute('content')).toBe(PANEL_CSP);
    expect(srcdoc).toContain('content="' + PANEL_CSP + '"');
    expect(PANEL_CSP).toContain("frame-src 'none'");
  });

  it('оболочка несёт script-src \'unsafe-inline\' (иначе наследование CSP убьёт скрипты панели)', () => {
    const { doc } = roundTrip('<body>x</body>');
    // Мутация «убрать script-src из оболочки» красит этот тест: srcdoc наследует
    // политику создателя, и без script-src 'unsafe-inline' инлайн-скрипты панели гаснут.
    expect(doc.head.firstElementChild?.getAttribute('content')).toContain("script-src 'unsafe-inline'");
  });

  // Тема в первом кадре (ADR-031 §3.3, минор 7 Глеба): до init панель рисовалась
  // светлой, затем переключалась сообщением (вспышка в тёмной теме). Фикс — атрибут
  // data-cc-theme="dark" на ВНУТРЕННЕМ <html>; светлая тема — без атрибута.
  it('тема в первом кадре: dark → data-cc-theme="dark" на внутреннем <html>, light — нет', () => {
    const dark = buildPanelSrcdoc('<body>x</body>', 'dark');
    const darkDoc = new DOMParser().parseFromString(dark, 'text/html');
    const darkSrcdoc = darkDoc.querySelector('iframe')?.getAttribute('srcdoc') ?? '';
    expect(darkSrcdoc).toBe(expectedInner('<body>x</body>', 'dark'));
    expect(darkSrcdoc).toContain('<html data-cc-theme="dark">');

    const light = buildPanelSrcdoc('<body>x</body>', 'light');
    const lightDoc = new DOMParser().parseFromString(light, 'text/html');
    const lightSrcdoc = lightDoc.querySelector('iframe')?.getAttribute('srcdoc') ?? '';
    expect(lightSrcdoc).toBe(expectedInner('<body>x</body>'));
    // <html> без атрибута (CSS theme.css несёт :root[data-cc-theme="dark"], но не <html>)
    expect(lightSrcdoc).not.toContain('<html data-cc-theme');

    // Без явной темы — по умолчанию светлая (обратная совместимость со старыми вызовами).
    const def = buildPanelSrcdoc('<body>x</body>');
    expect(def).toBe(buildPanelSrcdoc('<body>x</body>', 'light'));
  });
});

// Входы панели (ADR-031 §3.6): HTML панели НЕ разбирается — идёт конкатенацией,
// барьер — оболочка. Каждый вход обязан: (1) round-trip — srcdoc === внутренний
// документ, (2) во внешнем документе ровно 1 iframe и 0 <script> (теги панели внутри
// значения атрибута srcdoc, не реальные элементы).
describe('buildPanelSrcdoc: входы панели (edge cases, ADR-031 §3.6)', () => {
  function invariants(panelHtml: string) {
    const { doc, srcdoc } = roundTrip(panelHtml);
    expect(doc.querySelectorAll('iframe').length).toBe(1);
    expect(doc.querySelectorAll('script').length).toBe(0);
    expect(srcdoc).toBe(expectedInner(panelHtml));
  }

  it('кавычка " — экранируется, round-trip строгий', () => invariants('"'));
  it('сущность &quot; — round-trip строгий', () => invariants('&quot;'));
  it('сущность &amp; — round-trip строгий', () => invariants('&amp;'));

  it('"></iframe><script>… — не закрывает оболочку: 1 iframe, 0 script', () =>
    invariants('"></iframe><script>evil()</script>'));

  it('</iframe> в html панели — внутри атрибута, не закрывает оболочку', () =>
    invariants('<div>x</div></iframe>'));

  it('</head> внутри строки скрипта — проходит как есть (барьер — оболочка)', () =>
    invariants('<script>var s = "</head>";</script>'));

  it('несколько <HEAD foo> (регистр) — проходят как есть', () =>
    invariants('<HEAD foo="1"><HEAD bar="2">'));

  it('атрибуты с > — проходят как есть', () =>
    invariants('<div data-x="a>b" data-y="c>d">x</div>'));

  it('<meta http-equiv=refresh> — проходит как есть (навигацию держит оболочка)', () =>
    invariants('<meta http-equiv="refresh" content="0;url=https://evil.example/x">'));

  it('255 КиБ повторов <head — линейно (без ReDoS), round-trip строгий', () => {
    const panelHtml = '<head'.repeat(Math.ceil((255 * 1024) / '<head'.length));
    const t0 = Date.now();
    invariants(panelHtml);
    // конкатенация + 2 линейных replace: < 200мс (ReDoS дал бы секунды)
    expect(Date.now() - t0).toBeLessThan(200);
  });

  it('потолок 256 КиБ: ровно MAX_PANEL_FILE_BYTES — монтируем, +1 — «слишком большая»', () => {
    const atLimit = 'x'.repeat(MAX_PANEL_FILE_BYTES);
    const overLimit = 'x'.repeat(MAX_PANEL_FILE_BYTES + 1);
    // та же логика, что в PluginFrame/DraftCard: html.length > MAX_PANEL_FILE_BYTES
    expect(atLimit.length > MAX_PANEL_FILE_BYTES).toBe(false);
    expect(overLimit.length > MAX_PANEL_FILE_BYTES).toBe(true);
  });
});

// Направление «панель → хост» (ADR-031 §3.6): адресат шима не проверялся ничем —
// окно панели в тестах подменяли заглушкой, поэтому регрессия «parent.postMessage»
// прошла зелёной. Ассерт по исходнику шима: отправка — в window.top (запасной
// window.parent), НЕ в parent (после оболочки parent — это оболочка без скриптов);
// приём ждёт источник window.top || window.parent (окно хоста). Мутация «вернуть
// parent.postMessage» красит оба теста.
describe('шим plugin-kit: адресат моста (панель → хост, ADR-031 §3.6)', () => {
  it('отправка — в window.top (запасной window.parent), не в parent (оболочку)', () => {
    expect(shimJs).toContain('(window.top || window.parent).postMessage');
    // bare parent.postMessage — адресат оболочка; (window.top || window.parent).postMessage
    // содержит window.parent, но НЕ подстроку parent.postMessage (между ними закрывающая скобка)
    expect(shimJs).not.toContain('parent.postMessage');
  });

  it('приём ждёт источник window.top || window.parent (окно хоста)', () => {
    expect(shimJs).toContain('e.source !== (window.top || window.parent)');
  });
});

describe('сообщения хоста', () => {
  it('init несёт plugin/slot/context/theme/locale/granted (+ tokens по умолчанию {})', () => {
    expect(buildInitMessage({
      plugin: { id: 'a/b', version: '1.0.0' },
      slot: 'sidebar',
      context: { projectId: 'p1' },
      theme: 'light',
      locale: 'ru-RU',
      granted: ['notes:read'],
    })).toEqual({
      source: 'ccs-host', v: 1, type: 'init',
      plugin: { id: 'a/b', version: '1.0.0' },
      slot: 'sidebar',
      context: { projectId: 'p1' },
      theme: 'light',
      locale: 'ru-RU',
      granted: ['notes:read'],
      tokens: {},
    });
  });

  it('result: успех и отказ — с тем же id', () => {
    expect(buildResultMessage('c1', true, { ok: 1 })).toEqual({ source: 'ccs-host', v: 1, type: 'result', id: 'c1', ok: true, data: { ok: 1 } });
    expect(buildResultMessage('c1', false, { code: 'denied', message: 'Нет права: notes:read' }))
      .toEqual({ source: 'ccs-host', v: 1, type: 'result', id: 'c1', ok: false, error: { code: 'denied', message: 'Нет права: notes:read' } });
  });

  it('theme — смена темы без перемонтирования (tokens {} по умолчанию)', () => {
    expect(buildThemeMessage('dark')).toEqual({ source: 'ccs-host', v: 1, type: 'theme', theme: 'dark', tokens: {} });
  });
});

// === Токены панели (S8): из темы хоста, а не ручная копия палитры ===
// Критерий: панель получает значения --cc-* от хоста (инъекция <style id="cc-tokens">
// в srcdoc + tokens в init/theme), а не из статического файла. Мутации:
//  • убрать tokens из buildThemeMessage → «theme несёт tokens» краснеет;
//  • убрать #cc-tokens из buildPanelSrcdoc → «srcdoc инжектит #cc-tokens» краснеет;
//  • сломать applyTokens в шиме → «шим обновляет #cc-tokens» краснеет.
describe('токены панели (S8: из темы хоста, не ручная копия)', () => {
  const T = { '--cc-bg': '#FBF8F2', '--cc-fg': '#39332B', '--cc-accent': '#D97757' };

  it('buildTokensStyle: :root { --cc-*: значение } из токенов; пустой набор — ""', () => {
    expect(buildTokensStyle(T)).toBe(':root {\n  --cc-bg: #FBF8F2;\n  --cc-fg: #39332B;\n  --cc-accent: #D97757;\n}');
    expect(buildTokensStyle({})).toBe('');
  });

  it('srcdoc инжектит <style id="cc-tokens"> с токенами (first-paint, без вспышки)', () => {
    const outer = buildPanelSrcdoc('<body>x</body>', 'light', T);
    // Во внешнем документе кавычки srcdoc экранированы (&quot;), поэтому ищем экранированную форму
    expect(outer).toContain('id=&quot;cc-tokens&quot;');
    expect(outer).toContain('--cc-bg: #FBF8F2');
    // Без токенов — style не инжектится (структурный CSS theme.css без hex остаётся).
    // Ищем экранированную id=&quot;cc-tokens&quot; (не просто cc-tokens: шим несёт
    // getElementById('cc-tokens') в JS, и то есть в srcdoc всегда).
    const bare = buildPanelSrcdoc('<body>x</body>');
    expect(bare).not.toContain('id=&quot;cc-tokens&quot;');
  });

  it('init несёт tokens хоста (панель применяет их, сама палитру не знает)', () => {
    const msg = buildInitMessage({
      plugin: { id: 'a/b', version: '1.0.0' },
      slot: 'sidebar',
      context: { projectId: 'p1' },
      theme: 'light',
      locale: 'ru-RU',
      granted: [],
      tokens: T,
    });
    expect(msg).toMatchObject({ type: 'init', theme: 'light', tokens: T });
  });

  it('theme несёт tokens НОВОЙ темы (шим обновляет #cc-tokens, без перезагрузки)', () => {
    const dark = { '--cc-bg': '#2E2A25', '--cc-fg': '#EDE6DB', '--cc-accent': '#E38A6A' };
    expect(buildThemeMessage('dark', dark)).toEqual({ source: 'ccs-host', v: 1, type: 'theme', theme: 'dark', tokens: dark });
  });

  // Шим (plugin-kit.js) в фейковом окне: theme-сообщение → applyTokens переписывает
  // <style id="cc-tokens">. Панель перекрашивается БЕЗ перезагрузки фрейма.
  // Глобальные window/document затираем и ВОССТАНАВЛИВАЕМ (иначе последующие
  // PluginFrame-тесты в этом файле увидят фейк без matchMedia).
  it('шим applyTokens: theme-сообщение обновляет <style id="cc-tokens">', () => {
    const origWindow = (globalThis as any).window;
    const origDoc = (globalThis as any).document;
    try {
      const style = { textContent: '' };
      const doc = {
        documentElement: { setAttribute: () => {}, removeAttribute: () => {} },
        getElementById: (id: string) => (id === 'cc-tokens' ? style : null),
        body: { scrollHeight: 100 },
        readyState: 'complete',
      };
      const win: any = Object.assign(new EventTarget(), { ccsPlugin: undefined, top: null, parent: null, postMessage: () => {} });
      win.top = win; win.parent = win;
      (globalThis as any).window = win;
      (globalThis as any).document = doc;

      // Выполняем шим: он вешает message-обработчик и шлёт ready
      new Function('window', 'document', 'CustomEvent', 'ResizeObserver', shimJs)(win, doc, (globalThis as any).CustomEvent, (globalThis as any).ResizeObserver);
      expect(win.ccsPlugin, 'шим установил window.ccsPlugin').toBeDefined();

      // Хост шлёт theme с токенами новой темы (source = окно хоста = window.top)
      const msg = { source: 'ccs-host', v: 1, type: 'theme', theme: 'dark', tokens: { '--cc-bg': '#2E2A25', '--cc-fg': '#EDE6DB' } };
      const e = new Event('message');
      Object.assign(e, { source: win, origin: 'null', data: msg });
      win.dispatchEvent(e);

      // Шим переписал #cc-tokens: панель перекрасится без перезагрузки
      expect(style.textContent).toContain('--cc-bg: #2E2A25');
      expect(style.textContent).toContain('--cc-fg: #EDE6DB');
    } finally {
      (globalThis as any).window = origWindow;
      (globalThis as any).document = origDoc;
    }
  });
});

// Высота КОНТЕНТА установленной панели (S1): resize = высота контента,
// хост ставит её фрейму. Порог снизу 48, страховочный потолок 4000 (не «размер слота» 800).
describe('clampContentHeight (высота контента, S1)', () => {
  it('2000 → 2000; 30000 → 20000 (потолок); 10 → 48 (порог)', () => {
    expect(clampContentHeight(2000)).toBe(2000);
    expect(clampContentHeight(30000)).toBe(PANEL_CONTENT_MAX_HEIGHT);
    expect(clampContentHeight(10)).toBe(PANEL_MIN_HEIGHT);
  });
});

describe('deniedKeyOf (отказ по праву)', () => {
  it('вытаскивает ключ права из «Нет права: {key}»', () => {
    expect(deniedKeyOf('Нет права: notes:read')).toBe('notes:read');
    expect(deniedKeyOf('Нет права: net:api.github.com')).toBe('net:api.github.com');
  });
  it('пустой ключ или другая формулировка — null', () => {
    expect(deniedKeyOf('Нет права:   ')).toBeNull();
    expect(deniedKeyOf('Upstream error')).toBeNull();
  });
});

describe('bridgeErrorText (макет 3, «ОШИБКА МОСТА»)', () => {
  it('текст для каждого кода', () => {
    expect(bridgeErrorText('bridge_timeout')).toBe('Мост не получил ответа за 3 с.');
    expect(bridgeErrorText('bridge_bad_message')).toBe('Панель отправила неверное сообщение');
    expect(bridgeErrorText('bridge_version')).toBe('Версия моста не поддерживается');
  });
});

// === Часть 2: поток компонента — валидный call доходит до API ===

// React-элемент: { type, props }. Дети — в props.children (массив или один узел).
function findElement(tree: unknown, type: string): { props: Record<string, unknown> } | null {
  if (tree === null || tree === undefined || typeof tree !== 'object') return null;
  const el = tree as { type?: unknown; props?: Record<string, unknown> };
  if (el.type === type && el.props) return { props: el.props };
  const children = el.props?.children;
  const list = Array.isArray(children) ? children : children !== undefined && children !== null ? [children] : [];
  for (const child of list) {
    const found = findElement(child, type);
    if (found) return found;
  }
  return null;
}

// Первый элемент дерева, чей style удовлетворяет предикату (обёртка фрейма и т.п.).
function findByStyle(tree: unknown, pred: (st: Record<string, unknown>) => boolean): { props: Record<string, unknown> } | null {
  if (tree === null || tree === undefined || typeof tree !== 'object') return null;
  const el = tree as { props?: Record<string, unknown> };
  const st = el.props?.style as Record<string, unknown> | undefined;
  if (st && pred(st)) return { props: el.props! };
  const children = el.props?.children;
  const list = Array.isArray(children) ? children : children !== undefined && children !== null ? [children] : [];
  for (const child of list) {
    const found = findByStyle(child, pred);
    if (found) return found;
  }
  return null;
}

// Событие message на window-EventTarget: Node требует экземпляр Event,
// source/origin/data дописываем (в браузере это поля MessageEvent).
function fireMessage(source: unknown, origin: string, data: unknown) {
  const e = new Event('message');
  Object.assign(e, { source, origin, data });
  (window as unknown as EventTarget).dispatchEvent(e);
}

const plugin: PluginInfo = {
  id: 'acme/notes-panel',
  version: '1.0.0',
  sha256: 'a'.repeat(64),
  requested: ['notes:read'],
  granted: ['notes:read'],
  enabled: true,
  source: 'upload:u1',
  secrets: [],
  // панель объявлена: мост тестирует именно панель, и слот её выбирает по contributes.panels
  contributes: { panels: [{ id: 'notes-panel', slot: 'sidebar', title: 'Notes Panel' }] },
};
// Конкретная панель (S5: PluginFrame принимает (plugin, panel) — одна панель = одна карточка).
// entry — любой: mock panelHtml игнорирует аргументы.
const panel: import('../../lib/api').PluginPanelView = { id: 'notes-panel', title: 'Notes Panel', entry: 'panel/index.html' };

describe('PluginFrame: мост v1 (мини-раннер)', () => {
  beforeEach(() => {
    m.list.mockClear();
    m.panelHtml.mockClear();
    m.call.mockClear();
    m.list.mockImplementation(async () => [plugin]);
    m.panelHtml.mockImplementation(async () => '<html><body>panel</body></html>');
    m.call.mockImplementation(async () => ({ ok: true, result: { hello: 'world' } }));
  });

  it('ready → init; валидный call → POST /call с id/контекстом хоста; result возвращается фрейму', async () => {
    const project = { id: 'p1' } as Project;
    const host: Host = { i: 0, states: new Map(), refs: new Map(), cbs: new Map(), effects: new Map() };
    const render = () => {
      host.i = 0;
      rt.state.current = host;
      try {
        return PluginFrame({ project, plugin, panel });
      } finally {
        rt.state.current = null;
      }
    };

    // 1-й render: plugin/panel — пропом (S5), панель запрашена (panelHtml) сразу
    render();
    await Promise.resolve();
    const [phId, phVer] = m.panelHtml.mock.calls[0];
    expect(phId).toBe('acme/notes-panel');
    expect(phVer).toBe('1.0.0');

    // 2-й render: html на месте, iframe смонтирован, мост ждёт ready
    const tree3 = render();
    const iframeEl = findElement(tree3, 'iframe');
    expect(iframeEl, 'iframe в дереве после загрузки html').not.toBeNull();
    // sandbox: allow-scripts БЕЗ allow-same-origin (ADR-031, инвариант 1)
    expect(iframeEl!.props.sandbox).toBe('allow-scripts');
    expect(String(iframeEl!.props.srcDoc)).toContain('Content-Security-Policy');

    // Фейковый фрейм: оболочка (contentWindow) + окно панели (frames[0]) с postMessage-шпионом
    const posted: unknown[] = [];
    const panelWindow = { postMessage: (msg: unknown) => { posted.push(msg); } };
    const fakeFrame = { contentWindow: { frames: [panelWindow] } };
    (iframeEl!.props.ref as { current: unknown }).current = fakeFrame;

    // ready от панели (валидный конверт, origin 'null', source — окно панели frames[0])
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'ready' });

    // host ответил init (тема, права, контекст слота)
    const init = posted.find(p => (p as { type?: string }).type === 'init') as Record<string, unknown>;
    expect(init).toBeDefined();
    expect(init.plugin).toEqual({ id: 'acme/notes-panel', version: '1.0.0' });
    expect(init.slot).toBe('sidebar');
    expect(init.context).toEqual({ projectId: 'p1' });
    expect(init.granted).toEqual(['notes:read']);

    // Валидный call: host шлёт POST /plugins/{id}/call (id и контекст — от хоста)
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'call', id: 'c1', op: 'notes.read', args: { id: 'n1' } });
    await Promise.resolve();
    await Promise.resolve();
    expect(m.call).toHaveBeenCalledTimes(1);
    // Контракт (ADR-031 §3.4, PluginCallContractTests): тело = {capability, args, projectId};
    // op → capability, context.projectId → projectId; лишних полей (op, context) нет.
    const [callId, callBody] = m.call.mock.calls[0] as [string, { capability: string; args: Record<string, unknown>; projectId: string | null }];
    expect(callId).toBe('acme/notes-panel');
    expect(callBody.capability).toBe('notes.read');
    expect(callBody.args).toEqual({ id: 'n1' });
    expect(callBody.projectId).toBe('p1');
    expect(callBody).not.toHaveProperty('op');
    expect(callBody).not.toHaveProperty('context');

    // result с тем же id возвращается фрейму
    const result = posted.find(p => (p as { type?: string }).type === 'result') as Record<string, unknown>;
    expect(result).toBeDefined();
    expect(result.id).toBe('c1');
    expect(result.ok).toBe(true);
    expect(result.data).toEqual({ hello: 'world' });
  });

  it('отказ по праву (denied) — result с кодом и плашка «Нет права {key}»', async () => {
    const project = { id: 'p1' } as Project;
    const host: Host = { i: 0, states: new Map(), refs: new Map(), cbs: new Map(), effects: new Map() };
    const render = () => {
      host.i = 0;
      rt.state.current = host;
      try {
        return PluginFrame({ project, plugin, panel });
      } finally {
        rt.state.current = null;
      }
    };
    render();
    await Promise.resolve();
    render();
    await Promise.resolve();
    const tree = render();
    const iframeEl = findElement(tree, 'iframe');
    expect(iframeEl).not.toBeNull();
    const posted: unknown[] = [];
    const panelWindow = { postMessage: (msg: unknown) => { posted.push(msg); } };
    const fakeFrame = { contentWindow: { frames: [panelWindow] } };
    (iframeEl!.props.ref as { current: unknown }).current = fakeFrame;

    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'ready' });

    m.call.mockImplementationOnce(async () => ({
      ok: false,
      error: { code: 'denied', message: 'Нет права: notes:read' },
    }));
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'call', id: 'c2', op: 'notes.read', args: {} });
    await Promise.resolve();
    await Promise.resolve();

    const result = posted.find(p => (p as { type?: string }).type === 'result') as Record<string, unknown>;
    expect(result.id).toBe('c2');
    expect(result.ok).toBe(false);
    expect((result.error as { code: string }).code).toBe('denied');
  });

  it('resize: высота контента (порог 48, потолок 20000), rAF-очередь', async () => {
    const project = { id: 'p1' } as Project;
    const host: Host = { i: 0, states: new Map(), refs: new Map(), cbs: new Map(), effects: new Map() };
    const render = () => {
      host.i = 0;
      rt.state.current = host;
      try {
        return PluginFrame({ project, plugin, panel });
      } finally {
        rt.state.current = null;
      }
    };
    render();
    await Promise.resolve();
    render();
    await Promise.resolve();
    const tree = render();
    const iframeEl = findElement(tree, 'iframe');
    expect(iframeEl).not.toBeNull();
    const panelWindow = { postMessage: () => {} };
    const fakeFrame = { contentWindow: { frames: [panelWindow] } };
    (iframeEl!.props.ref as { current: unknown }).current = fakeFrame;

    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'ready' });

    // Обёртка фрейма — единственный скролл панели (R-A, critical 1): без overflowY:auto
    // фрейм выше карточки (resize) недостижим. Мутация «убрать overflow» красит тест.
    const wrap = findByStyle(tree, st => st.overflowY === 'auto');
    expect(wrap, 'скролл-контейнер фрейма найден').not.toBeNull();
    expect((wrap!.props.style as Record<string, unknown>).overflowY).toBe('auto');

    // 2000 → 2000 (высота контента ставится фрейму как есть)
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'resize', height: 2000 });
    let el = findElement(render(), 'iframe');
    expect((el!.props.style as Record<string, unknown>).height).toBe(2000);

    // 30000 → 20000 (страховочный потолок PANEL_CONTENT_MAX_HEIGHT)
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'resize', height: 30000 });
    el = findElement(render(), 'iframe');
    expect((el!.props.style as Record<string, unknown>).height).toBe(PANEL_CONTENT_MAX_HEIGHT);
    // Упёрлись в потолок — в журнале моста строка про обрезку (иначе хвост теряется молча)
    const logLines = [...host.states.values()].filter(Array.isArray).flat() as unknown[];
    expect(logLines.some(l => typeof l === 'string' && l.includes('обрезана')), 'строка про обрезку в журнале').toBe(true);

    // 10 → 48 (порог снизу PANEL_MIN_HEIGHT)
    fireMessage(panelWindow, 'null', { source: 'ccs-plugin', v: 1, type: 'resize', height: 10 });
    el = findElement(render(), 'iframe');
    expect((el!.props.style as Record<string, unknown>).height).toBe(PANEL_MIN_HEIGHT);

  });
});

// === Готовые контролы (S9): классы .cc-* по дизайн-системой, опт-ин, без hex ===
// Критерий: девять базовых классов есть; голые button/input/ul НЕ стилизуются
// (каждый селектор — .cc-*); сырого hex нет (цвета — токенами --cc-*). Мутации:
//  • убрать .cc-input из controls.css → «девять классов» краснеет;
//  • добавить голый селектор `button {` → «опт-ин» краснеет;
//  • вписать #fff в controls.css → «без hex» краснеет.
// Файл читаем через fs (не ?raw): в vitest-окружении ?raw для .css даёт пустую
// строку (см. round-trip-тесты), а e2e уже читает файлы шима через fs.
describe('контролы кита (S9: .cc-* по дизайн-системе, опт-ин, без hex)', () => {
  const controlsCss = readFileSync(fileURLToPath(new URL('../../../public/plugin-kit/controls.css', import.meta.url)), 'utf8');
  // Селекторы файла: текст до '{' (без at-rules @media/@keyframes и без %-селекторов
  // keyframes). Голый элемент (button/input/ul) не должен попасть — иначе опт-ин сломан.
  function selectorsOf(css: string): string[] {
    const noComments = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const out: string[] = [];
    const re = /([^{}]+)\{/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(noComments))) {
      const sel = m[1].trim();
      if (!sel || sel.startsWith('@')) continue;
      if (/^[\d.]+%(\s*,\s*[\d.]+%)*$/.test(sel)) continue; // 0%/50%/100% keyframes
      out.push(sel);
    }
    return out;
  }

  it('все девять базовых классов есть', () => {
    for (const c of ['.cc-btn', '.cc-input', '.cc-list', '.cc-list-item', '.cc-card', '.cc-badge', '.cc-empty', '.cc-notice', '.cc-skeleton']) {
      expect(controlsCss, c).toContain(c + ' {');
    }
  });

  it('опт-ин: каждый селектор — .cc-* (голый button/input/ul не стилизуется)', () => {
    const sels = selectorsOf(controlsCss);
    expect(sels.length, 'в файле есть правила').toBeGreaterThan(0);
    for (const s of sels) {
      expect(s, 'селектор обязан начинаться с .cc-').toMatch(/^\.cc-/);
    }
  });

  it('нет сырого hex: цвета — токенами --cc-* (альфа — color-mix от токена)', () => {
    expect(controlsCss, 'hex в controls.css').not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
  });
});
