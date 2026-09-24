import { test, expect, type APIRequestContext, type Page, type Route } from '@playwright/test';

// E2E локального проекта (ADR-016 §3.4): флаг `local-projects` включён,
// диалог создания проекта показывает сегмент «Локальный», выбор устройства
// фильтрует по capabilities.exec. UI-уровневый smoke — реальное устройство
// через бэк не проверяем (требует живого агента).

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
// Уникальный суффикс, чтобы прогоны не сталкивались и легко было чистить
const RUN = Date.now().toString(36);
const PROJECT_NAME = `E2E локальный ${RUN}`;

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

// Фич-флаг меняется PUT /api/feature-flags/{key}; ответ проверяем — иначе опечатка в
// маршруте молча оставит флаг выключенным. Возвращает значение ДО правки, чтобы вернуть
// инстанс как было. Токен — из beforeAll: вход под rate-limit, лишний логин его выжигает
async function setFlag(request: APIRequestContext, token: string, key: string, value: boolean): Promise<boolean> {
  const before = await request.get('/api/feature-flags', { headers: auth(token) });
  expect(before.ok(), `флаги читаются: ${before.status()}`).toBeTruthy();
  const was = Boolean((await before.json()).values?.[key]);
  const r = await request.put(`/api/feature-flags/${key}`, { headers: auth(token), data: { enabled: value } });
  expect(r.ok(), `флаг ${key} переключается: ${r.status()} ${await r.text()}`).toBeTruthy();
  return was;
}

// У Modal нет role="dialog" — карточку модалки находим по её заголовку
const modal = (page: Page, title: string) =>
  page.locator('.cc-modal-card').filter({ has: page.getByRole('heading', { name: title }) });

