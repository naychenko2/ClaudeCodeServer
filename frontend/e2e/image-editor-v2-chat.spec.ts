import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Шаг 14 редактора картинок v2: чат картинки в редакторе на живых ручках chats и хабе
// SendImageChatMessage. Гоняется против стенда на ВРЕМЕННОЙ data (не боевой): проект —
// папка IE_PROJECT_ROOT с images/hero.png. Ход модели на такой data не нужен: проверяем
// то, что пишет сервер до хода — чат, привязку и пометку снимка у сообщения.
//
//   IE_PROJECT_ROOT=/tmp/ie14/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5099 \
//     IE_SHOTS_DIR=../.cc-attachments/image-editor-v2/step14 npx playwright test e2e/image-editor-v2-chat.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.IE_PROJECT_ROOT || '';
const SHOTS = process.env.IE_SHOTS_DIR || '';
const HERO = 'images/hero.png';

interface StoredUser { kind: string; text?: string; attachedPaths?: string[]; imageSnapshot?: { revision: string; attached: boolean } | null }
interface ChatSession { id: string; name: string; imageChat?: { currentPath: string } | null }

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

async function ensureProject(request: APIRequestContext, token: string): Promise<{ id: string; name: string }> {
  const headers = { Authorization: `Bearer ${token}` };
  const list = (await (await request.get('/api/projects', { headers })).json()) as { id: string; name: string; rootPath: string }[];
  const found = list.find(p => p.rootPath === ROOT);
  if (found) return found;
  const r = await request.post('/api/projects', { headers, data: { name: 'Сайт студии', rootPath: ROOT } });
  expect(r.ok(), `проект должен создаться: ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as { id: string; name: string };
}

async function openEditor(page: Page, projectId: string, token: string) {
  await page.addInitScript(tk => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.removeItem('cc-image-editor-mock');
  }, token);
  await page.goto(`/#/project/${projectId}/file/${encodeURIComponent(HERO)}`);
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(page.getByRole('button', { name: 'К файлам' })).toBeVisible();
}

async function userMessages(request: APIRequestContext, token: string, projectId: string, sessionId: string): Promise<StoredUser[]> {
  const r = await request.get(`/api/projects/${projectId}/sessions/${sessionId}/history`, { headers: { Authorization: `Bearer ${token}` } });
  expect(r.ok()).toBeTruthy();
  return ((await r.json()) as StoredUser[]).filter(m => m.kind === 'user_message');
}

async function send(page: Page, text: string) {
  const input = page.locator('[data-image-chat] textarea').first();
  await input.fill(text);
  await input.press('Enter');
}

// Пометка на холсте: мазок кистью по середине картинки
async function drawMark(page: Page) {
  const svg = page.locator('svg[preserveAspectRatio="none"]').first();
  const box = (await svg.boundingBox())!;
  await page.mouse.move(box.x + box.width * 0.4, box.y + box.height * 0.5);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.55, box.y + box.height * 0.55, { steps: 6 });
  await page.mouse.up();
}

test.beforeAll(() => {
  expect(ROOT, 'нужен IE_PROJECT_ROOT — папка тестового проекта на временной data').not.toBe('');
  expect(fs.existsSync(path.join(ROOT, HERO))).toBeTruthy();
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
});

