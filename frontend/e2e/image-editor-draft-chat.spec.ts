import { test, expect, type APIRequestContext, type Page, type WebSocketRoute } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Чат у новой картинки («Нарисовать картинку»): чат виден сразу, первое сообщение создаёт
// чат-черновик по папке, агент запускает генерацию, «Применить» привязывает чат к файлу,
// в списке чатов — миниатюра. Стенд — на ВРЕМЕННОЙ data, создание чата и привязка живые.
// Ход агента подставляется моком: отправка сообщения в хаб перехватывается (настоящего хода
// нет), события tool_use/tool_result и image_edit_* вкладываются в WebSocket хаба. Ручки
// поставщика (каталог, котировка, задачи, варианты, запись файла) — мок HTTP: драйверов на
// тестовой data нет. Мок записи кладёт файл в проект и переводит чат живой ручкой path.
//
//   IE_PROJECT_ROOT=/tmp/iedraft/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5099 \
//     IE_SHOTS_DIR=../.cc-attachments/image-editor-draft-chat npx playwright test e2e/image-editor-draft-chat.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.IE_PROJECT_ROOT || '';
const SHOTS = process.env.IE_SHOTS_DIR || '';
const FOLDER = 'images';
const NOTE = 'описание.txt';
const MODEL = 'fal-ai/flux/dev';
const RS = '\u001e';
// Непрозрачный PNG 1×1: вариант задачи и сохранённый файл
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

interface ChatSession { id: string; name: string; imageChat?: { currentPath: string | null; draftFolder?: string | null } | null }

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

