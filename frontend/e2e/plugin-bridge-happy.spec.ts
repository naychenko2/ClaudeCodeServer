// Мост панели плагина: «счастливый» сценарий на РЕАЛЬНОЙ оболочке и РЕАЛЬНОМ шиме.
//
// Направление «панель → хост» (ADR-031 §3.6) не покрывалось: bridge.test.ts подменял
// окно панели заглушкой, поэтому адресат шима (parent vs window.top) не проверялся.
// Здесь — реальный plugin-kit.js (fs) в реальной двухуровневой оболочке (top → оболочка
// → панель), как в PluginFrame: ready доходит до хоста с e.source === frames[0] и
// origin === 'null', хост шлёт init, панель отвечает call → result, resize меняет высоту.
//
// Мутация «вернуть parent.postMessage» (шаг 1) красит: ready уходит в оболочку,
// хост его не видит. Запуск: npx playwright test plugin-bridge-happy (Playwright ≥ 1.63).
import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

// Те же файлы, что встраивает buildPanelSrcdoc (frontend/src/features/plugins/bridge.ts).
// cwd — корень frontend/ (отсюда запускается playwright), public/ — рядом.
const KIT = path.join(process.cwd(), 'public', 'plugin-kit');
const shimJs = fs.readFileSync(path.join(KIT, 'plugin-kit.js'), 'utf8');
const shimCss = fs.readFileSync(path.join(KIT, 'theme.css'), 'utf8');

// CSP — та же константа PANEL_CSP из bridge.ts (ADR-031 §3.1/§3.6).
const PANEL_CSP =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
  "img-src data: blob:; connect-src 'none'; form-action 'none'; " +
  "base-uri 'none'; frame-src 'none'";

// buildPanelSrcdoc (bridge.ts): оболочка (CSP + ровно один iframe) + внутренний
// документ (CSP + шим + html панели). Экранирование srcdoc: & → &amp;, " → &quot;.
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
function buildPanelSrcdoc(panelHtml: string): string {
  const inner =
    '<!doctype html><html><head>' +
    '<meta http-equiv="Content-Security-Policy" content="' + PANEL_CSP + '">' +
    '<style>' + shimCss + '</style>' +
    '<script>' + shimJs + '</script>' +
    '</head><body>' + panelHtml + '</body></html>';
  return (
    '<!doctype html><html><head>' +
    '<meta http-equiv="Content-Security-Policy" content="' + PANEL_CSP + '">' +
    '</head><body>' +
    '<iframe sandbox="allow-scripts" srcdoc="' + esc(inner) + '"></iframe>' +
    '</body></html>'
  );
}

// Панель (недоверенный код, но в тесте наша): по init делает «пустой» call,
// по result — resize(200). Флаги window.__panel читаем из фрейма панели.
const panelHtml =
  '<script>' +
  'window.__panel = { inited: false, gotResult: false, resultData: null, callErr: null };' +
  'ccsPlugin.on("init", function () {' +
  '  window.__panel.inited = true;' +
  '  ccsPlugin.call("notes.read", {}).then(function (r) {' +
  '    window.__panel.gotResult = true;' +
  '    window.__panel.resultData = r;' +
  '    ccsPlugin.resize(200);' +
  '  }, function (err) { window.__panel.callErr = err; });' +
  '});' +
  '</script><p>panel</p>';

