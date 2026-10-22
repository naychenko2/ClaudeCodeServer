#!/usr/bin/env node
// Автопроверка панели «Погода» (шаг C3 плана «Погода»).
//
// Прогоняет шесть сценариев Playwright'ом против хост-эмулятора
// (tools/plugins/dev-host/) на фикстурах из tools/plugins/dev-host/fixtures/.
//
// Запуск из корня репозитория:
//   node tools/plugins/weather/check.mjs
//
// Скрипт сам поднимает статический сервер над корнем репозитория (эмулятор
// раздаёт панель, кит и токены темы по HTTP), запускает chromium из
// frontend/node_modules и закрывает всё за собой. Сеть наружу не ходит:
// ответы панели — фикстуры.
//
// Сценарии:
//   1. Главный экран: 7 строк недели, герой с температурой и состоянием.
//   2. «Сегодня 22 окт» при timezoneId America/Los_Angeles и Asia/Tokyo —
//      дата по фикстуре, а не по поясу машины.
//   3. Нет горизонтальной прокрутки на ширинах 320 и 390.
//   4. denied (нет права на прогноз) и status400 — каждый даёт свой кадр.
//   5. Поиск «Ярц» — сортировка по населению (Ярцево Смоленское первым).
//   6. Пустой поиск — пустое состояние.

import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, extname, sep } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..', '..'); // tools/plugins/weather → корень репозитория
const require = createRequire(join(ROOT, 'frontend', 'package.json'));
const { chromium } = require('playwright');

const PANEL = 'tools/plugins/weather/panel/index.html';
const DEV_HOST = 'tools/plugins/dev-host/index.html';
const FX = 'tools/plugins/dev-host/fixtures/';
const SHOTS = join(ROOT, '.cc-attachments', 'weather-c3');

const GRANT_ALL = 'api.open-meteo.com,geocoding-api.open-meteo.com';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
};

// ---------- Статический сервер над корнем репозитория ----------

