import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, seed, type Api } from './helpers';
import { existsSync, readFileSync, rmSync } from 'node:fs';

// QA · ночной разбор: итог ночи с фактическими входными токенами (доработки 05.11).
// Стенд из worktree вручную (:5560); локальный движок — 127.0.0.1:18020 (qwen3.8-27b).
// Реплику человека сеем файлом истории: ход агента на локальной модели длится минуты и
// занимает движок, а ночь чат с идущим ходом не берёт.

const PREFIX = 'qa-wikin-';
const DATA = '/home/an/Sources/ClaudeCodeServer-wiki-create/data-qa';

test.describe.configure({ mode: 'serial' });
test.setTimeout(480_000);

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;
let chatId: string;
let ownerId: string;

// реплика человека в чат: ждём, пока сервер её увидит (messageCount)
async function sendMessage(page: any, chatId: string, text: string): Promise<boolean> {
  for (let attempt = 0; attempt < 3; attempt++) {
    await page.goto(`/#/project/${projectId}/chat/${chatId}`);
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(4000);
    const box = page.locator('textarea:visible').first();
    await box.waitFor({ state: 'visible', timeout: 20000 });
    await box.click();
    await box.fill(text);
    await page.keyboard.press('Enter');
    for (let i = 0; i < 20; i++) {
      const list = await client.get<Array<{ id: string; messageCount: number }>>(`/api/projects/${projectId}/sessions`);
      const s = list.find((x: any) => x.id === chatId);
      if (s && s.messageCount > 0) return true;
      await page.waitForTimeout(1500);
    }
  }
  return false;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);

  const me = await client.get<{ userId: string }>('/api/auth/me');
  ownerId = me.userId;

  const stamp = Date.now();
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = project.id;
  chatId = (await seeded.session(projectId, `${PREFIX}chat-${stamp}`)).id;

  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await client.post('/api/wiki/initialize', {}).catch(() => undefined);
  await client.put('/api/me/model-tiers', { strong: 'qwen3.8-27b', medium: 'qwen3.8-27b', weak: 'qwen3.8-27b' }).catch(() => undefined);

  // реплика человека: длинная (ступень 0 отбора требует диалог длиннее 1500 символов)
  const text =
    'Команда QA в этом проекте называется «Кедровая стража», её глава — Вера. ' +
    'Релиз «Кедр» готовили к ноябрю: проверяли на трёх ширинах — 360, 1280 и 1920 пикселя. ' +
    'На мобильном портрете ряд кнопок в шапке чата сжимается в одну кнопку-меню, и подписи у неё не обрезаются. ' +
    'На широком экране ряд остаётся, и каждая кнопка называется своим словом, а не иконкой без подписи. ' +
    'Отдельно следили за мелкими целями: кнопка меньше сорока пикселей считается дефектом. ' +
    'И ещё за переполнением по горизонтали: документ не должен ехать вбок ни на одной ширине. ' +
    'Тёмная тема проверялась отдельно от светлой — цвета берутся из токенов, а не пишутся руками. ' +
    'Итог по проекту «Кедр»: приёмка сдана с замечаниями, критичных дефектов нет. ' +
    'Кедровая стража доводит каждое замечание до конца и не закрывает карточку голословно. ' +
    'Правило простое: шаги воспроизведения записаны, ожидаемый результат записан тоже, ' +
    'а то, что проверить не удалось, отмечено отдельно — пустое лучше правдоподобного. ' +
    'Стенд приёмки поднимается из рабочего дерева задачи, данные для него лежат в своём каталоге, ' +
    'чтобы боевая папка не страдала, а каждый прогон начинается с чистого листа. ' +
    'Учётная запись на стенде своя, пароль админа на свежем стенде случайный, поэтому его берут из лога, ' +
    'а не из памяти. Токен получает сам спек через helper, руками в браузере ничего не добывают. ' +
    'Ошибки консоли и упавшие запросы собираются отдельно, но шум модулей федерации на стенде завышает счётчики, ' +
    'поэтому смотрят только на мелкие цели, переполнение и обрезанный текст. ' +
    'Каждый найденный дефект оформляется карточкой: шаги воспроизведения, ожидаемый результат, ' +
    'фактический результат и скриншот. Дефекты вне текущей задачи заводятся отдельными карточками, ' +
    'а не абзацем внутри отчёта — иначе их теряют. ' +
    'Итог приёмки — таблица «сценарий — ок или дефект», и вердикт ставится явно: принято, принято с замечаниями, ' +
    'возврат в доработку или отозвано. Без вердикта карточка фичи не показывается.';
});

test.afterAll(async () => {
  try { await client.put('/api/wiki/settings', { nightly: { enabled: false } }); } catch { /* best-effort */ }
  await seeded?.cleanup();
  await request?.dispose();
});