test('чат картинки: создаётся первым сообщением, находится снова, снимок — только при изменении холста', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  const headers = { Authorization: `Bearer ${token}` };
  const lookup = async () => (await (await request.get(
    `/api/projects/${project.id}/image-editor/chats?path=${encodeURIComponent(HERO)}`, { headers })).json()) as { current: ChatSession | null };
  expect((await lookup()).current, 'на свежей data чата картинки ещё нет').toBeNull();

  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page, project.id, token);

  // До первого сообщения — заглушка, чата нет ни в списке, ни на сервере
  await expect(page.getByText('Новый чат картинки · появится в списке после первого сообщения')).toBeVisible();
  await expect(page.locator('[data-image-chat-lead]')).toContainText(`Чат привязан к ${HERO}`);
  await expect(page.locator('[data-image-snapshot-chip="changed"]')).toContainText('hero.png');
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'stub-1440.png') });

  // Первое сообщение создаёт чат и уходит со снимком
  await send(page, 'Что здесь можно улучшить?');
  await expect.poll(async () => (await lookup()).current?.id ?? null, { timeout: 20_000 }).not.toBeNull();
  const chat = (await lookup()).current!;
  expect(chat.name).toBe('hero.png · правка');
  const sessions = (await (await request.get(`/api/projects/${project.id}/sessions`, { headers })).json()) as ChatSession[];
  expect(sessions.map(s => s.id), 'чат картинки виден в списке чатов проекта').toContain(chat.id);

  await expect.poll(async () => (await userMessages(request, token, project.id, chat.id)).length, { timeout: 20_000 }).toBe(1);
  const [first] = await userMessages(request, token, project.id, chat.id);
  expect(first.imageSnapshot?.attached, 'первое сообщение несёт снимок холста').toBe(true);
  expect(first.attachedPaths?.some(p => p.includes('hero-снимок.png'))).toBeTruthy();
  const lead = page.locator('[data-image-chat-lead]');
  await expect(lead.getByRole('button', { name: 'Новый чат' })).toBeVisible();
  await expect(lead.getByRole('button', { name: 'Открыть в полном чате' })).toBeVisible();

  // Холст не менялся — чип «без изменений», второе сообщение идёт без снимка
  await expect(page.locator('[data-image-snapshot-chip="same"]')).toContainText('hero.png · без изменений');
  await expect(page.locator('[data-image-chat] textarea').first()).toBeEnabled({ timeout: 60_000 });
  await send(page, 'А если сделать вечер?');
  await expect.poll(async () => (await userMessages(request, token, project.id, chat.id)).length, { timeout: 60_000 }).toBe(2);
  const second = (await userMessages(request, token, project.id, chat.id))[1];
  expect(second.imageSnapshot).toEqual({ revision: first.imageSnapshot!.revision, attached: false });
  expect(second.attachedPaths ?? []).toEqual([]);
  await expect(page.getByText('холст не менялся — снимок не приложен')).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'chat-1440.png') });

  // Повторное открытие картинки — тот же чат, без заглушки
  await page.getByRole('button', { name: 'К файлам' }).click();
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(page.getByText(`Открыт чат этой картинки: «${chat.name}»`)).toBeVisible();
  await expect(page.getByText('А если сделать вечер?')).toBeVisible();
  await expect(page.getByText('Новый чат картинки · появится в списке после первого сообщения')).toHaveCount(0);

  // Пометка меняет холст — третье сообщение снова со снимком
  await expect(page.locator('[data-image-snapshot-chip]')).toBeVisible();
  await drawMark(page);
  await expect(page.locator('[data-image-snapshot-chip="changed"]')).toContainText('hero.png · 1 пометка');
  await expect(page.locator('[data-image-chat] textarea').first()).toBeEnabled({ timeout: 60_000 });
  await send(page, 'Вот тут поправь');
  await expect.poll(async () => (await userMessages(request, token, project.id, chat.id)).length, { timeout: 60_000 }).toBe(3);
  const third = (await userMessages(request, token, project.id, chat.id))[2];
  expect(third.imageSnapshot?.attached).toBe(true);
  expect(third.imageSnapshot?.revision).not.toBe(first.imageSnapshot!.revision);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'reopen-marked-1440.png') });
  await request.dispose();
});

test('телефон 390: чат картинки в шторке на 86 %', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  await request.dispose();

  await page.setViewportSize({ width: 390, height: 844 });
  await openEditor(page, project.id, token);
  await page.getByRole('button', { name: 'Чат', exact: true }).click();
  const lead = page.locator('[data-image-chat-lead]');
  await expect(lead).toBeVisible();
  await expect(page.locator('[data-image-chat]').getByText('Вот тут поправь')).toBeVisible();
  const box = (await page.locator('[data-image-chat]').boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'chat-390.png') });
});
