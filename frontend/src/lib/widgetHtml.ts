// Сборка srcDoc для HTML-виджетов чата (mcp__widgets__widget_show) — чистые функции,
// тестируемое ядро WidgetView. HTML модели оборачивается в наш документ: строгая CSP
// (никаких внешних запросов), тема приложения через CSS-переменные и скрипт авто-высоты.

// Защитный cap рендера: MCP-сервер режет html на 64 КБ, но в историю мог попасть input
// до валидации — совсем гигантский html не рендерим вовсе (плашка вместо iframe).
export const WIDGET_MAX_RENDER_BYTES = 256 * 1024;

// Пределы высоты iframe (px); база — когда виджет ещё не сообщил свою высоту
export const WIDGET_MIN_HEIGHT = 120;
export const WIDGET_MAX_HEIGHT = 800;
export const WIDGET_MAX_HEIGHT_MOBILE = 560;
export const WIDGET_DEFAULT_HEIGHT = 320;

// Тип postMessage-сообщения из iframe виджета (единственный канал наружу)
export const WIDGET_HEIGHT_MSG = 'cc-widget-height';

export interface WidgetInput {
  html: string;
  title: string;
  height: number | null;
}

// Кламп желаемой высоты в допустимые пределы
export function clampWidgetHeight(h: number, mobile = false): number {
  const max = mobile ? WIDGET_MAX_HEIGHT_MOBILE : WIDGET_MAX_HEIGHT;
  return Math.min(max, Math.max(WIDGET_MIN_HEIGHT, Math.round(h)));
}

// Defensive-разбор input вызова widget_show: input приходит как unknown
// (из стрима или истории), мусор превращаем в пустые значения
export function parseWidgetInput(input: unknown): WidgetInput {
  if (input === null || typeof input !== 'object')
    return { html: '', title: '', height: null };
  const o = input as Record<string, unknown>;
  const html = typeof o.html === 'string' ? o.html : '';
  const title = typeof o.title === 'string' ? o.title.trim() : '';
  const height = typeof o.height === 'number' && Number.isFinite(o.height)
    ? clampWidgetHeight(o.height)
    : null;
  return { html, title, height };
}

// Пары значений темы для CSS-переменных виджета — hex из lib/theme.css
// (в srcDoc живые var(--c-*) приложения недоступны: iframe — отдельный документ)
const THEME_VARS: Record<'light' | 'dark', Record<string, string>> = {
  light: {
    '--cc-bg': '#FFFFFF',
    '--cc-text': '#39332B',
    '--cc-accent': '#D97757',
    '--cc-border': '#E0D7C8',
    '--cc-muted': '#9A8F7E',
  },
  dark: {
    '--cc-bg': '#2E2A25',
    '--cc-text': '#EDE6DB',
    '--cc-accent': '#E38A6A',
    '--cc-border': '#3D3830',
    '--cc-muted': '#8A8072',
  },
};

// Строгая CSP: никакой сети (default-src 'none'), inline-скрипты/стили работают,
// картинки/шрифты/медиа — только data:/blob:. form-action не фолбэчится в default-src —
// задан явно, чтобы submit не утёк наружу. Наша мета первая: даже если модель добавит
// свою CSP, действует пересечение (строже, не слабее).
const CSP =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
  "img-src data: blob:; font-src data:; media-src data: blob:; form-action 'none'";

// Скрипт авто-высоты: iframe в sandbox без allow-same-origin — DOM недоступен родителю,
// единственный канал — postMessage. ResizeObserver шлёт высоту при каждом изменении.
const HEIGHT_SCRIPT =
  '<script>(function(){var p=function(){parent.postMessage({type:"' + WIDGET_HEIGHT_MSG + '",' +
  'h:document.documentElement.scrollHeight},"*")};' +
  'new ResizeObserver(p).observe(document.documentElement);addEventListener("load",p)})();</script>';

// Полный документ для iframe.srcDoc: CSP + тема + авто-высота + html модели.
// Модель шлёт фрагмент без <html>/<head>/<body>; если пришёл полный документ —
// браузер распарсит вложенные теги лениво (виджет работает, но без нашей темы).
export function buildWidgetSrcDoc(html: string, theme: 'light' | 'dark'): string {
  const vars = Object.entries(THEME_VARS[theme])
    .map(([k, v]) => `${k}:${v}`)
    .join(';');
  return (
    '<!doctype html><html><head><meta charset="utf-8">' +
    `<meta http-equiv="Content-Security-Policy" content="${CSP}">` +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    `<style>:root{color-scheme:${theme};${vars}}` +
    'body{margin:0;background:var(--cc-bg);color:var(--cc-text);' +
    "font-family:'Hanken Grotesk',-apple-system,'Segoe UI',sans-serif}</style>" +
    HEIGHT_SCRIPT +
    '</head><body>' +
    html +
    '</body></html>'
  );
}