async function startServer() {
  const server = createServer(async (req, res) => {
    try {
      const rel = decodeURIComponent(new URL(req.url, 'http://x').pathname).replace(/^\/+/, '');
      const file = join(ROOT, rel);
      if (!file.startsWith(ROOT + sep)) { res.writeHead(403).end(); return; }
      const body = await readFile(file);
      res.writeHead(200, { 'content-type': MIME[extname(file)] || 'application/octet-stream' });
      res.end(body);
    } catch (e) {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      res.end('нет такого файла: ' + req.url);
    }
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const { port } = server.address();
  return { server, base: 'http://127.0.0.1:' + port };
}

// ---------- Помощники проверки ----------

const log = (...a) => console.log(...a);

function check(name, ok, detail) {
  return { name, ok: !!ok, detail: detail === undefined ? '' : String(detail) };
}

function assertEq(checks, name, actual, expected) {
  const ok = String(actual) === String(expected);
  checks.push(check(name, ok, ok ? actual : 'получено «' + actual + '», ожидалось «' + expected + '»'));
}

function assertTrue(checks, name, ok, detail) {
  checks.push(check(name, !!ok, detail));
}

// Метка сценария и его результат.
const results = [];
async function scenario(title, fn) {
  const checks = [];
  let error = null;
  try {
    await fn(checks);
  } catch (e) {
    error = e;
  }
  results.push({ title, checks, error });
  const failed = checks.filter((c) => !c.ok).length;
  const mark = error || failed ? '✗' : '✓';
  log(mark + ' ' + title + (error ? ' — исключение: ' + error.message : ''));
  for (const c of checks) {
    if (!c.ok) log('    ✗ ' + c.name + ': ' + c.detail);
    else if (process.env.WX_VERBOSE) log('    · ' + c.name + ': ' + c.detail);
  }
}

// ---------- Рамка с панелью ----------

// Панель смонтирована во внутреннем srcdoc-фрейме эмулятора: ищем рамку с #wx.
async function panelFrame(page) {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    for (const f of page.frames()) {
      if (f === page.mainFrame()) continue;
      try {
        if (await f.evaluate(() => !!document.getElementById('wx'))) return f;
      } catch (e) { /* рамка ещё не готова — пробуем следующую */ }
    }
    await page.waitForTimeout(150);
  }
  throw new Error('панель не смонтировалась в эмуляторе');
}

async function openEmulator(browser, base, params, opts = {}) {
  const context = await browser.newContext({
    timezoneId: opts.timezoneId || 'UTC',
    viewport: { width: 1280, height: 900 },
  });
  const page = await context.newPage();
  const q = new URLSearchParams({ panel: PANEL, ...params });
  await page.goto(base + '/' + DEV_HOST + '?' + q.toString(), { waitUntil: 'load' });
  const frame = await panelFrame(page);
  return { context, page, frame };
}

// Текст элемента (innerText, чтобы не ловить скрытое).
async function textOf(frame, selector) {
  return frame.locator(selector).first().innerText();
}

async function shot(page, name) {
  try {
    await mkdir(SHOTS, { recursive: true });
    await page.screenshot({ path: join(SHOTS, name + '.png'), fullPage: true });
  } catch (e) { /* скриншот — не критерий, тихо пропускаем */ }
}

// ---------- Сценарии ----------

async function scenarioMain(browser, base) {
  await scenario('1. Главный экран: неделя, герой, состояние', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: FX + 'forecast-yaroslavl.json',
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });

      const rows = frame.locator('.week .day');
      assertEq(checks, 'строк недели', await rows.count(), 7);

      const firstDn = await textOf(frame, '.week .day:first-child .dn');
      assertTrue(checks, 'первая строка — «Сегодня»', firstDn.includes('Сегодня'), firstDn.replace(/\n/g, ' '));

      const dates = await frame.locator('.week .day small').allInnerTexts();
      assertEq(checks, 'даты недели', dates.map((d) => d.trim()).join(' '), '22 окт 23 окт 24 окт 25 окт 26 окт 27 окт 28 окт');

      const temp = (await textOf(frame, '.hero .temp')).trim();
      assertEq(checks, 'температура героя', temp, '+10°');
      const cond = (await textOf(frame, '.hero .cond')).trim();
      assertEq(checks, 'состояние героя', cond, 'Преимущественно ясно');
      const when = (await textOf(frame, '.hero .when')).trim();
      assertEq(checks, 'время «сейчас»', when, 'Сейчас · 15:45');
      const hl = (await textOf(frame, '.hero .hl')).trim();
      assertTrue(checks, 'днём/ночью в герое', hl.includes('Днём') && hl.includes('ночью'), hl);

      assertTrue(checks, 'три плитки героя', (await frame.locator('.hero .metric').count()) === 3);
    } catch (e) {
      await shot(page, 'scenario-1-main');
      throw e;
    } finally {
      await context.close();
    }
  });
}

async function scenarioTimezones(browser, base) {
  await scenario('2. «Сегодня 22 окт» в поясах America/Los_Angeles и Asia/Tokyo', async (checks) => {
    for (const tz of ['America/Los_Angeles', 'Asia/Tokyo']) {
      const { context, page, frame } = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fixtures: FX + 'forecast-yaroslavl.json',
      }, { timezoneId: tz });
      try {
        await frame.locator('.week .day').first().waitFor({ timeout: 15000 });
        const date = (await textOf(frame, '.week .day:first-child small')).trim();
        const dn = (await textOf(frame, '.week .day:first-child .dn')).replace(/\n/g, ' ').trim();
        assertEq(checks, tz + ': дата первой строки', date, '22 окт');
        assertTrue(checks, tz + ': подпись «Сегодня»', dn.includes('Сегодня'), dn);
      } catch (e) {
        await shot(page, 'scenario-2-' + tz.replace(/\//g, '-'));
        throw e;
      } finally {
        await context.close();
      }
    }
  });
}

