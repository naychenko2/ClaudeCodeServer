namespace ClaudeHomeServer.Services.TestRuns;

// Скрипт замера интерфейса для headless Playwright из node_modules проекта (`node --eval`, каталог
// процесса — каталог с пакетом). Только ЧИТАЕТ страницу: измеряет и печатает одну строку
// `UIPROBE-RESULT {json}` с сырыми числами; пороги и сборку итога применяет UiProbeReport на
// стороне сервера. Адрес, маршрут и ширины едут JSON-аргументом, учётка — переменными окружения
// (не в командной строке процесса).
internal static class UiProbeScript
{
    public const string ResultMarker = "UIPROBE-RESULT ";

    // Токен входа отдельной строкой: сервер вырезает её в первом же обработчике строк, до любого
    // накопления вывода (хвост ошибок, итог), поэтому в итоговый JSON и в хвост отказа он не попадает
    public const string TokenMarker = "UIPROBE-TOKEN ";

    // Окно устойчивости, мс: картина считается устойчивой, если не менялась столько подряд. Дольше типичной
    // паузы между ступенями догрузки SPA (1–2 с); ступень с паузой больше окна поймать нельзя в принципе
    public const int StableWindowMs = 3000;

    // Потолок ожидания устойчивой картины на ширину, мс: не устоялась за него — отказ, а не недозамер.
    // Вмещает окно с запасом на несколько ступеней догрузки; на потолок 9 мин всего вызова (3 ширины) не влияет
    public const int SettleTimeoutMs = 15000;

