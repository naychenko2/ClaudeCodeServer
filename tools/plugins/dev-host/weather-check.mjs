#!/usr/bin/env node
// Автопроверка панели «Погода» (шаг C3 плана «Погода»).
//
// Прогоняет сценарии Playwright'ом против хост-эмулятора
// (tools/plugins/dev-host/) на фикстурах из tools/plugins/dev-host/fixtures/.
//
// Лежит рядом с эмулятором, а не в папке пакета: пакет «Погода» устанавливается
// «папкой» целиком, а check-файл в allow-list пакета не входит (дефект 203422c3,
// правка C5).
//
// Запуск из корня репозитория:
//   node tools/plugins/dev-host/weather-check.mjs
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
//      4d–4f: разные отказы — разные тексты: таймаут и сбой на стороне сервиса,
//      ответ без прогноза, ответ не разобрался (правки 23.10 и C5-д).
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
//   12. Выход из режима по «Назад» не оставляет висящего дебаунса: ни одного
//       запроса к геокодеру — и без повторного открытия режима (minor M4, major MAJOR-1).
//   13. Выданное позже право на геокодер оживляет поиск при повторном
//       открытии — липкая плашка отказа снимается (minor M5).
//   14. Хранилище отключено: эмулятор отвечает «unavailable», а не роняет
//       обработчик (minor M2, ревью R2-д).
//   17. Пустые температуры в списке городов — прочерк «—», не пустое место
//       (правка приёмки 23.10).
//   18. Порядок результатов: своя страна (RU) раньше чужих, внутри страны — по
//       населению; подпись «Город, Регион, Страна» (правка приёмки 23.10).
//   19. Обещание запоминания — только если операция storage реально отвечает,
//       а не просто выдано право (правка приёмки 23.10).
//   20. Лимит частоты (limit) — «Слишком много запросов», не «нет связи» (C5-д, MAJOR 1).
//   21. Исключение внутри renderMain — «Прогноз не удалось показать»;
//       «Обновить данные» — self-restart: панель жива, данные запрошены заново,
//       эмулятор не отвечает init на повторный ready (C5-д, C5-д3).
//   22. Квоты: до 5 своих + до 2 иностранных, свои первыми; иностранный не
//       вытесняет мелкого своего, «ru» в нижнем регистре — своя страна (C5-д2).
//   23. Свободные места своих достаются иностранным: 2 + (5 − N_своих), потолок 7 (C5-д3).
//   29. Два открытия режима во время пробы — один storage.get (C5-д3).
//   30. Право storage отозвано после успешной пробы — подсказки нет (C5-д3).
//   31. Крайние квоты: 5 своих + 5 иностранных → 5 + 2; 0 своих + 7 иностранных → 7 (C5-м).
//   32. Self-restart сбрасывает storageOk/offlineShown; online — один load() (C5-м).
//   24. Температура 0 — «0°», а не прочерк (C5-д, minor).
//   25. storage «denied»: при отказе хранилища подсказки о запоминании нет (C5-д, minor).
//   4g. unavailable / not_found / invalid — свои тексты, не «нет связи» (C5-д2).
//   26. «Повторить» уходит новым запросом net.fetch (C5-д2).
//   27. Сбой пробы storage не кэшируется: вторая проба удалась — подсказка есть (C5-д2).
//   28. Равное население — порядок по названию; нет поля country — код страны (C5-д2).