// Хост-страница: outer iframe (оболочка) + слушатель message. Адресат — окно панели
// (frames[0] оболочки), не оболочка: e.source сверяем с ним, init/result шлём в него.
function hostHtml(): string {
  const shell = buildPanelSrcdoc(panelHtml);
  return (
    '<!doctype html><html><head><meta charset="utf-8"></head><body>' +
    '<iframe id="outer" style="width:900px;height:400px;border:0" srcdoc="' + esc(shell) + '"></iframe>' +
    '<script>' +
    'window.__host = { ready: 0, sourceOk: false, originOk: false, calls: [], resultSent: 0, resizes: [] };' +
    'window.addEventListener("message", function (e) {' +
    '  var outer = document.getElementById("outer");' +
    '  var panelWin = outer && outer.contentWindow ? outer.contentWindow.frames[0] : null;' +
    '  var d = e.data;' +
    '  if (!d || d.source !== "ccs-plugin" || d.v !== 1) return;' +
    '  if (e.source !== panelWin || e.origin !== "null") return;' +
    '  if (d.type === "ready") {' +
    '    window.__host.ready++;' +
    '    window.__host.sourceOk = (e.source === panelWin);' +
    '    window.__host.originOk = (e.origin === "null");' +
    '    panelWin.postMessage({ source: "ccs-host", v: 1, type: "init", plugin: { id: "a/b", version: "1.0.0" }, slot: "sidebar", context: { projectId: "p1" }, theme: "light", locale: "ru-RU", granted: ["notes:read"] }, "*");' +
    '  } else if (d.type === "call") {' +
    '    window.__host.calls.push(d);' +
    '    panelWin.postMessage({ source: "ccs-host", v: 1, type: "result", id: d.id, ok: true, data: { hello: "world" } }, "*");' +
    '    window.__host.resultSent++;' +
    '  } else if (d.type === "resize") {' +
    '    window.__host.resizes.push(d.height);' +
    '    outer.style.height = d.height + "px";' +
    '  }' +
    '});' +
    '</script>' +
    '</body></html>'
  );
}

test('мост: ready → init → call → result → resize (реальная оболочка, реальный шим)', async ({ page }) => {
  await page.setContent(hostHtml(), { waitUntil: 'load' });

  // Ждём, пока мост дойдёт до конца: ready принят, call дошёл, resize записан.
  await page.waitForFunction(
    () => {
      const h = (window as unknown as { __host?: { ready: number; calls: unknown[]; resizes: number[] } }).__host;
      return !!h && h.ready >= 1 && h.calls.length >= 1 && h.resizes.length >= 1;
    },
    { timeout: 10_000 },
  );

  const host = await page.evaluate(() => {
    const h = (window as unknown as { __host: { ready: number; sourceOk: boolean; originOk: boolean; calls: { op: string; id: string }[]; resultSent: number; resizes: number[] } }).__host;
    const outer = document.getElementById('outer') as HTMLIFrameElement;
    return { ...h, outerHeight: outer.style.height };
  });

  // ready дошёл до хоста: источник — окно панели (frames[0]), origin — 'null'.
  expect(host.ready, 'ready дошёл до хоста').toBeGreaterThanOrEqual(1);
  expect(host.sourceOk, 'e.source === outer.contentWindow.frames[0]').toBe(true);
  expect(host.originOk, 'origin === "null"').toBe(true);

  // call «пустого» вызова дошёл до хоста, хост ответил result.
  expect(host.calls.length, 'call дошёл до хоста').toBeGreaterThanOrEqual(1);
  expect(host.calls[0].op, 'op вызова').toBe('notes.read');
  expect(host.resultSent, 'хост ответил result').toBeGreaterThanOrEqual(1);

  // resize(200) изменил высоту фрейма.
  expect(host.resizes, 'resize(200) записан').toContain(200);
  expect(host.outerHeight, 'высота outer iframe обновлена').toBe('200px');

  // Панель получила init и result (round-trip): читаем флаги из фрейма панели.
  const panel = await (async () => {
    for (const f of page.frames()) {
      try {
        const p = await f.evaluate(() => (window as unknown as { __panel?: { inited: boolean; gotResult: boolean; resultData: unknown; callErr: unknown } }).__panel);
        if (p) return p;
      } catch { /* фрейм недоступен */ }
    }
    return null;
  })();
  expect(panel, 'фрейм панели найден').not.toBeNull();
  expect(panel!.inited, 'панель получила init').toBe(true);
  expect(panel!.callErr, 'call не упал').toBeNull();
  expect(panel!.gotResult, 'панель получила result').toBe(true);
  expect(panel!.resultData, 'result несёт данные').toEqual({ hello: 'world' });
});
