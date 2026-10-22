#!/usr/bin/env node
// Автопроверка панели «Погода» (шаг C3 плана «Погода»).
//
// Прогоняет сценарии Playwright'ом против хост-эмулятора
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
//   1. Главный экран: 7 строк недели, герой с температурой и состоянием;
//      подвал — «обновлено в» по времени города (R3).
//   2. «Сегодня 22 окт» при timezoneId America/Los_Angeles и Asia/Tokyo —
//      дата по фикстуре, а не по поясу машины.
//   3. Нет горизонтальной прокрутки на ширинах 320 и 390.
//   4. denied (нет права на прогноз), status400 и неполный ответ — каждый
//      даёт свой кадр; неполный ответ — прочерки, не выдуманная погода (R3).
//   5. Поиск «Ярц» — сортировка по населению (Ярцево Смоленское первым);
//      повторное открытие режима выбора — чистое (R3).
//   6. Пустой поиск — пустое состояние.
//   7. Гонка: два поиска подряд, пакет первого приходит позже результатов
//      второго — температуры только свои (R3, major 1).
//   8. Город в storage — объект (не двойная строка), и панель читает
//      запомненный город при повторном открытии (C3-д).
//   9. Подсказка «Город запоминается…» — только при выданном праве storage (C3-д).
//   10. Нет права на геокодер — человеческий текст и бейдж с ключом права,
//       главный экран работает (C3-д).
//   11. Сброс запроса ниже двух символов гасит предыдущий поиск (minor M3).
//   12. Выход из режима в пределах дебаунса гасит висящий таймер (minor M4).
//   13. Выданное позже право на геокодер оживляет поиск при повторном
//       открытии — липкая плашка отказа снимается (minor M5).
//   14. Хранилище отключено: эмулятор отвечает «unavailable», а не роняет
//       обработчик (minor M2, ревью R2-д).