async function scenarioNoOverflow(browser, base) {
  await scenario('3. Нет горизонтальной прокрутки на ширинах 320 и 390', async (checks) => {
    for (const width of [320, 390]) {
      const { context, page, frame } = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fixtures: FX + 'forecast-yaroslavl.json',
        width: String(width),
      });
      try {
        // Переключатель width= эмулятора задаёт ширину карточки, но сам кадр с
        // панелью остаётся 300 px (iframe без CSS-ширины). Ширину панели задаём
        // явно — иначе проверялась бы одна и та же ширина 300.
        await page.evaluate((w) => { document.getElementById('frame').style.width = w + 'px'; }, width);
        const deadline = Date.now() + 5000;
        let cw = 0;
        while (Date.now() < deadline) {
          cw = await frame.evaluate(() => document.documentElement.clientWidth);
          if (Math.abs(cw - width) <= 1) break;
          await page.waitForTimeout(100);
        }
        assertTrue(checks, 'ширина панели ' + width + ' px', Math.abs(cw - width) <= 1, 'clientWidth=' + cw);

        await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
        const m = await frame.evaluate(() => ({
          sw: document.documentElement.scrollWidth,
          cw: document.documentElement.clientWidth,
          bsw: document.body.scrollWidth,
        }));
        assertTrue(
          checks,
          'ширина ' + width + ': нет горизонтальной прокрутки',
          m.sw <= m.cw + 1 && m.bsw <= m.cw + 1,
          'scrollWidth=' + m.sw + ' clientWidth=' + m.cw + ' bodyScrollWidth=' + m.bsw
        );
      } catch (e) {
        await shot(page, 'scenario-3-' + width);
        throw e;
      } finally {
        await context.close();
      }
    }
  });
}

async function scenarioErrors(browser, base) {
  await scenario('4a. denied (нет права на прогноз) — свой кадр', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, { grant: '' });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок отказа', title, 'Нужен доступ к сервису погоды');
      const badge = await frame.locator('.cc-badge').first().innerText().catch(() => '');
      assertEq(checks, 'бейдж права', badge.trim(), 'net:api.open-meteo.com');
    } catch (e) {
      await shot(page, 'scenario-4a-denied');
      throw e;
    } finally {
      await context.close();
    }
  });

  await scenario('4b. status400 — свой кадр', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fail: 'status400',
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок сбоя', title, 'Сервис погоды не ответил');
      const btn = await frame.locator('#retry').innerText().catch(() => '');
      assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
    } catch (e) {
      await shot(page, 'scenario-4b-status400');
      throw e;
    } finally {
      await context.close();
    }
  });
}

async function scenarioSearchSort(browser, base) {
  await scenario('5. Поиск «Ярц» — сортировка по населению', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      // Порядок вызовов: прогноз главного экрана → геокодер → пакетные температуры.
      fixtures: [FX + 'forecast-yaroslavl.json', FX + 'geocode-arts.json', FX + 'forecast-batch.json'].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });

      const count = await frame.locator('.city').count();
      assertTrue(checks, 'строк найдено не больше 6', count > 0 && count <= 6, 'строк: ' + count);
      const first = (await textOf(frame, '.city:first-child')).replace(/\n/g, ' ').trim();
      assertTrue(checks, 'первым — Ярцево', first.includes('Ярцево'), first);
      assertTrue(checks, 'Ярцево Смоленское (по населению)', first.includes('Смоленская обл.'), first);
    } catch (e) {
      await shot(page, 'scenario-5-search');
      throw e;
    } finally {
      await context.close();
    }
  });
}

async function scenarioSearchEmpty(browser, base) {
  await scenario('6. Пустой поиск — пустое состояние', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [FX + 'forecast-yaroslavl.json', FX + 'geocode-empty.json'].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Xyzqwe');
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'пустое состояние', title, 'Город не найден');
      assertEq(checks, 'список результатов пуст', await frame.locator('.city').count(), 0);
    } catch (e) {
      await shot(page, 'scenario-6-empty');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// ---------- Запуск ----------

const { server, base } = await startServer();
const browser = await chromium.launch();
try {
  await scenarioMain(browser, base);
  await scenarioTimezones(browser, base);
  await scenarioNoOverflow(browser, base);
  await scenarioErrors(browser, base);
  await scenarioSearchSort(browser, base);
  await scenarioSearchEmpty(browser, base);
} finally {
  await browser.close();
  server.close();
}

const failedScenarios = results.filter((r) => r.error || r.checks.some((c) => !c.ok));
const total = results.reduce((n, r) => n + r.checks.length, 0);
log('');
log('итого: сценариев ' + results.length + ', проверок ' + total + ', красных ' + failedScenarios.length);
if (failedScenarios.length) {
  for (const r of failedScenarios) log('  ✗ ' + r.title + (r.error ? ' — ' + r.error.message : ''));
  process.exit(1);
}
log('все сценарии зелёные');