test.describe('локальный проект (ADR-016 §3.4)', () => {
  let token: string;
  let flagWas = false;

  // Логин ровно один на спек: окно auth-login — 10 входов в минуту на IP, а спеки
  // локального проекта гоняются подряд и по нескольку раз
  test.beforeAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    flagWas = await setFlag(request, token, 'local-projects', true);
    await request.dispose();
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    if (!token) return;
    const request = await playwright.request.newContext({ baseURL });
    const list = (await (await request.get('/api/projects', { headers: auth(token) })).json()) as Array<{ id: string; name: string }>;
    for (const p of list.filter((x) => x.name === PROJECT_NAME))
      await request.delete(`/api/projects/${p.id}`, { headers: auth(token) });
    // Возвращаем флаг как было до спека
    await setFlag(request, token, 'local-projects', flagWas);
    await request.dispose();
  });

  test('диалог создания показывает сегмент «Локальный» под флагом', async ({ page, context }) => {
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/projects');
    await expect(page.getByRole('button', { name: 'Добавить проект' }).first()).toBeVisible();
    // Дать флагам и проектам подгрузиться
    await page.waitForTimeout(1500);

    // Открываем диалог создания
    await page.getByRole('button', { name: 'Добавить проект' }).first().click();
    const dialog = modal(page, 'Добавить проект');
    await expect(dialog).toBeVisible();
    // Сегмент «Новый / Существующий» уже был до фичи — без него никуда
    await expect(dialog.getByRole('button', { name: 'Новый' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Существующий' })).toBeVisible();
    // Новый сегмент «Серверный / Локальный» — под флагом local-projects
    await expect(dialog.getByRole('button', { name: 'Локальный' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Серверный' })).toBeVisible();
  });

  test('без устройства с capabilities.exec плашка «нет устройств»', async ({ page, context }) => {
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/projects');
    await page.waitForTimeout(1500);

    await page.getByRole('button', { name: 'Добавить проект' }).first().click();
    const dialog = modal(page, 'Добавить проект');
    await dialog.getByRole('button', { name: 'Локальный' }).click();

    // Подсказка: либо список устройств, либо empty state, если их нет.
    // В чистом dev-инстансе устройств обычно нет — empty state должен быть виден
    const emptyOrList = dialog.getByText(/Нет устройств|Сопрягите устройство|Выберите устройство/);
    await expect(emptyOrList).toBeVisible({ timeout: 10000 });

    // Кнопка «Создать» disabled, пока не выбрано устройство и не указан путь
    const submit = dialog.getByRole('button', { name: /Создать|Добавить/ });
    await expect(submit).toBeDisabled();
  });
});

// Локальный проект с офлайн-устройством (ADR-016 §3.4, дизайн-ревью 4.7 S3/S4). Живого
// устройства в e2e нет, поэтому серверный проект подменяется в ответе /api/projects на
// локальный: матрица host=device, файлы и ход недоступны, серверного контента нет.
// Сравнение «локальный против серверного» идёт на одном и том же проекте — без подмены
// он остаётся серверным
const OFFLINE = 'Устройство офлайн';
const OFFLINE_NAME = `E2E офлайн ${RUN}`;

async function asOfflineLocalProject(page: Page, projectId: string) {
  await page.route('**/api/projects', async (route: Route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const list = (await response.json()) as Array<Record<string, unknown>>;
    const patched = list.map(p => p.id !== projectId ? p : {
      ...p,
      deviceId: 'fake-device',
      device: { id: 'fake-device', name: 'Домашний ПК', online: false, platform: 'linux', agentVersion: '0.0.0', harnessReady: true, harnessProblem: null },
      capabilities: {
        host: 'device', deviceId: 'fake-device',
        files: { host: 'device', available: false, reason: OFFLINE, features: ['files', 'diff', 'git', 'fileWatcher', 'terminal', 'devServers', 'skills', 'attachments'] },
        platform: { host: 'server', available: true, reason: null, features: ['chat', 'history', 'tasks', 'memory', 'personas', 'notes', 'costs', 'tts'] },
        serverContent: { host: 'off', available: false, reason: 'Нужен контент проекта на сервере — у локального проекта недоступно', features: ['knowledge', 'codeGraph', 'dossiers', 'docs', 'mapHygiene'] },
        exec: { available: false, reason: OFFLINE },
      },
    });
    await route.fulfill({ response, json: patched });
  });
}

test.describe('локальный проект с офлайн-устройством (ADR-016, 4.7 S3/S4)', () => {
  let token: string;
  let projectId: string;
  let sessionId: string;
  let flagsWere: Record<string, boolean> = {};

  test.beforeAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    // Карта проекта — серверный контент под своим флагом: без него секции нет ни у кого
    flagsWere = {
      'local-projects': await setFlag(request, token, 'local-projects', true),
      'project-map-hygiene': await setFlag(request, token, 'project-map-hygiene', true),
    };
    const r = await request.post('/api/projects', {
      headers: auth(token),
      data: { name: OFFLINE_NAME, rootPath: `/tmp/e2e-offline-${RUN}`, createDirectory: true },
    });
    expect(r.ok(), `проект создаётся: ${r.status()} ${await r.text()}`).toBeTruthy();
    projectId = (await r.json()).id as string;
    const s = await request.post(`/api/projects/${projectId}/sessions`, { headers: auth(token), data: { mode: 'acceptEdits' } });
    expect(s.ok(), `чат создаётся: ${s.status()} ${await s.text()}`).toBeTruthy();
    sessionId = (await s.json()).id as string;
    await request.dispose();
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    if (!token) return;
    const request = await playwright.request.newContext({ baseURL });
    if (projectId) await request.delete(`/api/projects/${projectId}`, { headers: auth(token) });
    for (const [key, was] of Object.entries(flagsWere)) await setFlag(request, token, key, was);
    await request.dispose();
  });

  test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: 'ignoreErrors' }); });

  const openChat = async (page: Page) => {
    await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/project/${projectId}/chat/${sessionId}`);
    await expect(page.locator('textarea').first()).toBeVisible();
  };

  const openEditDialog = async (page: Page) => {
    await page.getByRole('button', { name: 'Настройки проекта' }).first().click();
    const dialog = modal(page, 'Редактировать проект');
    await expect(dialog).toBeVisible();
    return dialog;
  };

  test('диалог правки: у локального нет секции git и карты проекта, у серверного есть', async ({ page }) => {
    // Тот же проект без подмены — серверный: обе секции на месте
    await openChat(page);
    let dialog = await openEditDialog(page);
    await expect(dialog.getByText('История файлов (Git)')).toBeVisible();
    await expect(dialog.getByText('Карта проекта (CLAUDE.md)')).toBeVisible();
    await dialog.getByRole('button', { name: 'Отмена' }).click();

    await asOfflineLocalProject(page, projectId);
    await page.reload();
    await expect(page.locator('textarea').first()).toBeVisible();
    dialog = await openEditDialog(page);
    // Секция устройства говорит, что проект локальный — значит, подмена доехала до диалога
    await expect(dialog.getByText('Домашний ПК').first()).toBeVisible();
    await expect(dialog.getByText('История файлов (Git)')).toHaveCount(0);
    await expect(dialog.getByText('Карта проекта (CLAUDE.md)')).toHaveCount(0);
  });

  test('композер: кнопка отправки недоступна, Enter не шлёт, баннер говорит, что делать', async ({ page }) => {
    await asOfflineLocalProject(page, projectId);
    await openChat(page);

    const gate = page.locator('[data-composer-exec-gate]');
    await expect(gate).toContainText('Сообщение не отправится, пока устройство не в сети');
    await expect(gate).toContainText('Задачи и отложенные сообщения дождутся устройства сами');

    const box = page.locator('textarea').first();
    const text = `не должно уйти ${RUN}`;
    await box.fill(text);
    const send = page.locator('[data-composer-send]');
    await expect(send).toBeDisabled();
    await expect(send).toHaveAttribute('title', new RegExp(OFFLINE));

    await box.press('Enter');
    // Ушедшее сообщение очищает поле и оседает в истории чата на сервере — ни того, ни другого
    await page.waitForTimeout(1500);
    await expect(box).toHaveValue(text);
    const history = await page.request.get(`/api/projects/${projectId}/sessions/${sessionId}/history`, { headers: auth(token) });
    expect(history.ok(), `история читается: ${history.status()}`).toBeTruthy();
    expect(JSON.stringify(await history.json())).not.toContain(text);
  });

  test('шапка чата: бейдж «на устройстве · имя · офлайн»', async ({ page }) => {
    await asOfflineLocalProject(page, projectId);
    await openChat(page);
    await expect(page.locator('[data-project-device-badge]')).toHaveText('на устройстве · Домашний ПК · офлайн');
  });

  test.describe('телефон, 360 CSS-пикселей', () => {
    test.use({ viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true });

    test('вкладка «Файлы» не пропадает и показывает причину, бейдж виден в шапке чата', async ({ page }) => {
      await asOfflineLocalProject(page, projectId);
      await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
      await page.goto(`/#/project/${projectId}`);
      // Вкладка может уехать в «⋯», если не влезла по ширине — ищем там же
      // Вкладка на месте: раньше у офлайн-устройства она пропадала молча
      await page.getByRole('button', { name: /^Файлы(:|$)/ }).filter({ visible: true }).first().click();
      const gate = page.locator(`[data-capability-gate="files"]`);
      await expect(gate).toBeVisible();
      await expect(gate.getByText(OFFLINE)).toBeVisible();

      // Смена одного hash страницу не перезагружает, а диплинк на чат читается при загрузке
      await page.goto(`/#/project/${projectId}/chat/${sessionId}`);
      await page.reload();
      await expect(page.locator('[data-project-device-badge]')).toHaveText('устройство офлайн');
      // При пустом поле на месте отправки кнопка голоса — вводим текст
      await page.locator('textarea').first().fill('проверка');
      await expect(page.locator('[data-composer-send]')).toBeDisabled();
      expect(await page.evaluate(() => document.documentElement.scrollWidth), 'страница не шире экрана').toBeLessThanOrEqual(360);
    });
  });
});

// Хелпер для будущих расширений теста, когда в e2e-инстансе появится живой агент
export async function expectOfflineBanner(page: Page) {
  // Баннер «устройство офлайн» в композере — data-capability-gate селектор.
  // Проверка на стороне фронта через атрибут, без зависимости от текста
  await expect(page.locator('[data-composer-exec-gate]')).toBeVisible({ timeout: 5000 });
}
