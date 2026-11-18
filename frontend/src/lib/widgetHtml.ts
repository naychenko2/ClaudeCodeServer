// Сборка srcDoc для HTML-виджетов чата (mcp__widgets__widget_show) — чистые функции,
// тестируемое ядро WidgetView. HTML модели оборачивается в наш документ ВНУТРИ оболочки:
// оболочка держит навигацию фрейма и пересылает высоту, внутренний документ — строгая CSP,
// тема приложения через CSS-переменные и скрипт авто-высоты (та же схема, что у панелей
// плагинов, ADR-031 §3.6).

// Защитный cap рендера: MCP-сервер режет html на 64 КБ, но в историю мог попасть input
// до валидации — совсем гигантский html не рендерим вовсе (плашка вместо iframe).
export const WIDGET_MAX_RENDER_BYTES = 256 * 1024;

// Пределы высоты iframe (px); база — когда виджет ещё не сообщил свою высоту.
// Минимум низкий (48): авто-замер плотно облегает мелкие виджеты — без пустоты
// снизу, из-за которой контент выглядел «прижатым к верху» карточки
export const WIDGET_MIN_HEIGHT = 48;
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

// Единая CSP на оба уровня (meta оболочки и meta внутреннего документа), по образцу
// PANEL_CSP (ADR-031 §3.6): srcdoc-документ наследует CSP создателя, и политики
// складываются пересечением — более строгая оболочка вырезала бы data:/blob: картинки,
// шрифты и медиа самого виджета. Никакой сети (default-src 'none'), inline-скрипты/
// стили работают (style-src нужен и <style> оболочки — растяжка внутреннего iframe),
// картинки/шрифты/медиа — только data:/blob:. form-action не фолбэчится в default-src —
// задан явно, чтобы submit не утёк наружу. connect-src/base-uri/frame-src 'none' —
// явные, хотя и фолбэчатся в default-src: это читаемый контракт; frame-src 'none'
// держит навигацию фрейма (мета-CSP не ограничивает навигацию того фрейма, в котором
// документ показан сам, — её держит именно оболочка). Наша мета первая: даже если
// модель добавит свою CSP, действует пересечение (строже, не слабее).
export const WIDGET_CSP =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
  "img-src data: blob:; font-src data:; media-src data: blob:; form-action 'none'; " +
  "connect-src 'none'; base-uri 'none'; frame-src 'none'";

// Скрипт авто-высоты: iframe в sandbox без allow-same-origin — DOM недоступен родителю,
// единственный канал — postMessage. ResizeObserver шлёт высоту при каждом изменении.
// Меряем body (реальная высота контента), а НЕ documentElement: его scrollHeight не
// бывает меньше высоты iframe, из-за чего мелкий виджет не мог ужаться ниже базы
// и висел «прижатым к верху» с пустотой снизу. Скрипт стоит В КОНЦЕ body — body
// уже существует, observer вешается сразу.
const HEIGHT_SCRIPT =
  '<script>(function(){var p=function(){parent.postMessage({type:"' + WIDGET_HEIGHT_MSG + '",' +
  'h:Math.ceil(document.body.scrollHeight)},"*")};' +
  'new ResizeObserver(p).observe(document.body);addEventListener("load",p)})();</script>';

// Растяжка оболочки: html/body без полей и прокрутки, внутренний iframe — на 100%×100%.
// Без неё он жил бы в дефолтных 300×150 (тот же SHELL_STYLE, что у панелей, ADR-031 §3.6).
const SHELL_STYLE =
  'html,body{margin:0;height:100%;overflow:hidden}' +
  ' iframe{display:block;border:0;width:100%;height:100%}';

// Оболочка пересылает наверх только сообщения СВОЕГО iframe: высота от чужого окна
// (другой виджет на странице) до хоста не доходит. Скрипт — после iframe в body: элемент
// уже есть. srcdoc-фрейм наследует origin родителя (opaque у sandbox-оболочки), поэтому
// contentWindow своего iframe оболочке доступен.
const SHELL_SCRIPT =
  '<script>(function(){var f=document.querySelector("iframe");' +
  'addEventListener("message",function(e){if(e.source!==f.contentWindow)return;' +
  'parent.postMessage(e.data,"*")})})();</script>';

// Экранирование значения srcdoc (как у панелей, ADR-031 §3.6): & → &amp;, " → &quot;.
// < и > не экранируются: внутри двойнокавычного атрибута они значение не закрывают,
// а </script> в html модели внутри атрибута безопасен.
function escapeSrcdoc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
}

// Полный документ для iframe.srcDoc хоста — ОБОЛОЧКА: meta-CSP оболочки (та же
// WIDGET_CSP, что у внутреннего: навигацию фрейма держит именно оболочка) + ровно один
// <iframe sandbox="allow-scripts" srcdoc=…> с внутренним документом
// (та же CSP + тема + авто-высота + html модели, значение экранировано). Модель шлёт фрагмент
// без <html>/<head>/<body>; если пришёл полный документ — браузер распарсит вложенные теги
// лениво (виджет работает, но без нашей темы).
export function buildWidgetSrcDoc(html: string, theme: 'light' | 'dark'): string {
  const vars = Object.entries(THEME_VARS[theme])
    .map(([k, v]) => `${k}:${v}`)
    .join(';');
  const inner =
    '<!doctype html><html><head><meta charset="utf-8">' +
    `<meta http-equiv="Content-Security-Policy" content="${WIDGET_CSP}">` +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    `<style>:root{color-scheme:${theme};${vars}}` +
    'body{margin:0;background:var(--cc-bg);color:var(--cc-text);' +
    "font-family:'Hanken Grotesk',-apple-system,'Segoe UI',sans-serif}</style>" +
    '</head><body>' +
    html +
    HEIGHT_SCRIPT +
    '</body></html>';
  return (
    '<!doctype html><html><head>' +
    `<meta http-equiv="Content-Security-Policy" content="${WIDGET_CSP}">` +
    '<style>' + SHELL_STYLE + '</style>' +
    '</head><body>' +
    '<iframe sandbox="allow-scripts" srcdoc="' + escapeSrcdoc(inner) + '"></iframe>' +
    SHELL_SCRIPT +
    '</body></html>'
  );
}
