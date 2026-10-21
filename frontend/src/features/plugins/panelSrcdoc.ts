// Сборка srcdoc панели плагина (ADR-031 §3.6) — БЕЗ ?raw: shimJs/shimCss приходят
// параметрами. bridge.ts подставляет ?raw-импорты; e2e передаёт файлы через fs.
// CSP и экранирование — только здесь (единая точка).

// CSP srcdoc (ADR-031 §3.1): внешних ресурсов нет, inline-скрипты/стили работают,
// сеть из кадра закрыта — весь выход наружу только мостом. Единая константа: стоит
// и в meta оболочки, и в meta панели (§3.6) — оболочка держит навигацию (frame-src 'none'),
// base-uri 'none' закрывает <base>.
export const PANEL_CSP =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
  "img-src data: blob:; connect-src 'none'; form-action 'none'; " +
  "base-uri 'none'; frame-src 'none'";

// Экранирование значения атрибута srcdoc (ADR-031 §3.6): & → &amp;, " → &quot;,
// и больше ничего. < и > не экранируются: внутри двойнокавычного атрибута они
// значение не закрывают, а HTML панели идёт «как есть» (без разбора).
function escapeSrcdoc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
}

// ЕДИНАЯ сборка документа панели: для установленной панели (PluginFrame) и для
// предпросмотра черновика (DraftCard) — одна функция, одна PANEL_CSP. Возвращает
// ВНЕШНИЙ документ (оболочку): meta-CSP PANEL_CSP + ровно один
// <iframe sandbox="allow-scripts" srcdoc="…">. Навигацию панели держит оболочка
// (frame-src 'none' в PANEL_CSP): браузер сверяет навигацию фрейма с политикой
// родителя. srcdoc — внутренний документ: CSP + шим + HTML панели (простая
// конкатенация, без разбора); значение экранировано.
//
// theme — тема в первом кадре (ADR-031 §3.3): до init панель рисовалась светлой,
// затем переключалась сообщением (вспышка в тёмной теме). Атрибут data-cc-theme
// встраивается в ВНУТРЕННИЙ <html> (шим обновляет его по init/theme); HTML панели
// не меняется.
//
// shimJs/shimCss — содержимое plugin-kit.js / theme.css (bridge.ts подставляет
// ?raw-импорты; e2e передаёт файлы через fs.readFileSync).
//
// Оболочка — ВНЕШНИЙ документ: html/body на 100% без полей и прокрутки, внутренний
// <iframe srcdoc> растянут на 100%×100% (display:block, border:0). Без этих стилей
// внутренний iframe жил в дефолтных 300×150: контент панели не растягивался и
// прокручивался сам. CSP не меняем: style-src 'unsafe-inline' уже в PANEL_CSP.
const SHELL_STYLE =
  'html,body{margin:0;height:100%;overflow:hidden}' +
  ' iframe{display:block;border:0;width:100%;height:100%}';

export function buildPanelSrcdoc(
  panelHtml: string,
  theme: 'light' | 'dark' = 'light',
  shimJs: string = '',
  shimCss: string = '',
): string {
  const themeAttr = theme === 'dark' ? ' data-cc-theme="dark"' : '';
  const inner =
    '<!doctype html><html' + themeAttr + '><head>' +
    '<meta http-equiv="Content-Security-Policy" content="' + PANEL_CSP + '">' +
    '<style>' + shimCss + '</style>' +
    '<script>' + shimJs + '</script>' +
    '</head><body>' + panelHtml + '</body></html>';
  return (
    '<!doctype html><html><head>' +
    '<meta http-equiv="Content-Security-Policy" content="' + PANEL_CSP + '">' +
    '<style>' + SHELL_STYLE + '</style>' +
    '</head><body>' +
    '<iframe sandbox="allow-scripts" srcdoc="' + escapeSrcdoc(inner) + '"></iframe>' +
    '</body></html>'
  );
}