import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
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

      // Подвал — «обновлено в» по времени города (R3): формат HH:MM, не «данные на».
      const foot = (await textOf(frame, '.foot')).trim();
      assertTrue(checks, 'подвал «обновлено в» с временем города',
        /^Данные: Open-Meteo · обновлено в \d{2}:\d{2}$/.test(foot), foot);

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

  await scenario('4b. Неполный ответ — прочерки, не выдуманная погода (R3)', async (checks) => {
    // Синтетическая фикстура в gitignore-папке: current без значений, daily без
    // температур — ровно тот вид неполного ответа, который раньше давал «ясно» и «NaN°».
    const incomplete = '.cc-attachments/weather-c3/forecast-incomplete.json';
    await mkdir(join(ROOT, '.cc-attachments', 'weather-c3'), { recursive: true });
    await writeFile(join(ROOT, incomplete), JSON.stringify({
      latitude: 57.63, longitude: 39.87, utc_offset_seconds: 10800, timezone: 'Europe/Moscow',
      current: { time: '2026-10-22T15:45', interval: 900 },
      daily: { time: ['2026-10-22', '2026-10-23'], weather_code: [2, 3] },
    }));
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: incomplete,
    });
    try {
      await frame.locator('.hero .cond').waitFor({ timeout: 15000 });
      assertEq(checks, 'состояние героя — прочерк', (await textOf(frame, '.hero .cond')).trim(), '—');
      assertEq(checks, 'температура героя — прочерк', (await textOf(frame, '.hero .temp')).trim(), '—');
      // Строки недели без температур: min/max — прочерки, полосы диапазона нет.
      // Выдуманного «0°» в строке быть не должно — ровно то, что раньше пропускала
      // слабая проверка (minor M1, M6).
      const rows = await frame.locator('.week .day').all();
      assertEq(checks, 'строк недели', rows.length, 2);
      for (let i = 0; i < rows.length; i++) {
        const lo = (await rows[i].locator('.lo').innerText()).trim();
        const hi = (await rows[i].locator('.hi').innerText()).trim();
        assertEq(checks, 'строка ' + (i + 1) + ' — min и max прочерки', lo + ' ' + hi, '— —');
      }
      assertEq(checks, 'полос диапазона без значений', await frame.locator('.range i').count(), 0);
      const panelText = await frame.locator('.wx').innerText();
      assertTrue(checks, 'нет выдуманного «0°» нигде', !panelText.includes('0°'), 'в тексте панели: ' + panelText.replace(/\n/g, ' '));
      assertTrue(checks, 'нет NaN° нигде', !panelText.includes('NaN'), 'в тексте панели: ' + panelText.replace(/\n/g, ' '));
      assertTrue(checks, 'нет выдуманного «Ясно»/«Облачно»', !panelText.includes('Ясно') && !panelText.includes('Облачно'),
        'в тексте панели: ' + panelText.replace(/\n/g, ' '));
    } catch (e) {
      await shot(page, 'scenario-4b-incomplete');
      throw e;
    } finally {
      await context.close();
    }
  });

  await scenario('4c. status400 — свой кадр (тело валидное по форме)', async (checks) => {
    // Тело ответа — настоящая фикстура прогноза со всеми полями. Смысл: привязать
    // сценарий к проверке status (минор C3-д). С пустым телом панель показала бы
    // ошибку и без проверки status (невалидная форма), поэтому мутация «не смотреть
    // status» проходила бы зелёной. С валидным телом без проверки status панель
    // построила бы главный экран из ответа 400 — сценарий краснеет.
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fail: 'status400',
      fixtures: FX + 'forecast-yaroslavl.json',
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок сбоя', title, 'Сервис погоды не ответил');
      const btn = await frame.locator('#retry').innerText().catch(() => '');
      assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
    } catch (e) {
      await shot(page, 'scenario-4c-status400');
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

      // Повторное открытие режима — чистое: пустое поле и без старых результатов (R3).
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      assertEq(checks, 'поле поиска пустое', await frame.locator('#q').inputValue(), '');
      assertEq(checks, 'старых результатов нет', await frame.locator('.city').count(), 0);
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

async function scenarioRace(browser, base) {
  await scenario('7. Гонка: два поиска подряд — старый пакет не красит новый список (R3)', async (checks) => {
    const GAP = 2000; // пауза между поисками (больше дебаунса)
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      // Фикстуры привязаны к запросу через «|match», а не к порядку вызовов:
      // первый геокодер быстрый — пакет первой волны реально запрашивается; его
      // задержка 6000 мс — он приходит позже, чем новый список нарисован
      // (t0+2400). Это настоящая гонка, а не подавленная guard'ом (minor M2).
      // Match'и берутся без запятых (параметр разделяется запятыми) и уникальные:
      // «apparent_temperature» есть только в URL главного экрана, координаты
      // пакетов различимы между собой.
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json|55.06491|6000',
        FX + 'geocode-yaroslavl.json|name=%D0%AF%D1%80%D0%BE%D1%81%D0%BB%D0%B0%D0%B2',
        FX + 'forecast-batch.json|58.58259',
      ].join(','),
    });
    try {
      // Ждём настоящего главного экрана, не скелетона.
      await frame.locator('.hero .temp').waitFor({ timeout: 20000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await page.waitForTimeout(GAP);
      await frame.locator('#q').fill('Ярослав');
      // Событийное ожидание (minor M2): ждём не стенку времени, а доставку
      // старого пакета — «←» с координатами первой волны в логе эмулятора.
      // Проверка сразу после него: запас — секунды, а не 500 мс джиттера.
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('←') && p.textContent.includes('55.06491'));
      }, { timeout: 20000 });
      // Старый пакет обработан хостом: если guard'а нет, температуры первой
      // волны (+30°, +31°…) появились бы на строках второго списка. Правильный
      // ответ — только свои значения второго поиска.
      const temps = await frame.locator('.city .t').allInnerTexts();
      assertEq(checks, 'температуры нового списка — только свои', temps.join(' '), '+10° +8°');
      const first = (await textOf(frame, '.city:first-child')).replace(/\n/g, ' ').trim();
      assertTrue(checks, 'первая строка — Ярославль', first.includes('Ярославль'), first);
    } catch (e) {
      await shot(page, 'scenario-7-race');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// Сценарии инвариантов storage (C3-д): значение в хранилище — объект; чтение
// принимает и старую строку; подсказка — только при выданном праве.
async function scenarioStorageCity(browser, base) {
  await scenario('8. Город в storage — объект; повторное открытие читает его', async (checks) => {
    const grant = GRANT_ALL + ',storage';
    const fixtures = [FX + 'forecast-yaroslavl.json', FX + 'geocode-arts.json', FX + 'forecast-batch.json'].join(',');
    const { context, page, frame } = await openEmulator(browser, base, { grant, fixtures });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      await frame.locator('.city').first().click();

      // Барьер — сама запись, а не неделя в DOM: скелетон тоже содержит семь .day,
      // и ожидание по ним ловит момент ДО того, как хост обработал storage.set.
      // Ждём появления ключа в хранилище хоста (пишет его именно хост, panel —
      // в sandbox без доступа к localStorage).
      await page.waitForFunction(() => {
        for (let i = 0; i < localStorage.length; i++) {
          const k = localStorage.key(i);
          if (k.startsWith('ccs.plugin.') && k.endsWith('.city')) return true;
        }
        return false;
      }, { timeout: 15000 });

      // Что реально лежит в хранилище хоста: значение под ключом .city. Панель
      // шлёт объект, сериализует хост — двойное кодирование (JSON.stringify(city)
      // в rememberCity) краснит именно эту проверку.
      const raw = await page.evaluate(() => {
        for (let i = 0; i < localStorage.length; i++) {
          const k = localStorage.key(i);
          if (k.startsWith('ccs.plugin.') && k.endsWith('.city')) return localStorage.getItem(k);
        }
        return null;
      });
      assertTrue(checks, 'запись в storage есть', raw !== null, 'ключа .city в хранилище нет');
      let value = null;
      try { value = JSON.parse(raw); } catch (e) { /* не JSON — проверка красная */ }
      assertTrue(checks, 'значение — объект, не строка',
        value && typeof value === 'object' && typeof value.lat === 'number' && typeof value.name === 'string',
        'raw: ' + raw);

      // Чтение: та же страница заново (тот же контекст — localStorage жив). Значение
      // перекладываем в старый формат (строка внутри JSON) — панель обязана принять
      // и его: в логе координаты запомненного города, а не дефолтного Ярославля.
      await page.evaluate((v) => {
        for (let i = 0; i < localStorage.length; i++) {
          const k = localStorage.key(i);
          if (k.startsWith('ccs.plugin.') && k.endsWith('.city')) localStorage.setItem(k, JSON.stringify(v));
        }
      }, value);
      await page.goto(base + '/' + DEV_HOST + '?' + new URLSearchParams({ panel: PANEL, grant, fixtures }).toString(), { waitUntil: 'load' });
      const frame2 = await panelFrame(page);
      await frame2.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      const logText = await page.evaluate(() => document.getElementById('log').textContent);
      assertTrue(checks, 'панель прочитала город из старой строки и запросила его координаты',
        logText.includes('latitude=' + value.lat), 'лог: ' + logText.replace(/\n/g, ' ').slice(0, 400));
    } catch (e) {
      await shot(page, 'scenario-8-storage');
      throw e;
    } finally {
      await context.close();
    }
  });
}

async function scenarioHint(browser, base) {
  await scenario('9. Подсказка про запоминание — только при выданном storage', async (checks) => {
    const fixtures = [FX + 'forecast-yaroslavl.json', FX + 'geocode-arts.json', FX + 'forecast-batch.json'].join(',');
    for (const [grant, expect] of [[GRANT_ALL, false], [GRANT_ALL + ',storage', true]]) {
      const { context, page, frame } = await openEmulator(browser, base, { grant, fixtures });
      try {
        await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
        await frame.locator('#citybtn').click();
        await frame.locator('#q').waitFor({ timeout: 5000 });
        const n = await frame.locator('.hint').count();
        assertEq(checks, 'подсказок при ' + (grant.includes('storage') ? 'storage выдан' : 'без storage'), n, expect ? 1 : 0);
        if (expect) {
          const t = (await textOf(frame, '.hint')).trim();
          assertEq(checks, 'текст подсказки', t, 'Город запоминается в этом браузере.');
        }
      } catch (e) {
        await shot(page, 'scenario-9-hint-' + (expect ? 'on' : 'off'));
        throw e;
      } finally {
        await context.close();
      }
    }
  });
}

async function scenarioGeoDenied(browser, base) {
  await scenario('10. Нет права на геокодер — человеческий текст и бейдж ключа', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: 'api.open-meteo.com',
      fixtures: FX + 'forecast-yaroslavl.json',
    });
    try {
      // Главный экран работает: права на прогноз достаточно.
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.notice-row').waitFor({ timeout: 15000 });
      const notice = (await textOf(frame, '.notice-row')).replace(/\n/g, ' ').trim();
      assertTrue(checks, 'человеческий текст отказа', notice.includes('Поиск города недоступен'), notice);
      const badge = (await textOf(frame, '.notice-row .cc-badge')).trim();
      assertEq(checks, 'бейдж с ключом права', badge, 'net:geocoding-api.open-meteo.com');
      // Главный экран не сломан: «Назад» — неделя на месте.
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      assertEq(checks, 'строк недели', await frame.locator('.week .day').count(), 7);
    } catch (e) {
      await shot(page, 'scenario-10-geo-denied');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// M3: запрос, стёртый ниже двух символов, гасит поиск, который уже в полёте.
async function scenarioSearchClear(browser, base) {
  await scenario('11. Сброс запроса ниже двух символов гасит предыдущий поиск (M3)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      // Ответ геокодера намеренно с опозданием: человек стирает запрос ДО ответа.
      // «Яр» и «Ярц» содержат один и тот же префикс — привязка по нему ловит оба.
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80|3000',
        FX + 'forecast-batch-arts.json|55.06491',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Яр');
      // Ждём, пока дебаунс сработал и поиск реально ушёл к геокодеру (иначе
      // сброс просто отменит таймер — это сценарий 12, а не гашение ответа).
      await page.waitForTimeout(600);
      await frame.locator('#q').fill('Я'); // стёрли до одного символа — поиска нет
      // Ждём реальной доставки старого ответа: без гашения он нарисовал бы
      // города по уже стёртому «Яр».
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('←') && p.textContent.includes('name=%D0%AF%D1%80'));
      }, { timeout: 20000 });
      assertEq(checks, 'строк результатов после сброса нет', await frame.locator('.city').count(), 0);
    } catch (e) {
      await shot(page, 'scenario-11-clear');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// M4: выход из режима в пределах дебаунса не должен оставлять висящий таймер.
async function scenarioSearchTimer(browser, base) {
  await scenario('12. Выход из режима в пределах дебаунса гасит висящий таймер (M4)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Яр');
      // Быстрее дебаунса: «Назад», затем снова «Другой город». Без гашения таймера
      // дебаунс сработает уже в закрытом режиме — с пустым запросом.
      await frame.locator('#back').click();
      await frame.locator('#citybtn').click();
      await page.waitForTimeout(1000); // время, к которому сработал бы старый дебаунс
      const logText = await page.evaluate(() => document.getElementById('log').textContent);
      assertTrue(checks, 'пустого запроса к геокодеру не было', !logText.includes('search?name=&'),
        'лог: ' + logText.replace(/\n/g, ' ').slice(0, 400));
    } catch (e) {
      await shot(page, 'scenario-12-timer');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// M5: право на геокодер, выданное после первого отказа, снимает липкую плашку.
async function scenarioGeoRightLater(browser, base) {
  await scenario('13. Выданное позже право на геокодер оживляет поиск при открытии (M5)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: 'api.open-meteo.com', // права на геокодер сначала нет
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.notice-row').waitFor({ timeout: 15000 }); // отказ — плашка есть

      // «Право выдали»: хост обновляет права и переинициализирует панель с новым
      // granted — ровно так же, как при пересборке фрейма на боевом хосте.
      await page.evaluate(() => {
        const g = window.__emulator.cfg.grant;
        g.push('net:geocoding-api.open-meteo.com');
        const inner = document.getElementById('frame').contentWindow.frames[0];
        inner.postMessage({ source: 'ccs-host', v: 1, type: 'init', granted: g }, '*');
      });

      // Повторное открытие режима: старой плашки быть не должно.
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      assertEq(checks, 'плашки отказа при повторном открытии нет', await frame.locator('.notice-row').count(), 0);
      // И поиск действительно работает: результаты по «Ярц».
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      assertTrue(checks, 'результаты поиска есть', await frame.locator('.city').count() > 0);
    } catch (e) {
      await shot(page, 'scenario-13-right-later');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// M2: хранилище отключено (геттер бросит) — эмулятор отвечает, а не роняет обработчик.
async function scenarioStorageOff(browser, base) {
  await scenario('14. Хранилище отключено: эмулятор отвечает unavailable, панель работает (M2)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL + ',storage',
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // «Отключаем» хранилище: доступ к localStorage бросает, как при отключённом
      // хранилище браузера. Геттер читается на месте вызова storage.* — именно
      // этот случай и должен ловиться обёрткой (minor M2).
      await page.evaluate(() => {
        Object.defineProperty(window, 'localStorage', {
          configurable: true,
          get: function () { throw new Error('storage disabled'); },
        });
      });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      await frame.locator('.city').first().click(); // rememberCity → storage.set
      // Хост-эмулятор обязан ОТВЕТИТЬ (result unavailable), а не потерять вызов:
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('← unavailable: Хранилище недоступно'));
      }, { timeout: 15000 });
      // Панель не зависла: главный экран с выбранным городом на месте.
      const cityName = (await textOf(frame, '.wx-city .nm')).trim();
      assertTrue(checks, 'город выбран, панель работает', cityName.includes('Ярцево'), cityName);
    } catch (e) {
      await shot(page, 'scenario-14-storage-off');
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
  await scenarioRace(browser, base);
  await scenarioStorageCity(browser, base);
  await scenarioHint(browser, base);
  await scenarioGeoDenied(browser, base);
  await scenarioSearchClear(browser, base);
  await scenarioSearchTimer(browser, base);
  await scenarioGeoRightLater(browser, base);
  await scenarioStorageOff(browser, base);
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
