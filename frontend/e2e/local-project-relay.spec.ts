import { test, expect, type APIRequestContext, type Page, type Route } from '@playwright/test';

// E2E локального проекта с ДРУГОГО устройства (ADR-016 §5, задача 5.2): агента на этом компьютере
// нет, файлы и изменения читаются через ретранслятор сервера api/projects/{id}/relay/…, записи нет.
// Живого устройства в e2e нет, поэтому: проект подменяется в ответе /api/projects на локальный
// (матрица host=device), билет агента указывает на порт, где никто не слушает (так выглядит
// телефон), а сам ретранслятор — фейковый: перехват маршрутов relay/ с ответами той же формы,
// что у FilesController/GitController (контракт держит ProjectRelayContractTests на бэке).

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const RUN = Date.now().toString(36);
const PROJECT_NAME = `E2E ретранслятор ${RUN}`;
// Порт без слушателя: агента на «этом компьютере» нет
const NO_AGENT_PORT = Number(process.env.E2E_AGENT_PORT || 47318) + 2;

const PNG_1X1 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const README = '# README с устройства\n\n![Логотип](logo.png)\n';
const DIFF = [
  'diff --git a/changed.ts b/changed.ts',
  '--- a/changed.ts',
  '+++ b/changed.ts',
  '@@ -1 +1 @@',
  '-const before = 1;',
  '+const relayedLine = 2;',
  '',
].join('\n');

interface Seen { method: string; path: string; query: URLSearchParams; authorization?: string }
type RelayMode = { kind: 'ok' } | { kind: 'offline'; reason: string };

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });
const escapeRe = (v: string) => v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// ---------- подмена проекта, билета и ретранслятора ----------

async function asRemoteLocalProject(page: Page, serverOrigin: string, projectId: string, mode: { current: RelayMode }, seen: Seen[]) {
  await page.route('**/api/projects', async (route: Route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const list = (await response.json()) as Array<Record<string, unknown>>;
    const patched = list.map(p => p.id !== projectId ? p : {
      ...p,
      deviceId: 'fake-device',
      device: { id: 'fake-device', name: 'Домашний ПК', online: true, platform: 'linux', agentVersion: '0.0.0', harnessReady: true, harnessProblem: null },
      capabilities: {
        host: 'device', deviceId: 'fake-device',
        files: { host: 'device', available: true, reason: null, features: ['files', 'diff', 'git', 'fileWatcher', 'terminal', 'devServers', 'skills', 'attachments'] },
        platform: { host: 'server', available: true, reason: null, features: ['chat', 'history', 'tasks', 'memory', 'personas', 'notes', 'costs', 'tts'] },
        serverContent: { host: 'off', available: false, reason: 'Нужен контент проекта на сервере — у локального проекта недоступно', features: ['knowledge', 'codeGraph', 'dossiers', 'docs', 'mapHygiene'] },
        exec: { available: true, reason: null },
      },
    });
    await route.fulfill({ response, json: patched });
  });
  await page.route(`**/api/projects/${projectId}/device-agent/ticket`, (route: Route) =>
    route.fulfill({ json: {
      ticket: 'fake-ticket', deviceId: 'fake-device', expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
      port: NO_AGENT_PORT, header: 'X-Agent-Ticket',
    } }));
  // Прямые серверные маршруты файлов и git у локального проекта трогаться не должны вовсе
  await page.route(new RegExp(`^${escapeRe(serverOrigin)}/api/projects/${projectId}/(files|git|services|preview|launch-config|skills|agents)(/|\\?|$)`), (route: Route) =>
    route.fulfill({ status: 599, json: { error: 'запрос локального проекта ушёл на сервер мимо ретранслятора' } }));

  await page.route(new RegExp(`^${escapeRe(serverOrigin)}/api/projects/${projectId}/relay/`), (route: Route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.replace(new RegExp(`^/api/projects/${projectId}/relay/`), '');
    seen.push({ method: req.method(), path, query: url.searchParams, authorization: req.headers()['authorization'] });
    const json = (status: number, body: unknown) => route.fulfill({ status, json: body });
    if (mode.current.kind === 'offline') return json(409, { error: mode.current.reason, code: 'relay_unavailable' });
    if (req.method() !== 'GET') return json(405, { error: 'в ретрансляторе нет записи' });
    const file = url.searchParams.get('path') ?? '';
    const now = new Date().toISOString();
    switch (path) {
      case 'files':
      case 'files/tree':
        return json(200, [
          { name: 'readme.md', path: 'readme.md', isDirectory: false, size: README.length, modified: now, isModified: false },
          { name: 'logo.png', path: 'logo.png', isDirectory: false, size: PNG_1X1.length, modified: now, isModified: false },
          { name: 'clip.mp4', path: 'clip.mp4', isDirectory: false, size: 64, modified: now, isModified: false },
        ]);
      case 'files/content':
        if (file === 'readme.md') return json(200, { content: README, isBinary: false, isImage: false });
        if (file === 'clip.mp4') return json(200, { content: null, isBinary: true, isImage: false, isVideo: true, mimeType: 'video/mp4', fileSize: 64 });
        if (file === 'changed.ts') return json(200, { content: 'const relayedLine = 2;\n', isBinary: false, isImage: false });
        return json(404, { error: 'Не найдено' });
      case 'files/stream':
        // Тег <img>/<video> заголовков не шлёт: JWT пользователя едет в query, как у сервера
        if (!url.searchParams.get('access_token')) return json(401, { error: 'нет токена' });
        if (file === 'logo.png') return route.fulfill({ status: 200, contentType: 'image/png', body: PNG_1X1 });
        if (file === 'clip.mp4') return route.fulfill({ status: 200, contentType: 'video/mp4', body: Buffer.alloc(64) });
        return json(404, { error: 'Не найдено' });
      case 'files/diff':
        return json(200, { diff: file === 'changed.ts' ? DIFF : null });
      case 'git/status':
        return json(200, {
          isRepo: true, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false, isWorktree: false,
          staged: [], unstaged: [{ path: 'changed.ts', status: 'M', added: 1, deleted: 1 }], untracked: [],
        });
      case 'git/diff':
        return json(200, { diff: file === 'changed.ts' ? DIFF : null });
      case 'git/log':
        return json(200, []);
      default:
        return json(404, { error: 'Не найдено' });
    }
  });
}