test('ночь: итог ночи с непустыми входными токенами', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  // реплика человека: ночью есть что разбирать (длинный диалог — иначе отбор отсечёт чат)
  const text =
    'Команда QA в этом проекте называется «Кедровая стража», её глава — Вера. ' +
    'Релиз «Кедр» готовили к ноябрю: проверяли на трёх ширинах — 360, 1280 и 1920 пикселя. ' +
    'На мобильном портрете ряд кнопок в шапке чата сжимается в одну кнопку-меню, и подписи у неё не обрезаются. ' +
    'На широком экране ряд остаётся, и каждая кнопка называется своим словом, а не иконкой без подписи. ' +
    'Отдельно следили за мелкими целями: кнопка меньше сорока пикселей считается дефектом. ' +
    'И ещё за переполнением по горизонтали: документ не должен ехать вбок ни на одной ширине. ' +
    'Тёмная тема проверялась отдельно от светлой — цвета берутся из токенов, а не пишутся руками. ' +
    'Итог по проекту «Кедр»: приёмка сдана с замечаниями, критичных дефектов нет. ' +
    'Кедровая стража доводит каждое замечание до конца и не закрывает карточку голословно. ' +
    'Правило простое: шаги воспроизведения записаны, ожидаемый результат записан тоже, ' +
    'а то, что проверить не удалось, отмечено отдельно — пустое лучше правдоподобного. ' +
    'Стенд приёмки поднимается из рабочего дерева задачи, данные для него лежат в своём каталоге, ' +
    'чтобы боевая папка не страдала, а каждый прогон начинается с чистого листа. ' +
    'Учётная запись на стенде своя, пароль админа на свежем стенде случайный, поэтому его берут из лога, ' +
    'а не из памяти. Токен получает сам спек через helper, руками в браузере ничего не добывают. ' +
    'Ошибки консоли и упавшие запросы собираются отдельно, но шум модулей федерации на стенде завышает счётчики, ' +
    'поэтому смотрят только на мелкие цели, переполнение и обрезанный текст. ' +
    'Каждый найденный дефект оформляется карточкой: шаги воспроизведения, ожидаемый результат, ' +
    'фактический результат и скриншот. Дефекты вне текущей задачи заводятся отдельными карточками, ' +
    'а не абзацем внутри отчёта — иначе их теряют. ' +
    'Итог приёмки — таблица «сценарий — ок или дефект», и вердикт ставится явно: принято, принято с замечаниями, ' +
    'возврат в доработку или отозвано. Без вердикта карточка фичи не показывается.';
  const sent = await sendMessage(page, chatId, text);
  test.info().annotations.push({ type: 'note', description: `реплика отправлена: ${sent}` });
  expect(sent, 'реплика человека ушла в чат').toBeTruthy();

  const nightFile = `${DATA}/wiki/${ownerId}/keeper/nightly-last.json`;
  await client.put('/api/wiki/settings', { nightly: { enabled: true, windowStartHour: 0, windowEndHour: 23 } });
  // прошлая ночь закрыла дату — её итог убираем, чтобы ночь прошла заново.
  // Если файла нет — проход, скорее всего, идёт сейчас: не трогаем, ждём его итог.
  if (existsSync(nightFile)) {
    try {
      const prev = JSON.parse(readFileSync(nightFile, 'utf-8'));
      if (prev && (prev.selected ?? 0) === 0) rmSync(nightFile);
    } catch { /* файл пишется */ }
  }

  // ночь идёт в два такта: первый отбирает (ступени 0-2), второй разбирает отобранное,
  // поэтому итог с selected=0 — это «ещё не разбирали»: убираем его, чтобы прошёл следующий такт
  let night: any = null;
  const deadline = Date.now() + 400_000;
  while (Date.now() < deadline) {
    if (existsSync(nightFile)) {
      try {
        night = JSON.parse(readFileSync(nightFile, 'utf-8'));
        if (night && night.date && (night.selected ?? 0) > 0) break;
        if (night && night.date) { rmSync(nightFile); night = null; }
      } catch { /* файл пишется */ }
    }
    await page.waitForTimeout(4000);
  }
  test.info().annotations.push({ type: 'note', description: `итог ночи: ${JSON.stringify(night)}` });
  expect(night, 'итог ночи записан').not.toBeNull();
  expect(night.selected ?? 0, 'ночь отобрала чаты').toBeGreaterThan(0);
  expect(night.analyzed ?? 0, 'ночь разобрала чаты').toBeGreaterThan(0);
  expect(night.inputTokens ?? 0, 'входные токены ночи не 0').toBeGreaterThan(0);

  // тот же итог виден в настройках вики
  const settings = await client.get<{ lastNight: any }>('/api/wiki/settings');
  test.info().annotations.push({ type: 'note', description: `lastNight из API: ${JSON.stringify(settings.lastNight)}` });
  expect(settings.lastNight?.inputTokens ?? 0, 'в API итог ночи тоже с токенами').toBeGreaterThan(0);
});
