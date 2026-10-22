namespace ClaudeHomeServer.Services.TestRuns;

// Скрипт замера интерфейса для headless Playwright из node_modules проекта (`node --eval`, каталог
// процесса — каталог с пакетом). Только ЧИТАЕТ страницу: измеряет и печатает одну строку
// `UIPROBE-RESULT {json}` с сырыми числами; пороги и сборку итога применяет UiProbeReport на
// стороне сервера. Адрес, маршрут и ширины едут JSON-аргументом, учётка — переменными окружения
// (не в командной строке процесса).
internal static class UiProbeScript
{
    public const string ResultMarker = "UIPROBE-RESULT ";

    // Потолок записей в каждом списке: страница с тысячами элементов не должна раздувать вывод
    public const int MaxItems = 400;

    public const string Source = """
        const { createRequire } = require('node:module');
        const path = require('node:path');
        const req = createRequire(path.join(process.cwd(), 'package.json'));
        const input = JSON.parse(process.argv[1]);
        const MAX = input.max;

        function loadChromium() {
          for (const name of ['playwright', '@playwright/test', 'playwright-core']) {
            try { const m = req(name); if (m.chromium) return m.chromium; } catch { /* следующий пакет */ }
          }
          throw new Error('playwright не найден в node_modules каталога прогона');
        }

        // Выполняется в странице: сырые измерения без порогов
        function measure(args) {
          const sel = (el) => {
            const parts = [];
            let cur = el;
            for (let i = 0; cur && cur.nodeType === 1 && i < 4; i++, cur = cur.parentElement) {
              let p = cur.tagName.toLowerCase();
              if (cur.id) { parts.unshift(p + '#' + CSS.escape(cur.id)); break; }
              const cls = [...cur.classList].slice(0, 2).map((c) => '.' + CSS.escape(c)).join('');
              p += cls;
              const sib = cur.parentElement ? [...cur.parentElement.children].filter((c) => c.tagName === cur.tagName) : [];
              if (sib.length > 1) p += ':nth-of-type(' + (sib.indexOf(cur) + 1) + ')';
              parts.unshift(p);
            }
            return parts.join(' > ');
          };
          const visible = (el, r) => {
            if (r.width === 0 && r.height === 0) return false;
            const cs = getComputedStyle(el);
            return cs.visibility !== 'hidden' && cs.display !== 'none';
          };
          const nameOf = (el) => {
            const aria = el.getAttribute('aria-label');
            if (aria && aria.trim()) return true;
            const by = el.getAttribute('aria-labelledby');
            if (by && by.split(/\s+/).some((id) => { const t = document.getElementById(id); return t && t.textContent.trim(); })) return true;
            if ((el.getAttribute('title') || '').trim()) return true;
            if ((el.getAttribute('alt') || '').trim()) return true;
            if (el.labels && [...el.labels].some((l) => l.textContent.trim())) return true;
            if (el.tagName === 'INPUT' && ['button', 'submit', 'reset'].includes(el.type) && (el.value || '').trim()) return true;
            if (el.tagName === 'INPUT' && el.type === 'image' && (el.getAttribute('alt') || '').trim()) return true;
            if (el.textContent && el.textContent.trim()) return true;
            const img = el.querySelector('img[alt], svg title, [aria-label]');
            return !!(img && ((img.getAttribute && (img.getAttribute('alt') || img.getAttribute('aria-label') || '').trim()) || (img.textContent || '').trim()));
          };
          const roots = args.selector ? [...document.querySelectorAll(args.selector)] : [document.body];
          const all = new Set();
          for (const root of roots) {
            if (root !== document.body) all.add(root);
            for (const el of root.querySelectorAll('*')) all.add(el);
          }
          const interactiveSel = 'button, a[href], input:not([type=hidden]), select, textarea, [role=button], [tabindex]';
          const interactive = [], containers = [], clipped = [];
          for (const el of all) {
            const r = el.getBoundingClientRect();
            if (!visible(el, r)) continue;
            const native = el.matches('button, a[href], input, select, textarea, [role=button]');
            if (el.matches(interactiveSel) && !el.disabled && (native || el.getAttribute('tabindex') !== '-1')) {
              if (interactive.length < args.max) interactive.push({ s: sel(el), w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, named: nameOf(el) });
            }
            const cs = getComputedStyle(el);
            const overX = el.scrollWidth > el.clientWidth + 1, overY = el.scrollHeight > el.clientHeight + 1;
            const hides = (v) => v === 'hidden' || v === 'clip';
            if (overX && !hides(cs.overflowX) && el.clientWidth > 0 && containers.length < args.max)
              containers.push({ s: sel(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth, overflowX: cs.overflowX });
            if ((overX && hides(cs.overflowX) || overY && hides(cs.overflowY)) && (el.textContent || '').trim() && clipped.length < args.max)
              clipped.push({ s: sel(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight });
          }
          const de = document.documentElement;
          return {
            interactive, containers, clipped,
            doc: { scrollWidth: Math.max(de.scrollWidth, document.body ? document.body.scrollWidth : 0), clientWidth: de.clientWidth },
          };
        }

        (async () => {
          const out = { auth: 'skipped', viewports: [] };
          const chromium = loadChromium();
          const browser = await chromium.launch({ headless: true });
          try {
            const origin = new URL(input.url).origin;
            let token = null;
            if (process.env.UIPROBE_USER) {
              const ctx0 = await browser.newContext();
              try {
                const res = await ctx0.request.post(origin + '/api/auth/login', {
                  data: { username: process.env.UIPROBE_USER, password: process.env.UIPROBE_PASS || '' }, timeout: 10000 });
                if (res.ok()) { token = (await res.json()).token || null; out.auth = token ? 'ok' : 'no-token'; }
                else out.auth = 'failed:' + res.status();
              } catch (e) { out.auth = 'failed:' + String(e.message || e).slice(0, 80); }
              await ctx0.close();
            }
            for (const width of input.viewports) {
              const ctx = await browser.newContext({ viewport: { width, height: input.height } });
              if (token) await ctx.addInitScript((t) => { localStorage.setItem('cc_token', t); }, token);
              const page = await ctx.newPage();
              const vp = { width, status: null, console: [], failed: [] };
              page.on('console', (m) => { if (m.type() === 'error' && vp.console.length < MAX) vp.console.push({ type: 'error', text: m.text().slice(0, 300) }); });
              page.on('pageerror', (e) => { if (vp.console.length < MAX) vp.console.push({ type: 'pageerror', text: String(e.message || e).slice(0, 300) }); });
              page.on('requestfailed', (r) => { if (vp.failed.length < MAX) vp.failed.push({ url: r.url().slice(0, 200), failure: (r.failure() || {}).errorText || 'failed' }); });
              page.on('response', (r) => { if (r.status() >= 400 && vp.failed.length < MAX) vp.failed.push({ url: r.url().slice(0, 200), status: r.status() }); });
              try {
                const res = await page.goto(input.url, { waitUntil: 'load', timeout: 30000 });
                vp.status = res ? res.status() : null;
                await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});
                await page.waitForTimeout(300);
                Object.assign(vp, await page.evaluate(measure, { selector: input.selector, max: MAX }));
              } catch (e) { vp.error = String(e.message || e).slice(0, 200); }
              out.viewports.push(vp);
              await ctx.close();
            }
          } finally { await browser.close(); }
          console.log('UIPROBE-RESULT ' + JSON.stringify(out));
        })().catch((e) => { console.error('UIPROBE-FAIL ' + String(e && e.message || e)); process.exit(1); });
        """;
}