async function openProject(page: Page, projectId: string, token: string) {
  await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${projectId}`);
}

async function openTab(page: Page, tab: 'Файлы' | 'Изменения') {
  await page.getByRole('button', { name: new RegExp(`^${tab}(:|$)`) }).first().click();
  // Курсор, оставленный на кнопке рельсы, держит её всплывашку поверх первой строки панели
  await page.mouse.move(640, 650);
}

// Контролы записи, которых с другого устройства быть не должно ни в одной из панелей
const WRITE_CONTROL_NAMES = [
  'Новый', 'Править', 'Сохранить', 'Удалить', 'Откатить изменения', 'Зафиксировать', 'Зафиксировать изменения',
  'Отменить изменения', 'Отменить все изменения', 'Опубликовать', 'Выбрать файлы для коммита', 'Проиндексировать хунк',
  'Подключить git', 'Вернуть эту версию', 'Редактировать',
];

async function expectNoWriteControls(page: Page) {
  for (const name of WRITE_CONTROL_NAMES)
    await expect(page.getByRole('button', { name, exact: true }), `контрол записи «${name}»`).toHaveCount(0);
}

test.describe('локальный проект с другого устройства — только чтение (ADR-016, 5.2)', () => {
  let token: string;
  let projectId: string;
  let origin: string;
  const seen: Seen[] = [];
  const mode: { current: RelayMode } = { current: { kind: 'ok' } };

  test.beforeAll(async ({ playwright, baseURL }) => {
    origin = new URL(baseURL!).origin;
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    const r = await request.post('/api/projects', {
      headers: auth(token),
      data: { name: PROJECT_NAME, rootPath: `/tmp/e2e-relay-${RUN}`, createDirectory: true },
    });
    expect(r.ok(), `проект создаётся: ${r.status()} ${await r.text()}`).toBeTruthy();
    projectId = (await r.json()).id as string;
    await request.dispose();
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    if (projectId) await request.delete(`/api/projects/${projectId}`, { headers: auth(token) });
    await request.dispose();
  });

  test.beforeEach(() => { seen.length = 0; mode.current = { kind: 'ok' }; });
  test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: 'ignoreErrors' }); });

  test('дерево, файл и картинка markdown приходят через ретранслятор, контролов записи нет', async ({ page }) => {
    await asRemoteLocalProject(page, origin, projectId, mode, seen);
    await openProject(page, projectId, token);
    await openTab(page, 'Файлы');
    await expect(page.getByText('readme.md')).toBeVisible();

    // Контекстное меню строки — без переименования, переноса и удаления
    await page.getByText('readme.md').click({ button: 'right' });
    await expect(page.getByRole('button', { name: 'Копировать Markdown' })).toBeVisible();
    for (const name of ['Переименовать', 'Переместить в…', 'Удалить'])
      await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
    // Меню закрывается щелчком мимо
    await page.mouse.click(640, 650);
    await expect(page.getByRole('button', { name: 'Копировать Markdown' })).toHaveCount(0);

    await page.getByText('readme.md').click();
    await expect(page.getByRole('heading', { name: 'README с устройства' })).toBeVisible();
    const img = page.getByRole('img', { name: 'Логотип' });
    await expect(img).toHaveAttribute('src', new RegExp(`^/api/projects/${projectId}/relay/files/stream\\?path=logo\\.png&access_token=`));
    await expect.poll(() => img.evaluate(el => (el as HTMLImageElement).naturalWidth)).toBe(1);
    await expectNoWriteControls(page);

    // Всё чтение — GET под JWT пользователя; агенту ничего не ушло, на прямые маршруты сервера — тоже
    expect(seen.length).toBeGreaterThan(0);
    for (const s of seen) {
      expect(s.method).toBe('GET');
      if (s.path !== 'files/stream') expect(s.authorization).toBe(`Bearer ${token}`);
    }
    expect(seen.map(s => s.path)).toEqual(expect.arrayContaining(['files/content', 'files/stream']));
  });

  test('видео идёт потоком ретранслятора', async ({ page }) => {
    await asRemoteLocalProject(page, origin, projectId, mode, seen);
    await openProject(page, projectId, token);
    await openTab(page, 'Файлы');
    await page.getByText('clip.mp4').click();
    await expect(page.locator('video source')).toHaveAttribute('src', new RegExp(`^/api/projects/${projectId}/relay/files/stream\\?path=clip\\.mp4&access_token=`));
    await expect.poll(() => seen.some(s => s.path === 'files/stream' && s.query.get('path') === 'clip.mp4')).toBe(true);
  });

  test('изменения и diff видны, фиксации, отката и публикации нет', async ({ page }) => {
    await asRemoteLocalProject(page, origin, projectId, mode, seen);
    await openProject(page, projectId, token);
    await openTab(page, 'Изменения');
    const row = page.getByText('changed.ts').first();
    await expect(row).toBeVisible();
    await row.hover();
    await expectNoWriteControls(page);

    await row.click();
    await expect(page.getByText('const relayedLine = 2;').first()).toBeVisible();
    await expectNoWriteControls(page);
    expect(seen.some(s => s.path === 'git/status')).toBe(true);
    expect(seen.some(s => s.path === 'git/diff' || s.path === 'files/diff')).toBe(true);
    for (const s of seen) expect(s.method).toBe('GET');
  });

  test('устройство офлайн — плашка с причиной и повтор, а не ошибка', async ({ page }) => {
    mode.current = { kind: 'offline', reason: 'Устройство офлайн' };
    await asRemoteLocalProject(page, origin, projectId, mode, seen);
    await openProject(page, projectId, token);
    await openTab(page, 'Файлы');

    const state = page.locator('[data-capability-gate="relay"]');
    await expect(state).toBeVisible();
    await expect(state.getByText('Файлы недоступны')).toBeVisible();
    await expect(state.getByText('Устройство офлайн')).toBeVisible();

    // Устройство вернулось — «Проверить снова» открывает дерево без перезагрузки
    mode.current = { kind: 'ok' };
    await state.getByRole('button', { name: 'Проверить снова' }).click();
    await expect(page.getByText('readme.md')).toBeVisible();
    await expect(state).toHaveCount(0);
  });

  test.describe('телефон, 360 CSS-пикселей', () => {
    test.use({ viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true });

    test('дерево и файл читаются, правки нет ни плавающей кнопкой, ни в панели', async ({ page }) => {
      await asRemoteLocalProject(page, origin, projectId, mode, seen);
      await openProject(page, projectId, token);
      await openTab(page, 'Файлы');
      await expect(page.getByText('readme.md')).toBeVisible();
      await expectNoWriteControls(page);

      await page.getByText('readme.md').click();
      await expect(page.getByRole('heading', { name: 'README с устройства' })).toBeVisible();
      await expect.poll(() => page.getByRole('img', { name: 'Логотип' }).evaluate(el => (el as HTMLImageElement).naturalWidth)).toBe(1);
      await expectNoWriteControls(page);
      expect(await page.evaluate(() => document.documentElement.scrollWidth), 'страница не шире экрана').toBeLessThanOrEqual(360);

      for (const s of seen) expect(s.method).toBe('GET');
    });

    test('изменения читаются, действий записи нет', async ({ page }) => {
      await asRemoteLocalProject(page, origin, projectId, mode, seen);
      await openProject(page, projectId, token);
      await openTab(page, 'Изменения');
      await expect(page.getByText('changed.ts').first()).toBeVisible();
      await expectNoWriteControls(page);
      for (const s of seen) expect(s.method).toBe('GET');
    });
  });
});