    // Пакеты, из которых loadChromium берёт chromium (в том же порядке): дерево обязано нести хотя бы один
    public static readonly string[] Packages = ["playwright", "@playwright/test", "playwright-core"];

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
          const truncated = [];
          const put = (list, name, item) => { if (list.length < args.max) list.push(item); else if (!truncated.includes(name)) truncated.push(name); };
          for (const el of all) {
            const r = el.getBoundingClientRect();
            if (!visible(el, r)) continue;
            const native = el.matches('button, a[href], input, select, textarea, [role=button]');
            if (el.matches(interactiveSel) && !el.disabled && (native || el.getAttribute('tabindex') !== '-1')) {
              put(interactive, 'interactive', { s: sel(el), w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, named: nameOf(el) });
            }
            const cs = getComputedStyle(el);
            const overX = el.scrollWidth > el.clientWidth + 1, overY = el.scrollHeight > el.clientHeight + 1;
            const hides = (v) => v === 'hidden' || v === 'clip';
            if (overX && !hides(cs.overflowX) && el.clientWidth > 0)
              put(containers, 'containers', { s: sel(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth, overflowX: cs.overflowX });
            if ((overX && hides(cs.overflowX) || overY && hides(cs.overflowY)) && (el.textContent || '').trim())
              put(clipped, 'clipped', { s: sel(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight });
          }
          const de = document.documentElement;
          // Видимые интерактивные элементы ВСЕЙ страницы (не зоны selector): ноль — приложение не отрисовалось.
          // Этим же признаком (visible) ждёт устойчивости settle(): ожидание и подсчёт не расходятся
          let pageInteractive = 0;
          for (const el of document.body ? document.body.querySelectorAll(interactiveSel) : []) if (visible(el, el.getBoundingClientRect())) pageInteractive++;
          // Экран входа (LoginPage): видимое поле текущего пароля. Токен не применился — приложение показывает его
          const loginForm = [...document.querySelectorAll('input[type=password][autocomplete=current-password]')]
            .some((el) => visible(el, el.getBoundingClientRect()));
          return {
            interactive, containers, clipped, truncated, pageInteractive, loginForm,
            doc: {
              scrollWidth: Math.max(de.scrollWidth, document.body ? document.body.scrollWidth : 0), clientWidth: de.clientWidth,
              scrollHeight: Math.max(de.scrollHeight, document.body ? document.body.scrollHeight : 0),
            },
          };
        }

        // SPA дорисовывается ступенями, и между ступенями бывают паузы: меряем, пока картина не простоит неизменной
        // stableMs подряд. В картину входит всё, по чему выдаются находки: число видимых интерактивных элементов,
        // их размеры и признак «именован», величины переполнения и обрезки, размеры документа, экран входа. Ноль элементов за
        // устойчивость не считается до потолка: пустой первый кадр — не итог. Не устоялась за потолок —
        // settled=false, и сервер откажет, а не отдаст заниженные числа. Предел метода: ступень с паузой дольше
        // stableMs не поймать
        async function settle(page, args, timeoutMs, stableMs) {
          const deadline = Date.now() + timeoutMs;
          let prev = null, since = Date.now();
          for (;;) {
            const m = await page.evaluate(measure, args);
            // Размер и признак «именован» по каждому интерактивному, величины (не только числа) переполнения и обрезки
            const sig = [m.pageInteractive, m.interactive.map((i) => i.w + 'x' + i.h + (i.named ? 'n' : 'u')).join(','),
              m.containers.map((c) => c.scrollWidth + '/' + c.clientWidth).join(','),
              m.clipped.map((c) => c.scrollWidth + '/' + c.clientWidth + '/' + c.scrollHeight + '/' + c.clientHeight).join(','),
              m.truncated.join(','), m.doc.scrollWidth, m.doc.scrollHeight, m.loginForm].join('|');
            const now = Date.now();
            if (sig !== prev) { prev = sig; since = now; }
            const timeUp = now >= deadline;
            if (now - since >= stableMs && (m.pageInteractive > 0 || timeUp)) { m.settled = true; return m; }
            if (timeUp) { m.settled = false; return m; }
            await page.waitForTimeout(250);
          }
        }

        (async () => {
          const out = { reach: 'ok', auth: 'skipped', viewports: [] };
          const chromium = loadChromium();
          const origin = new URL(input.url).origin;
          // Стенд виден из среды процесса? Иначе каждый viewport упрётся в 30-секундный таймаут навигации
          try { await fetch(origin, { signal: AbortSignal.timeout(5000) }); }
          catch (e) { out.reach = 'failed:' + String((e.cause && e.cause.code) || e.message || e).slice(0, 80); console.log('UIPROBE-RESULT ' + JSON.stringify(out)); return; }
          const browser = await chromium.launch({ headless: true });
          try {
            // Токен из кэша сервера (UIPROBE_TOKEN) — без повторного входа: лимит входа 10/мин на IP
            let token = process.env.UIPROBE_TOKEN || null;
            if (token) out.auth = 'cached';
            else if (process.env.UIPROBE_USER) {
              const ctx0 = await browser.newContext();
              try {
                const res = await ctx0.request.post(origin + '/api/auth/login', {
                  data: { username: process.env.UIPROBE_USER, password: process.env.UIPROBE_PASS || '' }, timeout: 10000 });
                if (res.ok()) { token = (await res.json()).token || null; out.auth = token ? 'ok' : 'no-token'; if (token) console.log('UIPROBE-TOKEN ' + token); }
                else out.auth = 'failed:' + res.status();
              } catch (e) { out.auth = 'failed:' + String(e.message || e).slice(0, 80); }
              await ctx0.close();
              // Без входа страница — экран логина: замер дал бы ложное «находок нет»
              if (out.auth !== 'ok') { console.log('UIPROBE-RESULT ' + JSON.stringify(out)); return; }
            }
            for (const width of input.viewports) {
              const ctx = await browser.newContext({ viewport: { width, height: input.height } });
              if (token) await ctx.addInitScript((t) => { localStorage.setItem('cc_token', t); }, token);
              const page = await ctx.newPage();
              const vp = { width, status: null, console: [], failed: [], pageTruncated: [] };
              const keep = (list, name, item) => { if (list.length < MAX) list.push(item); else if (!vp.pageTruncated.includes(name)) vp.pageTruncated.push(name); };
              page.on('console', (m) => { if (m.type() === 'error') keep(vp.console, 'console', { type: 'error', text: m.text().slice(0, 300) }); });
              page.on('pageerror', (e) => keep(vp.console, 'console', { type: 'pageerror', text: String(e.message || e).slice(0, 300) }));
              page.on('requestfailed', (r) => keep(vp.failed, 'failed', { url: r.url().slice(0, 200), failure: (r.failure() || {}).errorText || 'failed' }));
              page.on('response', (r) => { if (r.status() >= 400) keep(vp.failed, 'failed', { url: r.url().slice(0, 200), status: r.status() }); });
              try {
                const res = await page.goto(input.url, { waitUntil: 'load', timeout: 30000 });
                vp.status = res ? res.status() : null;
                vp.finalUrl = page.url();
                await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});
                await page.waitForTimeout(300);
                Object.assign(vp, await settle(page, { selector: input.selector, max: MAX }, input.settleMs, input.stableMs));
                vp.finalUrl = page.url();
              } catch (e) { vp.error = String(e.message || e).slice(0, 200); }
              out.viewports.push(vp);
              await ctx.close();
            }
          } finally { await browser.close(); }
          console.log('UIPROBE-RESULT ' + JSON.stringify(out));
        })().catch((e) => { console.error('UIPROBE-FAIL ' + String(e && e.message || e)); process.exit(1); });
        """;
}
