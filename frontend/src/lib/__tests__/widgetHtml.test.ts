import { describe, it, expect } from 'vitest';
import {
  parseWidgetInput, buildWidgetSrcDoc, clampWidgetHeight,
  WIDGET_MIN_HEIGHT, WIDGET_MAX_HEIGHT, WIDGET_MAX_HEIGHT_MOBILE, WIDGET_HEIGHT_MSG,
} from '../widgetHtml';
import { isWidgetShow } from '../../components/chat/WidgetView';

// --- parseWidgetInput: defensive-разбор input вызова widget_show ---

describe('parseWidgetInput', () => {
  it('валидный input разбирается целиком', () => {
    const r = parseWidgetInput({ html: '<b>hi</b>', title: 'Дашборд', height: 400 });
    expect(r).toEqual({ html: '<b>hi</b>', title: 'Дашборд', height: 400 });
  });

  it('мусор вместо объекта → пустые значения', () => {
    expect(parseWidgetInput(null)).toEqual({ html: '', title: '', height: null });
    expect(parseWidgetInput('строка')).toEqual({ html: '', title: '', height: null });
    expect(parseWidgetInput(42)).toEqual({ html: '', title: '', height: null });
  });

  it('нестроковый html и title игнорируются', () => {
    const r = parseWidgetInput({ html: 123, title: { a: 1 }, height: 'big' });
    expect(r).toEqual({ html: '', title: '', height: null });
  });

  it('height клампится в допустимые пределы', () => {
    expect(parseWidgetInput({ html: 'x', height: 10 }).height).toBe(WIDGET_MIN_HEIGHT);
    expect(parseWidgetInput({ html: 'x', height: 5000 }).height).toBe(WIDGET_MAX_HEIGHT);
    expect(parseWidgetInput({ html: 'x', height: NaN }).height).toBeNull();
  });
});

// --- clampWidgetHeight: пределы десктопа и мобилы ---

describe('clampWidgetHeight', () => {
  it('мобильный предел ниже десктопного', () => {
    expect(clampWidgetHeight(9999)).toBe(WIDGET_MAX_HEIGHT);
    expect(clampWidgetHeight(9999, true)).toBe(WIDGET_MAX_HEIGHT_MOBILE);
    expect(clampWidgetHeight(0)).toBe(WIDGET_MIN_HEIGHT);
  });
});

// --- buildWidgetSrcDoc: CSP, тема, авто-высота, вставка html ---

describe('buildWidgetSrcDoc', () => {
  it('содержит строгую CSP-мету (без сети) и form-action', () => {
    const doc = buildWidgetSrcDoc('<b>w</b>', 'light');
    expect(doc).toContain('Content-Security-Policy');
    expect(doc).toContain("default-src 'none'");
    expect(doc).toContain("form-action 'none'");
  });

  it('вставляет html модели в body', () => {
    expect(buildWidgetSrcDoc('<b>маркер</b>', 'light')).toContain('<b>маркер</b>');
  });

  it('темы дают разные CSS-переменные и color-scheme', () => {
    const light = buildWidgetSrcDoc('x', 'light');
    const dark = buildWidgetSrcDoc('x', 'dark');
    expect(light).toContain('color-scheme:light');
    expect(dark).toContain('color-scheme:dark');
    expect(light).not.toEqual(dark);
    // Обещанные модели переменные присутствуют
    for (const v of ['--cc-bg', '--cc-text', '--cc-accent', '--cc-border', '--cc-muted']) {
      expect(light).toContain(v);
      expect(dark).toContain(v);
    }
  });

  it('содержит скрипт авто-высоты с postMessage-типом', () => {
    expect(buildWidgetSrcDoc('x', 'light')).toContain(WIDGET_HEIGHT_MSG);
  });
});

// --- buildWidgetSrcDoc: оболочка (навигацию фрейма держит оболочка, ADR-031 §3.6) ---

// Первый <meta CSP> документа: для внешнего — CSP оболочки; для внутреннего
// (после innerDoc, где srcdoc уже разэкранирован) — CSP внутреннего документа.
function cspMeta(doc: string): string {
  const m = doc.match(/<meta http-equiv="Content-Security-Policy" content="([^"]+)"/);
  expect(m).not.toBeNull();
  return m![1];
}

// Значение srcdoc внутреннего фрейма → сам внутренний документ (обратное экранирование:
// &quot; раньше &amp;, иначе &amp;quot; из исходного текста модели развернётся неверно).
function innerDoc(doc: string): string {
  const m = doc.match(/<iframe sandbox="allow-scripts" srcdoc="([^"]*)"><\/iframe>/);
  expect(m).not.toBeNull();
  return m![1].replace(/&quot;/g, '"').replace(/&amp;/g, '&');
}