import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, extname, sep } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..', '..'); // tools/plugins/dev-host → корень репозитория
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

  // Правка 23.10 и C5-д2: разные коды отказа хоста — разные тексты. Таймаут и
  // upstream — настоящий сетевой обрыв на бою: «Сервис погоды не ответил».
  for (const failCode of ['timeout', 'upstream']) {
    await scenario('4d. ' + failCode + ' — «Сервис погоды не ответил», не «нет связи»', async (checks) => {
      const { context, page, frame } = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fail: failCode,
      });
      try {
        await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
        const title = (await textOf(frame, '.cc-empty__title')).trim();
        assertEq(checks, 'заголовок ответа сервиса', title, 'Сервис погоды не ответил');
        const text = (await textOf(frame, '.cc-empty__text')).trim();
        assertEq(checks, 'текст повтори', text, 'Попробуйте ещё раз.');
        const btn = await frame.locator('#retry').innerText().catch(() => '');
        assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
      } catch (e) {
        await shot(page, 'scenario-4d-' + failCode);
        throw e;
      } finally {
        await context.close();
      }
    });
  }

  // C5-д2 (MAJOR): unavailable — про плагин, остальные коды хоста — нейтральный
  // дефолт; ни тот ни другой не «нет связи».
  for (const [failCode, wantTitle, wantText, wantBtn] of [
    ['unavailable', 'Плагин погоды сейчас недоступен', 'Попробуйте открыть панель позже или включите плагин в разделе «Плагины»', false],
    ['not_found', 'Данные о погоде получить не удалось', 'Попробуйте ещё раз.', true],
    ['invalid', 'Данные о погоде получить не удалось', 'Попробуйте ещё раз.', true],
  ]) {
    await scenario('4g. ' + failCode + ' — «' + wantTitle + '», не «нет связи»', async (checks) => {
      const { context, page, frame } = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fail: failCode,
      });
      try {
        await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
        assertEq(checks, 'заголовок', (await textOf(frame, '.cc-empty__title')).trim(), wantTitle);
        assertEq(checks, 'текст', (await textOf(frame, '.cc-empty__text')).trim(), wantText);
        assertEq(checks, 'кнопка «Повторить»', await frame.locator('#retry').count(), wantBtn ? 1 : 0);
        const all = (await textOf(frame, '#wx')).toLowerCase();
        assertTrue(checks, 'слов «нет связи» нет', !all.includes('нет связи'), all);
      } catch (e) {
        await shot(page, 'scenario-4g-' + failCode);
        throw e;
      } finally {
        await context.close();
      }
    });
  }

  await scenario('4e. Ответ без прогноза — «Сервис погоды не прислал прогноз»', async (checks) => {
    // Синтетическая фикстура: current есть, daily нет — сервис ответил успешно,
    // «вернул ошибку» про этот случай неверно (правка 23.10).
    const nodata = '.cc-attachments/weather-check/forecast-no-daily.json';
    await mkdir(join(ROOT, '.cc-attachments', 'weather-check'), { recursive: true });
    await writeFile(join(ROOT, nodata), JSON.stringify({
      latitude: 57.63, longitude: 39.87, utc_offset_seconds: 10800, timezone: 'Europe/Moscow',
      current: { time: '2026-10-22T15:45', temperature_2m: 10, weather_code: 1 },
    }));
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: nodata,
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок без прогноза', title, 'Сервис погоды не прислал прогноз');
      const text = (await textOf(frame, '.cc-empty__text')).trim();
      assertEq(checks, 'текст через минуту', text, 'Попробуйте ещё раз через минуту.');
      const btn = await frame.locator('#retry').innerText().catch(() => '');
      assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
    } catch (e) {
      await shot(page, 'scenario-4e-no-data');
      throw e;
    } finally {
      await context.close();
    }
  });

  await scenario('4f. Ответ не разбирается — «Не удалось прочитать ответ сервиса погоды»', async (checks) => {
    const badbody = '.cc-attachments/weather-check/forecast-not-json.json';
    await writeFile(join(ROOT, badbody), '<html>это не JSON</html>');
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: badbody,
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок не разобрался', title, 'Не удалось прочитать ответ сервиса погоды');
      const text = (await textOf(frame, '.cc-empty__text')).trim();
      assertEq(checks, 'текст повтори', text, 'Попробуйте ещё раз.');
      const btn = await frame.locator('#retry').innerText().catch(() => '');
      assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
    } catch (e) {
      await shot(page, 'scenario-4f-bad-body');
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
  await scenario('7. Гонка: два поиска подряд — старый геокодер не перетирает новый список (R3)', async (checks) => {
    const GAP = 2000; // пауза между поисками (больше дебаунса)
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      // Фикстуры привязаны к запросу через «|match», а не к порядку вызовов.
      // Медленный здесь СТАРЫЙ ГЕОКОДЕР (6000 мс): именно он перетирает `found`
      // и без guard'а searchSeq подменил бы уже нарисованный новый список.
      // Задержка пакета не годится: пакет применяется к собственному снимку
      // и в новый список не попадает при любом guard'е (MAJOR-2, ревью R3).
      // Match'и без запятых (параметр разделяется запятыми) и уникальные.
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86|6000',
        FX + 'forecast-batch-arts.json|55.06491',
        FX + 'geocode-yaroslavl.json|name=%D0%AF%D1%80%D0%BE%D1%81%D0%BB%D0%B0%D0%B2',
        FX + 'forecast-batch.json|58.58259',
      ].join(','),
    });
    try {
      await frame.locator('.hero .temp').waitFor({ timeout: 20000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await page.waitForTimeout(GAP);
      await frame.locator('#q').fill('Ярослав');
      // Ждём не стенку времени, а сигнал эмулятора «ответ старого геокодера
      // отправлен панели» (строка «⇢ доставлено» пишется после postToPanel).
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('⇢ доставлено') && p.textContent.includes('name=%D0%AF%D1%80%D1%86'));
      }, { timeout: 20000 });
      // Сообщение уже в очереди фрейма: два кадра — и панель его обработала.
      await frame.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
      // Без guard'а старый геокодер перетёр бы список «Ярцево…» без температур.
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
// принимает и старую строку.
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

// Сценарии инвариантов storage (C3-д): значение в хранилище — объект; чтение
// принимает и старую строку; подсказка — только если операция реально отвечает
// (правка приёмки 23.10: одного права storage мало).
async function scenarioHint(browser, base) {
  await scenario('9. Подсказка про запоминание — только при работающем storage', async (checks) => {
    const fixtures = [FX + 'forecast-yaroslavl.json', FX + 'geocode-arts.json', FX + 'forecast-batch.json'].join(',');
    for (const [grant, expect] of [[GRANT_ALL, false], [GRANT_ALL + ',storage', true]]) {
      const { context, page, frame } = await openEmulator(browser, base, { grant, fixtures });
      try {
        await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
        await frame.locator('#citybtn').click();
        await frame.locator('#q').waitFor({ timeout: 5000 });
        if (expect) {
          // Подсказка появляется после пробы операции — ждём её, а не стенку времени.
          await frame.locator('.hint').waitFor({ timeout: 5000 });
          assertEq(checks, 'подсказка при работающем storage', await frame.locator('.hint').count(), 1);
          const t = (await textOf(frame, '.hint')).trim();
          assertEq(checks, 'текст подсказки', t, 'Город запоминается в этом браузере.');
        } else {
          // Без права пробы не будет вовсе: плашки нет сразу.
          assertEq(checks, 'подсказок без права storage нет', await frame.locator('.hint').count(), 0);
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

// M4 (ревью R2-д) и MAJOR-1 (ревью R3): инвариант — дебаунс живёт только пока
// открыт режим поиска. Проверка — по факту запросов к геокодеру, а не по стенке
// времени: после «Назад» их не должно быть ни одного, в том числе без повторного
// открытия режима (именно этого прежняя проверка не ловила).
async function scenarioSearchTimer(browser, base) {
  await scenario('12. Выход по «Назад» не оставляет висящего дебаунса (M4, MAJOR-1)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80|1500',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    // Инвариант проверяется по логу эмулятора: строки «→ net.fetch https://geocoding-api…»
    // — это реальные запросы к геокодеру.
    const geoCalls = () => page.evaluate(() =>
      Array.from(document.querySelectorAll('#log p'))
        .filter((p) => p.textContent.includes('→ net.fetch https://geocoding-api')).length);
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Шаг 1 (MAJOR-1): «Назад» БЕЗ повторного открытия режима — в пределах
      // дебаунса. Без гашения запрос ушёл бы уже в закрытом режиме.
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Яр');
      await frame.locator('#back').click();
      await page.waitForTimeout(1200); // дольше дебаунса (400 мс)
      assertEq(checks, 'после «Назад» запросов к геокодеру нет', await geoCalls(), 0);
      // Шаг 2: тот же инвариант после повторного открытия режима и выхода.
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Яр');
      await frame.locator('#back').click();
      await page.waitForTimeout(1200);
      assertEq(checks, 'после второго «Назад» запросов к геокодеру тоже нет', await geoCalls(), 0);
      // Шаг 3 (MINOR-A): «Назад» при ответе В ПОЛЁТЕ. Геокодер ответит через 1500 мс —
      // уже в закрытом режиме; searchSeq++ в closeSearch обязан погасить ответ, и
      // пакет температур не должен уйти (ни геокодера, ни пакета после «Назад»).
      const searchCalls = () => page.evaluate(() =>
        Array.from(document.querySelectorAll('#log p'))
          .filter((p) => p.textContent.includes('→ net.fetch') && !p.textContent.includes('apparent_temperature')).length);
      const before = await geoCalls();
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Яр');
      await page.waitForFunction((n) => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.filter((p) => p.textContent.includes('→ net.fetch https://geocoding-api')).length > n;
      }, before, { timeout: 5000 });
      const inFlight = await searchCalls();
      await frame.locator('#back').click();
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('⇢ доставлено') && p.textContent.includes('name=%D0%AF%D1%80'));
      }, { timeout: 10000 });
      await frame.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
      assertEq(checks, 'после «Назад» при ответе в полёте новых запросов (геокодер/пакет) нет', await searchCalls(), inFlight);
    } catch (e) {
      await shot(page, 'scenario-12-timer');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// MINOR-B (ревью R3): право с портом «net:host:443» — тот же хост, что и без порта
// (боевой PluginNetProxy.HostOf отрезает порт по последнему двоеточию, без регистра).
// Эмулятор и панель обязаны сойтись с боем: запрос уходит, плашки отказа нет.
async function scenarioGeoRightWithPort(browser, base) {
  await scenario('15. Право net:геокодер:443 (с портом) — запрос уходит, плашки отказа нет (MINOR-B)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: 'api.open-meteo.com,net:geocoding-api.open-meteo.com:443',
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json|55.06491',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const geo = await page.evaluate(() =>
        Array.from(document.querySelectorAll('#log p'))
          .filter((p) => p.textContent.includes('→ net.fetch https://geocoding-api')).length);
      assertTrue(checks, 'запрос к геокодеру ушёл', geo >= 1, String(geo));
      assertEq(checks, 'плашки «Поиск города недоступен» нет', await frame.locator('.notice-row').count(), 0);
    } catch (e) {
      await shot(page, 'scenario-15-port-right');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// MINOR-C (ревью R3): событие `online` перерисовывает панель через load() и тем самым
// закрывает режим поиска — висящий дебаунс обязан погаснуть, иначе запрос к геокодеру
// уйдёт уже в закрытом режиме.
async function scenarioOnlineClosesSearch(browser, base) {
  await scenario('16. «online» в режиме поиска гасит дебаунс (MINOR-C)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    const geoCalls = () => page.evaluate(() =>
      Array.from(document.querySelectorAll('#log p'))
        .filter((p) => p.textContent.includes('→ net.fetch https://geocoding-api')).length);
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Уходим офлайн и обновляем: панель показывает «Нет подключения» (offlineShown).
      await context.setOffline(true);
      await frame.locator('#refresh').click();
      await frame.locator('#retry').waitFor({ timeout: 5000 });
      // C5-д (minor): офлайн-кадр назван явно про интернет, чтобы не путать его
      // с «Нет связи с сервисом погоды».
      const offlineTitle = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'офлайн-заголовок — явно про интернет', offlineTitle, 'Нет связи с интернетом');
      // Из офлайн-экрана открываем поиск и набираем запрос — дебаунс взведён.
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      // Отсчёт ведём от взведения таймера: стена от момента проверки при зависшей
      // вкладке прошла бы вакуумно (таймер сработал бы уже после неё).
      const armedAt = await page.evaluate(() => Date.now());
      await frame.locator('#q').fill('Яр');
      // Связь вернулась: `online` → load() → режим закрыт.
      await context.setOffline(false);
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Ждём не стену, а факт: с момента взведения прошло больше дебаунса (400 мс) с запасом,
      // и таймер при этом успел бы сработать (часы страницы — те же, что у таймера).
      await page.waitForFunction((t0) => Date.now() - t0 >= 800, armedAt);
      assertEq(checks, 'после «online» запросов к геокодеру нет', await geoCalls(), 0);
    } catch (e) {
      await shot(page, 'scenario-16-online');
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

// Правка 23.10: если геокодер ответил, а пакетный прогноз не пришёл, в строке
// — прочерк, а не пустое место.
async function scenarioEmptyTemps(browser, base) {
  await scenario('17. Пустые температуры в списке — «—», не пустое место (правка 23.10)', async (checks) => {
    // Синтетический пакет без current: геокодер ответил, температур нет.
    const batchEmpty = '.cc-attachments/weather-check/batch-no-current.json';
    await mkdir(join(ROOT, '.cc-attachments', 'weather-check'), { recursive: true });
    await writeFile(join(ROOT, batchEmpty), '[]');
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        batchEmpty + '|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      // Пакет пуст: на каждой строке есть место температуры, и везде прочерк.
      const count = await frame.locator('.city').count();
      const temps = await frame.locator('.city .t').allInnerTexts();
      assertEq(checks, 'места температуры на каждой строке', temps.length, count);
      assertEq(checks, 'все температуры — прочерки', temps.map((t) => t.trim()).join(' '), Array(count).fill('—').join(' '));
    } catch (e) {
      await shot(page, 'scenario-17-empty-temps');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// Правка 23.10: сперва своя страна (панель ищет по-русски), внутри страны — по
// населению; подпись «Город, Регион, Страна». Страна — из country_code, не по
// названию: «Торонто» не встанет перед русским «Тором».
async function scenarioCountrySort(browser, base) {
  await scenario('18. Квоты: свои раньше иностранных, подпись «Город, Регион, Страна» (правка 23.10, C5-д2)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-tor.json|name=%D0%A2%D0%BE%D1%80',
        FX + 'batch-tor.json|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Тор');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const lines = await frame.locator('.city').allInnerTexts();
      assertEq(checks, 'строк результатов', lines.length, 3);
      const first = lines[0].replace(/\n/g, ' ').trim();
      // По одному населению первым был бы Торонто (2,7 млн) — страна важнее.
      assertTrue(checks, 'первая строка — Тор в России', first.includes('Тор,') && first.includes('Россия'), first);
      assertTrue(checks, 'страна читается рядом с названием', first.includes(', Архангельская обл., Россия'), first);
      // Чужие страны — после своей; внутри них прежний порядок по населению.
      const second = lines[1].replace(/\n/g, ' ').trim();
      const third = lines[2].replace(/\n/g, ' ').trim();
      assertTrue(checks, 'вторая строка — Канада (население больше германского Тора)', second.includes('Канада'), second);
      assertTrue(checks, 'третья строка — Германия', third.includes('Германия'), third);
    } catch (e) {
      await shot(page, 'scenario-18-country-sort');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// Правка 23.10: обещание «город запоминается» — только если операция storage
// реально отвечает. Выданное право при недоступных операциях — не повод обещать.
async function scenarioStorageUnavailable(browser, base) {
  await scenario('19. Хранилище недоступно: обещания запоминания нет (правка 23.10)', async (checks) => {
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
      // «Отключаем» хранилище: геттер бросает — хост отвечает unavailable.
      await page.evaluate(() => {
        Object.defineProperty(window, 'localStorage', {
          configurable: true,
          get: function () { throw new Error('storage disabled'); },
        });
      });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      // Ждём факт пробы: в логе эмулятора — ответ unavailable.
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('← unavailable: Хранилище недоступно'));
      }, { timeout: 15000 });
      assertEq(checks, 'подсказки про запоминание нет', await frame.locator('.hint').count(), 0);
      // Сам поиск работает: результаты есть — и обещания всё равно нет.
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      assertEq(checks, 'подсказки нет и при результатах', await frame.locator('.hint').count(), 0);
    } catch (e) {
      await shot(page, 'scenario-19-storage-unavailable');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д (MAJOR 1): коды отказа хоста различаются. Лимит частоты — не «нет связи»:
// связь есть, упёрлись в частоту запросов (30 в минуту — достижимо реальным
// поиском с дебаунсом и батчем температур).
async function scenarioRateLimit(browser, base) {
  await scenario('20. Лимит частоты (limit) — «Слишком много запросов», не «нет связи» (C5-д)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fail: 'limit',
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок про частоту', title, 'Слишком много запросов');
      const text = (await textOf(frame, '.cc-empty__text')).trim();
      // Код limit — и частота, и «аргументы слишком большие», и «слишком много
      // одновременных вызовов»: текст нейтральный, без «минуты» (C5-д3).
      assertEq(checks, 'нейтральный текст', text, 'Подождите немного и попробуйте снова.');
      assertTrue(checks, 'слова «минуту» нет', !/минут/.test(title + ' ' + text), title + ' ' + text);
      const btn = await frame.locator('#retry').innerText().catch(() => '');
      assertEq(checks, 'кнопка «Повторить»', btn.trim(), 'Повторить');
    } catch (e) {
      await shot(page, 'scenario-20-limit');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д (MAJOR 2): исключение внутри панели — не «сервис не прочитал ответ».
// Фикстура валидна по форме, но temperature_2m_min — строка вместо массива:
// renderMain бросает внутри панели.
async function scenarioRenderFail(browser, base) {
  await scenario('21. Ошибка внутри панели — «Прогноз не удалось показать» (C5-д)', async (checks) => {
    const broken = '.cc-attachments/weather-check/forecast-broken-array.json';
    await mkdir(join(ROOT, '.cc-attachments', 'weather-check'), { recursive: true });
    await writeFile(join(ROOT, broken), JSON.stringify({
      latitude: 57.63, longitude: 39.87, utc_offset_seconds: 10800, timezone: 'Europe/Moscow',
      current: { time: '2026-10-22T15:45', temperature_2m: 10, weather_code: 1 },
      daily: {
        time: ['2026-10-22', '2026-10-23'], weather_code: [1, 2],
        temperature_2m_min: 'oops', temperature_2m_max: [12, 8],
        precipitation_probability_max: [10, 20],
      },
    }));
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: broken,
    });
    try {
      await frame.locator('.cc-empty__title').waitFor({ timeout: 15000 });
      const title = (await textOf(frame, '.cc-empty__title')).trim();
      assertEq(checks, 'заголовок ошибки панели', title, 'Прогноз не удалось показать');
      const text = (await textOf(frame, '.cc-empty__text')).trim();
      assertEq(checks, 'текст про обновление данных', text, 'Нажмите «Обновить данные», чтобы загрузить его заново.');
      // «Повторить» тут тупик — вместо него «Обновить данные» (self-restart).
      assertEq(checks, 'кнопки «Повторить» нет', await frame.locator('#retry').count(), 0);
      assertEq(checks, 'кнопка «Обновить данные»', (await frame.locator('#restart').innerText().catch(() => '')).trim(), 'Обновить данные');
      // Причина «починилась» (валидный ответ) — перезапуск должен поднять панель.
      await page.evaluate((path) => { window.__emulator.cfg.fixtures[0].path = path; }, '/' + FX + 'forecast-yaroslavl.json');
      const count = () => page.evaluate(() =>
        Array.from(document.querySelectorAll('#log p')).filter((p) => p.textContent.includes('→ net.fetch')).length);
      const before = await count();
      await frame.locator('#restart').click();
      // Панель жива: герой и неделя на месте, плагин в состоянии не потерян.
      await frame.locator('.hero').waitFor({ timeout: 15000 });
      assertEq(checks, 'герой есть', await frame.locator('.hero').count(), 1);
      assertEq(checks, 'в неделе 7 строк', await frame.locator('.week .day').count(), 7);
      assertTrue(checks, 'state.plugin не null', await frame.evaluate(() => !!(window.ccsPlugin.state && window.ccsPlugin.state.plugin)));
      assertEq(checks, 'запрос ровно один', (await count()) - before, 1);
      // Боевой гейт эмулятора: повторный ready остаётся без init.
      assertEq(checks, 'init хост отправил один раз', await page.evaluate(() => window.__emulator.initCount), 1);
      await frame.evaluate(() => window.ccsPlugin.ready());
      await page.waitForFunction(() =>
        Array.from(document.querySelectorAll('#log p')).some((p) => p.textContent.includes('повторный ready проигнорирован')),
      null, { timeout: 5000 });
      assertEq(checks, 'повторный ready init не породил', await page.evaluate(() => window.__emulator.initCount), 1);
    } catch (e) {
      await shot(page, 'scenario-21-render-fail');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д2 (продуктовое решение): раздельные квоты — до 5 своих по населению и до
// 2 иностранных по населению, сначала свои. Крупный иностранный не вытесняет
// мелкого своего; «ru» в нижнем регистре — тоже своя страна.
async function scenarioQuotas(browser, base) {
  await scenario('22. Квоты 5 своих + 2 иностранных: иностранный не вытесняет мелкого своего (C5-д2)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-mixed.json|name=%D0%A0%D0%BE%D0%B3%D0%B0%D1%87',
        FX + 'batch-mixed.json|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Рогач');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const lines = await frame.locator('.city').allInnerTexts().then((a) => a.map((s) => s.replace(/\n/g, ' ').trim()));
      assertEq(checks, 'строк результатов: 5 + 2', lines.length, 7);
      assertEq(checks, 'своих ровно 5', lines.filter((l) => l.includes('Россия')).length, 5);
      assertEq(checks, 'иностранных ровно 2', lines.filter((l) => !l.includes('Россия')).length, 2);
      assertTrue(checks, 'первая — самый крупный свой (600 тыс.)',
        lines[0].includes('Рогачёв,') && lines[0].includes('Тверская обл., Россия'), lines[0]);
      // Город с country_code «ru» (нижний регистр) — своя страна, в своём блоке.
      assertTrue(checks, '«ru» нижнего регистра — своя, четвёртая',
        lines[3].includes('Рогач,') && lines[3].includes('Псковская обл., Россия'), lines[3]);
      // Пятая своя — Рогачи (200 тыс.): её не вытеснил ни Роттердам (2 млн), ни Розенталь.
      assertTrue(checks, 'пятая своя — Рогачи (200 тыс.), не вытеснена иностранным',
        lines[4].includes('Рогачи') && lines[4].includes('Россия'), lines[4]);
      assertTrue(checks, 'шестая — Роттердам', lines[5].includes('Роттердам') && lines[5].includes('Нидерланды'), lines[5]);
      assertTrue(checks, 'седьмая — Розенталь', lines[6].includes('Розенталь') && lines[6].includes('Германия'), lines[6]);
      // Вытесняется шестой свой (100 тыс.) — своими же, а не иностранным.
      assertTrue(checks, 'шестой свой (Калининградская, 100 тыс.) вне выдачи', !lines.join(' ').includes('Калининградская'), lines.join(' '));
    } catch (e) {
      await shot(page, 'scenario-22-quotas');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д3: свободные места своих достаются иностранным. До 5 своих; иностранных —
// 2 + (5 − N_своих); общий потолок — 7 строк. Без своих — до 7 иностранных
// (в фикстуре их четыре — все четыре); при трёх своих — 3 + 4 = 7.
async function scenarioAllForeign(browser, base) {
  await scenario('23. Квоты со свободными местами: 0 своих — все иностранные, 3 своих — 3 + 4, потолок 7 (C5-д3)', async (checks) => {
    const open = async (geo, batch, q) => {
      const o = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fixtures: [FX + 'forecast-yaroslavl.json|apparent_temperature', FX + geo, FX + batch + '|latitude'].join(','),
      });
      await o.frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await o.frame.locator('#citybtn').click();
      await o.frame.locator('#q').waitFor({ timeout: 5000 });
      await o.frame.locator('#q').fill(q);
      await o.frame.locator('.city').first().waitFor({ timeout: 15000 });
      o.lines = await o.frame.locator('.city').allInnerTexts().then((a) => a.map((s) => s.replace(/\n/g, ' ').trim()));
      return o;
    };
    let o = await open('geocode-allforeign.json|name=%D0%9B%D0%B8%D0%BC', 'batch-allforeign.json', 'Лим');
    try {
      const lines = o.lines;
      assertEq(checks, '0 своих: все четыре иностранных, а не два', lines.length, 4);
      assertTrue(checks, '1 — Лима, Перу (9 млн)', lines[0].includes('Лима,') && lines[0].includes('Перу'), lines[0]);
      assertTrue(checks, '2 — Лимассо, Кипр (300 тыс.)', lines[1].includes('Лимассо') && lines[1].includes('Кипр'), lines[1]);
      assertTrue(checks, '3 — Лимы, Италия (100 тыс.)', lines[2].includes('Лимы') && lines[2].includes('Италия'), lines[2]);
      assertTrue(checks, '4 — Лима, Греция (50 тыс.)', lines[3].includes('Лима,') && lines[3].includes('Греция'), lines[3]);
    } catch (e) {
      await shot(o.page, 'scenario-23-all-foreign');
      throw e;
    } finally {
      await o.context.close();
    }
    o = await open('geocode-own3.json|name=%D0%9E', 'batch-allforeign.json', 'Ор');
    try {
      const lines = o.lines;
      const own = lines.filter((l) => l.includes('Россия')).length;
      assertEq(checks, '3 своих — все три', own, 3);
      assertEq(checks, 'иностранных 2 + 2 свободных места = 4', lines.length - own, 4);
      assertEq(checks, 'общий потолок 7', lines.length, 7);
      assertTrue(checks, 'свои первыми', lines.slice(0, 3).every((l) => l.includes('Россия')), lines.join(' | '));
      assertTrue(checks, 'иностранные по населению: Осло, Оттава, Остин, Орлеан',
        lines[3].includes('Осло') && lines[4].includes('Оттава') && lines[5].includes('Остин') && lines[6].includes('Орлеан'), lines.slice(3).join(' | '));
      assertTrue(checks, 'пятый иностранный (Оруро) вне выдачи', !lines.join(' ').includes('Оруро'), lines.join(' | '));
    } catch (e) {
      await shot(o.page, 'scenario-23-own3');
      throw e;
    } finally {
      await o.context.close();
    }
  });
}

// C5-д (minor): ноль — это значение, а не пустота. Прочерк ставят только
// отсутствующие значения.
async function scenarioZeroTemp(browser, base) {
  await scenario('24. Температура 0 — «0°», а не прочерк (C5-д)', async (checks) => {
    const zero = '.cc-attachments/weather-check/forecast-zero.json';
    await mkdir(join(ROOT, '.cc-attachments', 'weather-check'), { recursive: true });
    await writeFile(join(ROOT, zero), JSON.stringify({
      latitude: 57.63, longitude: 39.87, utc_offset_seconds: 10800, timezone: 'Europe/Moscow',
      current: {
        time: '2026-10-22T15:45', temperature_2m: 0, apparent_temperature: 0,
        relative_humidity_2m: 80, weather_code: 0, wind_speed_10m: 2, wind_direction_10m: 180, is_day: 1,
      },
      daily: {
        time: ['2026-10-22', '2026-10-23'], weather_code: [0, 1],
        temperature_2m_max: [0, 1], temperature_2m_min: [0, -1],
        precipitation_probability_max: [0, 10],
      },
    }));
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: zero,
    });
    try {
      await frame.locator('.hero .temp').waitFor({ timeout: 15000 });
      assertEq(checks, 'температура героя — 0°', (await textOf(frame, '.hero .temp')).trim(), '0°');
      const hl = (await textOf(frame, '.hero .hl')).trim();
      assertTrue(checks, 'днём/ночью в герое — 0°', hl.includes('Днём 0°') && hl.includes('ночью 0°'), hl);
      const firstRow = frame.locator('.week .day').first();
      assertEq(checks, 'первая строка недели — min 0°', (await firstRow.locator('.lo').innerText()).trim(), '0°');
      assertEq(checks, 'первая строка недели — max 0°', (await firstRow.locator('.hi').innerText()).trim(), '0°');
    } catch (e) {
      await shot(page, 'scenario-24-zero-temp');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д (minor): «denied» по хранилищу. Панель видит право по префиксу «storage»,
// хост отказывает неизвестное право («storageX» ≠ «storage») — обещания
// запоминания нет, панель работает.
async function scenarioStorageDenied(browser, base) {
  await scenario('25. storage denied: хост отказывает операцию — подсказки нет (C5-д)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL + ',storageX',
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json|55.06491',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Факт отказа по storage есть в логе хоста: код denied.
      await page.waitForFunction(() => {
        const lines = Array.from(document.querySelectorAll('#log p'));
        return lines.some((p) => p.textContent.includes('← denied: Нет права: storage'));
      }, { timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Ярц');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      assertEq(checks, 'подсказки про запоминание нет', await frame.locator('.hint').count(), 0);
      assertTrue(checks, 'сам поиск работает', await frame.locator('.city').count() > 0);
    } catch (e) {
      await shot(page, 'scenario-25-storage-denied');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д2 (minor): «Повторить» действительно перезапрашивает прогноз — считаем
// запросы net.fetch в логе хоста, а не только смену текста.
async function scenarioRetryRequests(browser, base) {
  await scenario('26. «Повторить» уходит новым запросом (C5-д2)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fail: 'upstream',
    });
    try {
      await frame.locator('#retry').waitFor({ timeout: 15000 });
      const count = () => page.evaluate(() =>
        Array.from(document.querySelectorAll('#log p')).filter((p) => p.textContent.includes('→ net.fetch')).length);
      const before = await count();
      await frame.locator('#retry').click();
      await page.waitForFunction((n) =>
        Array.from(document.querySelectorAll('#log p')).filter((p) => p.textContent.includes('→ net.fetch')).length > n, before, { timeout: 15000 });
      assertEq(checks, 'после «Повторить» ушёл ровно один новый запрос', (await count()) - before, 1);
      await frame.locator('#retry').waitFor({ timeout: 15000 });
    } catch (e) {
      await shot(page, 'scenario-26-retry');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д2 (minor): проба storage.get кэширует только успех. Первая проба не
// удалась (хранилище недоступно) — подсказки нет; вторая удалась — подсказка есть.
async function scenarioProbeRetry(browser, base) {
  await scenario('27. Проба storage: сбой не кэшируется — вторая проба удалась, подсказка есть (C5-д2)', async (checks) => {
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
      await page.evaluate(() => {
        window.__lsDesc = Object.getOwnPropertyDescriptor(window, 'localStorage');
        Object.defineProperty(window, 'localStorage', {
          configurable: true,
          get: function () { throw new Error('storage disabled'); },
        });
      });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await page.waitForFunction(() =>
        Array.from(document.querySelectorAll('#log p')).some((p) => p.textContent.includes('← unavailable: Хранилище недоступно')),
      null, { timeout: 15000 });
      assertEq(checks, 'первая проба не удалась — подсказки нет', await frame.locator('.hint').count(), 0);
      // Хост «починился»; выходим из режима и открываем его снова.
      await page.evaluate(() => { Object.defineProperty(window, 'localStorage', window.__lsDesc); });
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('.hint').waitFor({ timeout: 5000 });
      assertEq(checks, 'вторая проба удалась — подсказка появилась', (await textOf(frame, '.hint')).trim(), 'Город запоминается в этом браузере.');
    } catch (e) {
      await shot(page, 'scenario-27-probe-retry');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д2 (minor): страна в подписи без поля country (запасной country_code) и
// порядок при равном населении не зависит от порядка ответа геокодера.
async function scenarioTiesAndCountryFallback(browser, base) {
  await scenario('28. Равное население — порядок по названию; без поля country — код страны (C5-д2)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-ties.json|name=%D0%A2%D0%B5%D1%81',
        FX + 'batch-mixed.json|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Тес');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const lines = await frame.locator('.city').allInnerTexts().then((a) => a.map((s) => s.replace(/\n/g, ' ').trim()));
      assertEq(checks, 'строк результатов', lines.length, 4);
      assertTrue(checks, 'первый — Тесто (300 тыс.)', lines[0].includes('Тесто'), lines[0]);
      // В ответе «Тесов» стоит раньше «Тесны», население равное: решает название.
      assertTrue(checks, 'при равном населении «Тесна» раньше «Тесова»', lines[1].includes('Тесна') && lines[2].includes('Тесов'), lines[1] + ' | ' + lines[2]);
      assertTrue(checks, 'иностранный без поля country — в подписи код страны',
        lines[3].includes('Тессин') && /, DE(\s|$)/.test(lines[3]), lines[3]);
    } catch (e) {
      await shot(page, 'scenario-28-ties');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// Координаты в ТЕКСТЕ подписи: десятичное число или знак градуса. Код страны
// (US, SE) и приписка «ещё N» под это не попадают.
const COORD_IN_CAPTION = /\d+[.,]\d+|°/;

// C6: одноимённые пункты без admin1 различаются; висячих разделителей нет.
async function scenarioSameNames(browser, base) {
  await scenario('32. Одноимённые пункты: без координат в подписи, дедуп, приписка и подсказки, admin1 приоритетнее admin2 (C6)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-samename.json|name=%D0%9C%D0%BE%D1%81',
        FX + 'batch-mixed.json|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('Мос');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const lines = await frame.locator('.city .cn').allInnerTexts().then((a) => a.map((s) => s.replace(/\s+/g, ' ').trim()));
      const titles = await frame.locator('.city').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
      // Дубль геокодера (идентичные координаты) схлопнут: 8 записей → 7 строк.
      assertEq(checks, 'строк результатов (дубль схлопнут)', lines.length, 7);
      assertTrue(checks, 'координат в подписи нет', lines.every((l) => !COORD_IN_CAPTION.test(l)), lines.join(' | '));
      assertTrue(checks, 'нет висячих разделителей', lines.every((l) => !/[·,]\s*$/.test(l) && !l.includes(', ,') && !l.includes('·')), lines.join(' | '));
      const tm = lines.map((l, i) => i).filter((i) => lines[i].startsWith('Москва, Туркменистан'));
      const br = lines.map((l, i) => i).filter((i) => lines[i].startsWith('Москва, Бразилия'));
      assertEq(checks, 'три одноимённые Туркменистана (дубль схлопнут)', tm.length, 3);
      assertEq(checks, 'две одноимённые Бразилии', br.length, 2);
      // Приписка — только у первой строки группы.
      assertEq(checks, 'приписка у первой строки группы TM', lines[tm[0]], 'Москва, Туркменистан ещё 2 с похожим названием');
      assertTrue(checks, 'у остальных TM приписки нет', tm.slice(1).every((i) => lines[i] === 'Москва, Туркменистан'), lines.join(' | '));
      assertEq(checks, 'приписка у первой строки группы BR', lines[br[0]], 'Москва, Бразилия ещё 1 с похожим названием');
      // Подсказка — у КОНКРЕТНОЙ строки: порядок внутри группы известен (при равном
      // населении — по широте), поэтому проверяем пары «строка ↔ её координаты»,
      // а не набор. ≈ 300 м разницы (37,900/58,100 и 37,904/58,104) различимы,
      // знаки S/W выводятся верно, широта и долгота не перепутаны.
      assertEq(checks, 'подсказки TM по строкам', tm.map((i) => titles[i]).join(' | '),
        ['37,5000° N, 58,3700° E', '37,9000° N, 58,1000° E', '37,9040° N, 58,1040° E'].join(' | '));
      assertEq(checks, 'подсказки BR по строкам (южная широта, западная долгота)', br.map((i) => titles[i]).join(' | '),
        ['12,9000° S, 45,1000° W', '12,3456° S, 45,6789° W'].join(' | '));
      assertEq(checks, 'подсказки попарно различны', new Set(titles.filter(Boolean)).size, 5);
      // Регион различает сам: подсказок и приписок у не-групп нет.
      const kir = lines.findIndex((l) => l === 'Москва, Кировский район, Россия');
      assertTrue(checks, 'admin2 вместо пустого admin1', kir >= 0, lines.join(' | '));
      assertEq(checks, 'у строки с регионом нет подсказки', titles[kir], null);
      // Есть и admin1, и admin2: показывается только admin1.
      assertTrue(checks, 'при admin1 и admin2 — только admin1', lines.includes('Москва, Тверская обл., Россия') && !lines.some((l) => l.includes('Московская')), lines.join(' | '));
    } catch (e) {
      await shot(page, 'scenario-32-samename');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C6-д3: дедуп по расстоянию (≤ 50 м), а не по ячейке сетки округления.
async function scenarioDedupDistance(browser, base) {
  await scenario('33. Дедуп по расстоянию: дубль в ≈22 м через границу ячейки склеен, точки в ≈78 м — нет; код страны не координата (C6-д3)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL,
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-samename-dist.json|name=%D0%97%D0%B0',
        FX + 'batch-mixed.json|latitude',
      ].join(','),
    });
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await frame.locator('#q').fill('За');
      await frame.locator('.city').first().waitFor({ timeout: 15000 });
      const lines = await frame.locator('.city .cn').allInnerTexts().then((a) => a.map((s) => s.replace(/\s+/g, ' ').trim()));
      const titles = await frame.locator('.city').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
      const zarya = lines.map((l, i) => i).filter((i) => lines[i].startsWith('Заря'));
      const lug = lines.map((l, i) => i).filter((i) => lines[i].startsWith('Луг'));
      // 55,0004 и 55,0006: ≈ 22 м, но round(…, 3) даёт разные ячейки — раньше не склеивались.
      assertEq(checks, 'дубль в ≈22 м через границу ячейки склеен', zarya.length, 1);
      assertEq(checks, 'осталась первая — с большим населением', titles[zarya[0]], null);
      // 56,00055 и 56,00125: ≈ 78 м, одна ячейка round(…, 3) — раньше сливались.
      assertEq(checks, 'две разные точки в ≈78 м остались разными', lug.length, 2);
      assertEq(checks, 'подсказки Луг по строкам', lug.map((i) => titles[i]).join(' | '),
        ['56,0012° N, 37,5000° E', '56,0005° N, 37,5000° E'].join(' | '));
      assertEq(checks, 'приписка у первой строки Луг', lines[lug[0]], 'Луг, Тверская обл., Россия ещё 1 с похожим названием');
      assertEq(checks, 'у второй строки Луг приписки нет', lines[lug[1]], 'Луг, Тверская обл., Россия');
      // Страна без поля country: в подписи код (US, SE) — это не координаты.
      const sp = lines.map((l, i) => i).filter((i) => lines[i].startsWith('Спрингфилд'));
      assertEq(checks, 'Спрингфилд — две строки с кодом страны', sp.length, 2);
      assertTrue(checks, 'в подписях код страны US и SE', lines.some((l) => /, US(\s|$)/.test(l)) && lines.some((l) => /^Лунд, SE$/.test(l)), lines.join(' | '));
      assertTrue(checks, 'код страны не принят за координаты', lines.every((l) => !COORD_IN_CAPTION.test(l)), lines.join(' | '));
      assertEq(checks, 'подсказки Спрингфилд по строкам', sp.map((i) => titles[i]).join(' | '),
        ['39,8000° N, 89,6000° W', '37,2000° N, 93,3000° W'].join(' | '));
      assertEq(checks, 'всего строк', lines.length, 6);
    } catch (e) {
      await shot(page, 'scenario-33-dedup-distance');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-м: крайние квоты. 5 своих + 5 иностранных: иностранных ровно 2 (отсечение),
// всего 7. 0 своих + 7 иностранных: потолок 7 добирается, порядок по населению.
async function scenarioQuotaEdges(browser, base) {
  await scenario('31. Крайние квоты: 5 своих + 5 иностранных → 5 + 2; 0 своих + 7 иностранных → 7 (C5-м)', async (checks) => {
    const open = async (geo) => {
      const o = await openEmulator(browser, base, {
        grant: GRANT_ALL,
        fixtures: [FX + 'forecast-yaroslavl.json|apparent_temperature', FX + geo + '|name=%D0%A2%D0%B5%D1%81%D1%82', FX + 'batch-seven.json|latitude'].join(','),
      });
      await o.frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await o.frame.locator('#citybtn').click();
      await o.frame.locator('#q').waitFor({ timeout: 5000 });
      await o.frame.locator('#q').fill('Тест');
      await o.frame.locator('.city').first().waitFor({ timeout: 15000 });
      o.lines = await o.frame.locator('.city').allInnerTexts().then((a) => a.map((s) => s.replace(/\n/g, ' ').trim()));
      return o;
    };
    let o = await open('geocode-own5f5.json');
    try {
      const own = o.lines.filter((l) => l.includes('Россия')).length;
      assertEq(checks, '5 своих: свои все пять', own, 5);
      assertEq(checks, '5 своих: иностранных ровно 2', o.lines.length - own, 2);
      assertEq(checks, '5 своих: всего 7', o.lines.length, 7);
      assertTrue(checks, 'иностранные — самые крупные (Тестбург, Тестлунд)',
        o.lines[5].includes('Тестбург') && o.lines[6].includes('Тестлунд'), o.lines.slice(5).join(' | '));
    } catch (e) {
      await shot(o.page, 'scenario-31-own5');
      throw e;
    } finally {
      await o.context.close();
    }
    o = await open('geocode-foreign7.json');
    try {
      assertEq(checks, '0 своих: иностранных ровно 7', o.lines.length, 7);
      assertTrue(checks, '0 своих: ни одного своего', !o.lines.some((l) => l.includes('Россия')), o.lines.join(' | '));
      const names = ['Тестбург', 'Тестлунд', 'Тестхейм', 'Тестополь', 'Тествиль', 'Теставан', 'Тестград'];
      assertTrue(checks, '0 своих: порядок по населению', names.every((n, i) => o.lines[i].includes(n)), o.lines.join(' | '));
    } catch (e) {
      await shot(o.page, 'scenario-31-foreign7');
      throw e;
    } finally {
      await o.context.close();
    }
  });
}

// C5-м: self-restart действительно сбрасывает состояние. storageOk сброшен —
// после restart проба storage.get идёт заново; offlineShown сброшен — после
// restart событие `online` не перезапускает load() и панель показывает обычный
// экран. Дубль подписки `online` — одно событие, один load().
async function scenarioRestartResets(browser, base) {
  await scenario('32. Self-restart сбрасывает storageOk и offlineShown; online обрабатывается один раз (C5-м)', async (checks) => {
    const { context, page, frame } = await openEmulator(browser, base, {
      grant: GRANT_ALL + ',storage',
      fixtures: [
        FX + 'forecast-yaroslavl.json|apparent_temperature',
        FX + 'geocode-arts.json|name=%D0%AF%D1%80%D1%86',
        FX + 'forecast-batch-arts.json',
      ].join(','),
    });
    const logCount = (needle) => page.evaluate((n) =>
      Array.from(document.querySelectorAll('#log p')).filter((p) => p.textContent.includes(n)).length, needle);
    try {
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Проба storage сделана и закэширована: повторное открытие режима её не повторяет.
      await frame.locator('#citybtn').click();
      await frame.locator('.hint').waitFor({ timeout: 8000 });
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      const gets0 = await logCount('→ storage.get');
      await frame.locator('#citybtn').click();
      await frame.locator('.hint').waitFor({ timeout: 8000 });
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      assertEq(checks, 'без restart проба не повторяется (storageOk закэширован)', (await logCount('→ storage.get')) - gets0, 0);
      // Подписки на `online` считаем вызовами addEventListener: слушатель модульного
      // уровня не должен переезжать в startOnce (тогда каждый restart плодил бы подписки).
      await frame.evaluate(() => {
        window.__onlineAdds = 0;
        const orig = window.addEventListener;
        window.addEventListener = function (type) {
          if (type === 'online') window.__onlineAdds++;
          return orig.apply(this, arguments);
        };
      });
      // Три self-restart подряд (кнопка «Обновить данные» живёт на экране сбоя панели,
      // поэтому вызываем тот же обработчик клика через разметку).
      for (let i = 0; i < 3; i++) {
        await frame.evaluate(() => {
          const b = document.createElement('button');
          b.id = 'restart';
          document.getElementById('wx').appendChild(b);
          b.click();
        });
        await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      }
      assertEq(checks, 'restart не добавляет подписок на online', await frame.evaluate(() => window.__onlineAdds), 0);
      // storageOk сброшен: после restart открытие режима снова пробует storage.get.
      const gets1 = await logCount('→ storage.get');
      await frame.locator('#citybtn').click();
      await frame.locator('.hint').waitFor({ timeout: 8000 });
      assertTrue(checks, 'после restart проба storage.get сделана заново', (await logCount('→ storage.get')) > gets1,
        gets1 + ' → ' + (await logCount('→ storage.get')));
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // offlineShown сброшен: уходим в офлайн, чиним панель restart'ом, а затем
      // online не должен перезапускать load() — панель на обычном экране.
      await context.setOffline(true);
      await frame.locator('#refresh').click();
      await frame.locator('#retry').waitFor({ timeout: 5000 });
      // Чтение запомненного города замедляем: пока оно в полёте, приходит `online`.
      // offlineShown сброшен — событие не должно запускать преждевременный load().
      await page.evaluate(() => { window.__emulator.cfg.delay = 1200; });
      const fetches0 = await logCount('→ net.fetch https://api.open-meteo');
      await frame.evaluate(() => {
        const b = document.createElement('button');
        b.id = 'restart';
        document.getElementById('wx').appendChild(b);
        b.click();
      });
      await context.setOffline(false);
      await page.evaluate(() => { window.__emulator.cfg.delay = 0; });
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      // Ждём дольше задержки чтения города (1200 мс): отложенный load() тоже успел бы дать запрос.
      await page.waitForTimeout(2000);
      assertEq(checks, 'online после нескольких restart — ровно один load()',
        (await logCount('→ net.fetch https://api.open-meteo')) - fetches0, 1);
      // Панель на нормальном экране, а не на офлайн-состоянии.
      assertEq(checks, 'офлайн-кнопки нет', await frame.locator('#retry').count(), 0);
      // Теперь online на здоровой панели (offlineShown сброшен успешным load) ничего не делает.
      const fetches1 = await logCount('→ net.fetch https://api.open-meteo');
      await frame.evaluate(() => window.dispatchEvent(new Event('online')));
      await page.waitForTimeout(500);
      assertEq(checks, 'online на обычном экране запросов не порождает', (await logCount('→ net.fetch https://api.open-meteo')) - fetches1, 0);
    } catch (e) {
      await shot(page, 'scenario-32-restart-resets');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д3 (minor): guard `!storageProbing` — два открытия режима выбора подряд ВО
// ВРЕМЯ идущей пробы дают ровно один storage.get. Проба замедляется задержкой
// хоста, выставленной после загрузки главного экрана.
async function scenarioProbeGuard(browser, base) {
  await scenario('29. Два открытия режима во время пробы — ровно один storage.get (C5-д3)', async (checks) => {
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
      const gets = () => page.evaluate(() =>
        Array.from(document.querySelectorAll('#log p')).filter((p) => p.textContent.includes('→ storage.get')).length);
      const before = await gets();
      await page.evaluate(() => { window.__emulator.cfg.delay = 3000; });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      // Главный экран должен вернуться быстро: задержку снимаем до «Назад»,
      // а уже ушедший storage.get остаётся в полёте.
      await page.evaluate(() => { window.__emulator.cfg.delay = 0; });
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      // Дождёмся ответа пробы и подсказки.
      await frame.locator('.hint').waitFor({ timeout: 8000 });
      assertEq(checks, 'storage.get ушёл ровно один раз', (await gets()) - before, 1);
    } catch (e) {
      await shot(page, 'scenario-29-probe-guard');
      throw e;
    } finally {
      await context.close();
    }
  });
}

// C5-д3 (minor): кэш storageOk живёт до перезагрузки страницы, а право storage
// могут отозвать. Подсказка «Город запоминается…» — только если проба успешна И
// право есть сейчас.
async function scenarioHintRevoked(browser, base) {
  await scenario('30. Право storage отозвано после успешной пробы — подсказки нет (C5-д3)', async (checks) => {
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
      await frame.locator('#citybtn').click();
      await frame.locator('.hint').waitFor({ timeout: 8000 });
      assertEq(checks, 'пока право есть — подсказка показана', await frame.locator('.hint').count(), 1);
      // Хост переинициализирует панель без права storage (как после отзыва).
      await page.evaluate(() => {
        const g = window.__emulator.cfg.grant.filter((x) => !x.startsWith('storage'));
        const inner = document.getElementById('frame').contentWindow.frames[0];
        inner.postMessage({ source: 'ccs-host', v: 1, type: 'init', granted: g }, '*');
      });
      await frame.locator('#back').click();
      await frame.locator('.week .day').nth(6).waitFor({ timeout: 15000 });
      await frame.locator('#citybtn').click();
      await frame.locator('#q').waitFor({ timeout: 5000 });
      await page.waitForTimeout(500);
      assertEq(checks, 'право отозвано — подсказки нет', await frame.locator('.hint').count(), 0);
    } catch (e) {
      await shot(page, 'scenario-30-hint-revoked');
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
  await scenarioGeoRightWithPort(browser, base);
  await scenarioOnlineClosesSearch(browser, base);
  await scenarioEmptyTemps(browser, base);
  await scenarioCountrySort(browser, base);
  await scenarioStorageUnavailable(browser, base);
  await scenarioRateLimit(browser, base);
  await scenarioRenderFail(browser, base);
  await scenarioQuotas(browser, base);
  await scenarioAllForeign(browser, base);
  await scenarioZeroTemp(browser, base);
  await scenarioStorageDenied(browser, base);
  await scenarioRetryRequests(browser, base);
  await scenarioProbeRetry(browser, base);
  await scenarioTiesAndCountryFallback(browser, base);
  await scenarioProbeGuard(browser, base);
  await scenarioHintRevoked(browser, base);
  await scenarioQuotaEdges(browser, base);
  await scenarioRestartResets(browser, base);
  await scenarioSameNames(browser, base);
  await scenarioDedupDistance(browser, base);
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