async function ensureProject(request: APIRequestContext, token: string): Promise<{ id: string; name: string }> {
  const headers = { Authorization: `Bearer ${token}` };
  await request.put('/api/feature-flags/image-editor', { headers, data: { enabled: true } });
  const list = (await (await request.get('/api/projects', { headers })).json()) as { id: string; name: string; rootPath: string }[];
  const found = list.find(p => p.rootPath === ROOT);
  if (found) return found;
  const r = await request.post('/api/projects', { headers, data: { name: 'Сайт студии', rootPath: ROOT } });
  expect(r.ok(), `проект должен создаться: ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as { id: string; name: string };
}

// Поставщик и запись файла — мок. Сохранение кладёт PNG в проект и переводит чат на файл
// живой ручкой (так же, как это делает сервер при записи с chatSessionId)
async function mockProvider(page: Page, token: string, projectId: string, baseURL: string) {
  const saves: Record<string, unknown>[] = [];
  // Задача рисуется, пока тест не объявит её готовой
  const done = new Set<string>();
  const job = (jobId: string, status: string, extra: Record<string, unknown> = {}) => ({
    jobId, projectId, status, provider: 'fal', model: MODEL, variants: [], createdAt: new Date().toISOString(), ...extra,
  });
  await page.route('**/api/projects/*/image-editor/catalog', r => r.fulfill({ json: {
    default: { provider: 'fal', model: 'auto' },
    providers: [{ key: 'fal', label: 'fal.ai', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: MODEL, label: 'FLUX dev', caps: { ops: ['generate'], mask: 'none', maxReferences: 4, maxCount: 4, faceByReferences: false }, priceHint: { amount: 0.03, unit: 'usd', per: 'image' } },
    ] }],
    limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
  } }));
  await page.route('**/api/projects/*/image-editor/quote', async r => {
    const req = r.request().postDataJSON() as { count: number };
    await r.fulfill({ json: {
      quoteId: `q-${req.count}`, provider: 'fal', model: MODEL,
      estimate: { amount: 0.03 * req.count, unit: 'usd', approx: true, source: 'catalog' },
      expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 20,
    } });
  });
  await page.route(/\/image-editor\/jobs\/[^/]+\/variants\/\d+/, r => r.fulfill({ contentType: 'image/png', body: PNG }));
  await page.route(/\/image-editor\/jobs\/[^/?]+$/, async r => {
    const id = decodeURIComponent(r.request().url().split('/').pop()!);
    await r.fulfill({ json: done.has(id)
      ? job(id, 'completed', { outcome: 'ok', charged: true, variants: [0, 1], cost: { amount: 0.06, unit: 'usd' } })
      : job(id, 'running') });
  });
  await page.route('**/api/projects/*/image-editor/save', async r => {
    const req = r.request().postDataJSON() as { folder?: string; fileName?: string; chatSessionId?: string | null };
    saves.push(req);
    const rel = `${req.folder ? `${req.folder}/` : ''}${req.fileName}`;
    fs.writeFileSync(path.join(ROOT, rel), PNG);
    if (req.chatSessionId) {
      const moved = await page.request.put(`${baseURL}/api/projects/${projectId}/image-editor/chats/${req.chatSessionId}/path`,
        { headers: { Authorization: `Bearer ${token}` }, data: { path: rel } });
      expect(moved.ok(), `чат должен перейти на файл: ${moved.status()} ${await moved.text()}`).toBeTruthy();
    }
    await r.fulfill({ json: { path: rel } });
  });
  return { saves, finish: (jobId: string) => done.add(jobId) };
}

// WebSocket хаба: ходим к живому серверу, отправку сообщения чата картинки перехватываем
// (ход агента подставляет тест) и умеем вложить своё сообщение message
async function hubMock(page: Page) {
  let route: WebSocketRoute | null = null;
  const sent: unknown[][] = [];
  await page.routeWebSocket(/\/hubs\/session/, ws => {
    route = ws;
    const server = ws.connectToServer();
    ws.onMessage(m => {
      if (typeof m === 'string' && m.includes('"SendImageChatMessage"')) {
        const kept: string[] = [];
        for (const rec of m.split(RS).filter(Boolean)) {
          const inv = JSON.parse(rec) as { target?: string; invocationId?: string; arguments?: unknown[] };
          if (inv.target !== 'SendImageChatMessage') { kept.push(rec); continue; }
          sent.push(inv.arguments ?? []);
          ws.send(JSON.stringify({ type: 3, invocationId: inv.invocationId, result: 'started' }) + RS);
        }
        if (kept.length) server.send(kept.map(x => x + RS).join(''));
        return;
      }
      server.send(m);
    });
    server.onMessage(m => ws.send(m));
  });
  const inject = async (msg: Record<string, unknown>) => {
    await expect.poll(() => route !== null, { timeout: 15_000 }).toBe(true);
    route!.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + RS);
  };
  return { inject, sent };
}

async function openDraft(page: Page, projectId: string, token: string) {
  await page.addInitScript(tk => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.removeItem('cc-image-editor-mock');
  }, token);
  // Открытый файл папки раскрывает её в дереве файлов проекта
  await page.goto(`/#/project/${projectId}/file/${encodeURIComponent(`${FOLDER}/${NOTE}`)}`);
  await page.getByRole('button', { name: 'Файлы', exact: true }).click();
  await page.getByText(FOLDER, { exact: true }).first().click({ button: 'right' });
  await page.getByText('Нарисовать картинку', { exact: true }).click();
  await expect(page.getByRole('button', { name: 'К файлам' })).toBeVisible();
}

async function chatsOf(request: APIRequestContext, token: string, projectId: string): Promise<ChatSession[]> {
  const r = await request.get(`/api/projects/${projectId}/sessions`, { headers: { Authorization: `Bearer ${token}` } });
  return r.ok() ? ((await r.json()) as ChatSession[]) : [];
}

test.beforeAll(() => {
  expect(ROOT, 'нужен IE_PROJECT_ROOT — папка тестового проекта на временной data').not.toBe('');
  fs.mkdirSync(path.join(ROOT, FOLDER), { recursive: true });
  fs.writeFileSync(path.join(ROOT, FOLDER, NOTE), 'Картинки сайта студии');
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
});

for (const vp of [{ w: 1440, h: 900 }, { w: 390, h: 844 }]) {
  test(`${vp.w}: чат новой картинки — черновик, генерация агентом, «Применить» привязывает к файлу`, async ({ page, playwright, baseURL }) => {
    const mobile = vp.w < 800;
    const request = await playwright.request.newContext({ baseURL });
    const token = await login(request);
    const project = await ensureProject(request, token);
    fs.rmSync(path.join(ROOT, FOLDER, 'новая-картинка.png'), { force: true });
    const { saves, finish } = await mockProvider(page, token, project.id, baseURL!);
    const { inject, sent } = await hubMock(page);

    // Дерево файлов открываем на широком экране, редактор затем живёт в нужной ширине
    await page.setViewportSize({ width: 1440, height: 900 });
    await openDraft(page, project.id, token);
    await page.setViewportSize({ width: vp.w, height: vp.h });
    await expect(page.getByText('Новая картинка', { exact: true })).toBeVisible();
    if (mobile) await page.getByRole('button', { name: 'Чат', exact: true }).click();

    // Чат виден сразу: подсказка черновика и две подсказки-примера, заглушки нет
    const chatBox = page.locator('[data-image-chat]').last();
    const lead = chatBox.locator('[data-image-chat-draft]');
    await expect(lead).toContainText('Опишите, что нарисовать, — Claude подберёт промпт и сам запустит генерацию');
    await expect(page.getByText('Чат картинки появится')).toHaveCount(0);
    await expect(chatBox.getByText('Нарисуй уютную кофейню на закате, акварель')).toBeVisible();
    await expect(chatBox.getByText('Придумай обложку для поста о походе в горы')).toBeVisible();
    if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `draft-empty-${vp.w}.png`) });

    // Первое сообщение создаёт чат-черновик по папке; снимка холста нет — картинки ещё нет
    const before = new Set((await chatsOf(request, token, project.id)).map(c => c.id));
    const ask = 'Нарисуй рыжего кота на подоконнике, акварель';
    await chatBox.locator('textarea').first().fill(ask);
    // На телефоне Enter — новая строка: отправка кнопкой
    await chatBox.getByRole('button', { name: /^Отправить/ }).first().click();
    await expect.poll(() => sent.length, { timeout: 15_000 }).toBe(1);
    expect(sent[0][1]).toBe(ask);
    expect(sent[0][2]).toEqual([]);
    expect(sent[0][4]).toBeNull();
    let draft: ChatSession | undefined;
    await expect.poll(async () => {
      draft = (await chatsOf(request, token, project.id)).find(c => !before.has(c.id) && c.imageChat);
      return draft?.imageChat ?? null;
    }, { timeout: 10_000 }).toMatchObject({ currentPath: null, draftFolder: FOLDER });
    const sid = draft!.id;
    await expect(chatBox.getByText(ask).first()).toBeVisible();

    // Агент подбирает промпт и запускает генерацию: карточка «Запущена генерация», варианты в редакторе
    const agentPrompt = 'Рыжий кот дремлет на подоконнике, мягкий утренний свет, акварель';
    await inject({ type: 'tool_use', sessionId: sid, id: 'toolu_gen', name: 'mcp__image-editor__image_generate', input: { prompt: agentPrompt, count: 2 } });
    await inject({ type: 'tool_result', sessionId: sid, toolUseId: 'toolu_gen', isError: false, content: JSON.stringify({
      jobId: 'job-draft',
      quote: { provider: 'fal', model: MODEL, estimate: { amount: 0.06, unit: 'usd', approx: true, source: 'catalog' }, expectedSeconds: 20 },
      changes: [],
    }) });
    await inject({ type: 'image_edit_progress', jobId: 'job-draft', projectId: project.id, stage: 'running', chatSessionId: sid, initiator: 'agent' });
    if (!mobile) {
      await expect(chatBox.locator('[data-image-card]').first()).toContainText('Запущена генерация');
      await expect(page.getByText(/^Рисуем \d вариант/).first()).toBeVisible();
      if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `draft-agent-launch-${vp.w}.png`) });
    } else {
      await expect(chatBox.locator('[data-image-card]').first()).toContainText('Запущена генерация');
      if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `draft-agent-launch-${vp.w}.png`) });
      await page.keyboard.press('Escape');
      await expect(page.getByText(/^Рисуем \d вариант/).first()).toBeVisible();
    }
    finish('job-draft');
    await inject({ type: 'image_edit_completed', jobId: 'job-draft', projectId: project.id, variants: [0, 1], cost: { amount: 0.06, unit: 'usd' }, chatSessionId: sid, initiator: 'agent' });
    const apply = page.getByRole('button', { name: /^Применить/ }).first();
    await expect(apply).toBeVisible({ timeout: 15_000 });
    if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `draft-variants-${vp.w}.png`) });

    // «Применить»: запись уходит с chatSessionId, чат переходит на файл, в шапке — имя файла
    await apply.click();
    await expect.poll(() => saves.length).toBe(1);
    expect(saves[0]).toMatchObject({ chatSessionId: sid, folder: FOLDER, fileName: 'новая-картинка.png' });
    const saved = `${FOLDER}/новая-картинка.png`;
    await expect(page.getByText('новая-картинка.png', { exact: true }).first()).toBeVisible();
    await expect(page.getByText('Новая картинка', { exact: true })).toHaveCount(0);
    const bound = (await (await request.get(`/api/chats/${sid}`, { headers: { Authorization: `Bearer ${token}` } })).json()) as ChatSession;
    expect(bound.imageChat?.currentPath).toBe(saved);
    if (mobile) await page.getByRole('button', { name: 'Чат', exact: true }).click();
    await expect(page.locator('[data-image-chat-lead]').last()).toContainText(`Чат привязан к ${saved}`);
    if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `draft-bound-${vp.w}.png`) });
    if (mobile) await page.keyboard.press('Escape');

    // В списке чатов проекта — чат картинки с миниатюрой файла
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.getByRole('button', { name: 'К файлам' }).click();
    const thumb = page.locator('[data-image-chat-thumb]').first();
    await expect(thumb).toBeVisible({ timeout: 15_000 });
    await expect(thumb).toHaveAttribute('src', new RegExp(encodeURIComponent(saved).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    if (SHOTS) await page.screenshot({ path: path.join(SHOTS, `chat-list-thumb-${vp.w}.png`) });
    await request.dispose();
  });
}