describe('buildWidgetSrcDoc: оболочка и внутренний документ', () => {
  it('CSP оболочки держит навигацию фрейма: frame-src/base-uri none', () => {
    const csp = cspMeta(buildWidgetSrcDoc('<b>w</b>', 'light'));
    expect(csp).toContain("default-src 'none'");
    expect(csp).toContain("frame-src 'none'");
    expect(csp).toContain("base-uri 'none'");
    expect(csp).toContain("script-src 'unsafe-inline'");
  });

  // Одна константа WIDGET_CSP на оба уровня (по образцу PANEL_CSP): srcdoc-документ
  // наследует CSP создателя, политики складываются пересечением (ADR-031 §3.6) —
  // более строгая оболочка вырезала бы data:/blob: картинки/шрифты/медиа самого
  // виджета (картинка data:image/png была бы битой). Список ниже — контракт виджета,
  // он держится в тесте отдельно от кода: исчезнет директива из константы — тест красный.
  const WIDGET_REQUIRED_CSP = [
    "default-src 'none'", "script-src 'unsafe-inline'", "style-src 'unsafe-inline'",
    "img-src data: blob:", "font-src data:", "media-src data: blob:",
    "form-action 'none'", "connect-src 'none'", "base-uri 'none'", "frame-src 'none'",
  ];

  it('CSP оболочки не строже внутренней: директивы виджета есть на обоих уровнях', () => {
    const doc = buildWidgetSrcDoc('<b>w</b>', 'light');
    const shell = cspMeta(doc);
    const inner = cspMeta(innerDoc(doc));
    for (const d of WIDGET_REQUIRED_CSP) {
      expect(shell).toContain(d);
      expect(inner).toContain(d);
    }
    // Каждая директива внутренней есть в оболочке: внутренняя строгой не станет
    for (const d of inner.split(';').map(s => s.trim()).filter(Boolean)) {
      expect(shell).toContain(d);
    }
  });

  it('ровно один внутренний iframe с sandbox="allow-scripts", без allow-forms/allow-modals', () => {
    const doc = buildWidgetSrcDoc('<b>hi</b>', 'light');
    expect((doc.match(/<iframe/g) ?? []).length).toBe(1);
    expect(doc).toContain('<iframe sandbox="allow-scripts" srcdoc="');
    expect(doc).not.toContain('allow-forms');
    expect(doc).not.toContain('allow-modals');
  });

  it('внутренняя CSP — прежняя плюс явные connect-src/base-uri/frame none', () => {
    const inner = innerDoc(buildWidgetSrcDoc('<b>hi</b>', 'light'));
    for (const d of [
      "default-src 'none'", "form-action 'none'",
      "connect-src 'none'", "base-uri 'none'", "frame-src 'none'",
    ]) {
      expect(inner).toContain(d);
    }
  });

  it('html с </script> и кавычками кругом даёт тот же внутренний документ', () => {
    const tricky = '<b onclick="alert(\'x\')">a</b><script>var s="</script>";</script>&amp;<i style="color:red">y</i>';
    expect(innerDoc(buildWidgetSrcDoc(tricky, 'light'))).toContain(tricky);
  });

  it('оболочка пересылает наверх только сообщения своего iframe: чужое окно игнорируется', () => {
    const doc = buildWidgetSrcDoc('<b>hi</b>', 'light');
    // Скрипт оболочки — последний <script> документа (скрипты внутреннего документа
    // сидят внутри значения srcdoc и начаться с «var f=document» не могут)
    const m = doc.match(/<script>(\(function\(\)\{var f=document[\s\S]*?)<\/script><\/body><\/html>$/);
    expect(m).not.toBeNull();
    const innerWin = { tag: 'inner' };
    const foreignWin = { tag: 'foreign' };
    const forwarded: unknown[] = [];
    const handlers: Array<(e: { source: unknown; data: unknown }) => void> = [];
    new Function('document', 'parent', 'addEventListener', m![1])(
      { querySelector: () => ({ contentWindow: innerWin }) },
      { postMessage: (data: unknown) => { forwarded.push(data); } },
      (_type: string, h: (e: { source: unknown; data: unknown }) => void) => { handlers.push(h); },
    );
    const onMessage = handlers[0];
    onMessage({ source: innerWin, data: { type: WIDGET_HEIGHT_MSG, h: 123 } });
    expect(forwarded).toEqual([{ type: WIDGET_HEIGHT_MSG, h: 123 }]);
    onMessage({ source: foreignWin, data: { type: WIDGET_HEIGHT_MSG, h: 999 } });
    expect(forwarded.length).toBe(1);
  });
});

// --- isWidgetShow: детект инструмента по суффиксу ---

describe('isWidgetShow', () => {
  it('распознаёт mcp__widgets__widget_show (и без учёта регистра/ключа сервера)', () => {
    expect(isWidgetShow('mcp__widgets__widget_show')).toBe(true);
    expect(isWidgetShow('MCP__WIDGETS__WIDGET_SHOW')).toBe(true);
    expect(isWidgetShow('mcp__other__widget_show')).toBe(true);
  });

  it('не срабатывает на прочие инструменты', () => {
    expect(isWidgetShow('Bash')).toBe(false);
    expect(isWidgetShow('mcp__widgets__something')).toBe(false);
    expect(isWidgetShow('widget_show_extra')).toBe(false);
  });
});
