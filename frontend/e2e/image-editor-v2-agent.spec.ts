import { test, expect, type APIRequestContext, type Page, type WebSocketRoute } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Шаг 15 редактора картинок v2: агент чата меняет открытый редактор, карточки ленты
// (запуск, промпт, тихая строка) и значок чата картинки в списке. Стенд — на ВРЕМЕННОЙ data,
// чат и состояние редактора живые. Ответ агента подставляется моком: события image_chat_state,
// tool_use/tool_result и image_launch вкладываются в WebSocket хаба, а ручки поставщика
// (каталог, котировка, задачи) — моком HTTP: драйверов на тестовой data нет.
//
//   IE_PROJECT_ROOT=/tmp/ie15/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5099 \
//     IE_SHOTS_DIR=../.cc-attachments/image-editor-v2/step15 npx playwright test e2e/image-editor-v2-agent.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.IE_PROJECT_ROOT || '';
const SHOTS = process.env.IE_SHOTS_DIR || '';
const HERO = 'images/hero.png';
const FILL = 'fal-ai/flux-pro/v1/fill';
const RS = '\u001e';

interface ChatSession { id: string; name: string; imageChat?: { currentPath: string } | null }
interface ChatState { revision: number; prompt: string; [k: string]: unknown }

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

async function ensureChat(request: APIRequestContext, token: string, projectId: string): Promise<ChatSession> {
  const headers = { Authorization: `Bearer ${token}` };
  const base = `/api/projects/${projectId}/image-editor/chats`;
  const found = (await (await request.get(`${base}?path=${encodeURIComponent(HERO)}`, { headers })).json()) as { current: ChatSession | null };
  if (found.current) return found.current;
  const r = await request.post(base, { headers, data: { sourcePath: HERO } });
  expect(r.ok(), `чат картинки должен создаться: ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as ChatSession;
}

// Ручки поставщика: на тестовой data рисовать нечем, каталог и задачи — мок
async function mockProvider(page: Page) {
  const started: string[] = [];
  const job = (jobId: string, status: string, extra: Record<string, unknown> = {}) => ({
    jobId, projectId: 'p', status, provider: 'fal', model: FILL, variants: [], createdAt: new Date().toISOString(), ...extra,
  });
  await page.route('**/api/projects/*/image-editor/catalog', r => r.fulfill({ json: {
    default: { provider: 'fal', model: 'auto' },
    providers: [{ key: 'fal', label: 'fal.ai', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: FILL, label: 'FLUX Fill', caps: { ops: ['edit', 'inpaint'], mask: 'native', maxReferences: 4, maxCount: 4, faceByReferences: false }, priceHint: { amount: 0.08, unit: 'usd', per: 'image' } },
    ] }],
    limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
  } }));
  await page.route('**/api/projects/*/image-editor/quote', async r => {
    const req = r.request().postDataJSON() as { model: string; count: number };
    await r.fulfill({ json: {
      quoteId: `q-${req.count}`, provider: 'fal', model: req.model === 'auto' ? FILL : req.model,
      estimate: { amount: 0.08 * req.count, unit: 'usd', approx: true, source: 'catalog' },
      expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 30,
    } });
  });
  await page.route('**/api/projects/*/image-editor/jobs', async r => {
    if (r.request().method() !== 'POST') return r.fallback();
    started.push(r.request().postData() ?? '');
    await r.fulfill({ status: 202, json: { jobId: 'job-manual' } });
  });
  await page.route('**/api/projects/*/image-editor/jobs/*', async r => {
    const id = decodeURIComponent(r.request().url().split('/').pop()!);
    if (r.request().method() === 'DELETE') return r.fulfill({ json: job(id, 'cancelled', { charged: false, outcome: 'cancelled' }) });
    await r.fulfill({ json: job(id, 'running') });
  });
  return started;
}

// WebSocket хаба: ходим к живому серверу, эхо записи состояния отбрасываем (события агента
// шлёт тест) и умеем вложить своё сообщение message
async function hubMock(page: Page) {
  let route: WebSocketRoute | null = null;
  await page.routeWebSocket(/\/hubs\/session/, ws => {
    route = ws;
    const server = ws.connectToServer();
    ws.onMessage(m => server.send(m));
    server.onMessage(m => {
      if (typeof m !== 'string') { ws.send(m); return; }
      const kept = m.split(RS).filter(rec => rec && !rec.includes('"image_chat_state"'));
      if (kept.length) ws.send(kept.map(x => x + RS).join(''));
    });
  });
  return async (msg: Record<string, unknown>) => {
    await expect.poll(() => route !== null, { timeout: 15_000 }).toBe(true);
    route!.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + RS);
  };
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

// Агент пишет состояние так же, как тулсет: на сервер, а редактору — событием с той же ревизией
async function agentWrites(request: APIRequestContext, token: string, projectId: string, sessionId: string,
  inject: (m: Record<string, unknown>) => Promise<void>, patch: Record<string, unknown>) {
  const headers = { Authorization: `Bearer ${token}` };
  const url = `/api/projects/${projectId}/image-editor/chats/${sessionId}/state`;
  const before = (await (await request.get(url, { headers })).json()) as ChatState;
  const r = await request.put(url, { headers, data: { ...before, ...patch } });
  expect(r.ok(), `состояние агента должно записаться: ${r.status()}`).toBeTruthy();
  const state = (await r.json()) as ChatState;
  const changes = Object.keys(patch).map(field => ({ field, from: before[field], to: patch[field] }));
  await inject({ type: 'image_chat_state', sessionId, projectId, revision: state.revision, state, changedBy: 'agent', changes });
  return changes;
}

async function toolCall(inject: (m: Record<string, unknown>) => Promise<void>, sessionId: string,
  id: string, tool: string, input: Record<string, unknown>, result: unknown) {
  await inject({ type: 'tool_use', sessionId, id, name: `mcp__image-editor__${tool}`, input });
  await inject({ type: 'tool_result', sessionId, toolUseId: id, content: JSON.stringify(result), isError: false });
}

test.beforeAll(() => {
  expect(ROOT, 'нужен IE_PROJECT_ROOT — папка тестового проекта на временной data').not.toBe('');
  expect(fs.existsSync(path.join(ROOT, HERO))).toBeTruthy();
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
});

test('агент меняет открытый редактор, карточки запуска и промпта, тихая строка, значок в списке', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  const chat = await ensureChat(request, token, project.id);
  const started = await mockProvider(page);
  const inject = await hubMock(page);

  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page, project.id, token);
  await expect(page.getByText(`Открыт чат этой картинки: «${chat.name}»`)).toBeVisible();
  const lead = page.locator('[data-image-chat-lead]');
  await expect(lead.getByRole('button', { name: 'Новый чат' })).toBeVisible();

  // Человек набирает своё — редактор пишет состояние на сервер (дебаунс 500 мс)
  const promptBox = page.locator('[data-prompt-card] textarea');
  await promptBox.fill('мой набросок');
  const headers = { Authorization: `Bearer ${token}` };
  const stateUrl = `/api/projects/${project.id}/image-editor/chats/${chat.id}/state`;
  await expect.poll(async () => ((await (await request.get(stateUrl, { headers })).json()) as ChatState).prompt, { timeout: 10_000 })
    .toBe('мой набросок');

  // Сценарий А: агент сам меняет промпт, модель и число вариантов и запускает генерацию
  const agentPrompt = 'Убери торшер справа и сделай тёплый вечерний свет';
  const changes = await agentWrites(request, token, project.id, chat.id, inject,
    { prompt: agentPrompt, promptAuthor: 'agent', provider: 'fal', model: FILL, count: 2 });
  await expect(promptBox).toHaveValue(agentPrompt);
  // Метка автора: «написал Claude» или имя персоны чата (у чата по умолчанию — ассистент)
  await expect(page.locator('[data-prompt-card] [data-agent-tag]')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Вернуть мой текст' })).toBeVisible();
  await expect(page.locator('[data-count-agent]')).toBeVisible();
  await expect(page.locator('[data-editor-left]')).toContainText('✦ fal.ai · FLUX Fill');

  await toolCall(inject, chat.id, 'toolu_launch', 'image_generate', { prompt: agentPrompt, model: FILL, count: 2 }, {
    jobId: 'job-agent',
    quote: { provider: 'fal', model: FILL, estimate: { amount: 0.16, unit: 'usd', approx: true, source: 'catalog' }, expectedSeconds: 30 },
    changes: changes.filter(c => c.field !== 'promptAuthor'),
  });
  const launchCard = page.locator('[data-image-chat] [data-image-card]').first();
  await expect(launchCard).toContainText('Запущена генерация');
  await expect(launchCard).toContainText('≈ $0.16 · 2 варианта');
  await expect(launchCard.locator('[data-image-changed]')).toHaveText(/^(Изменил|Изменено): промпт, модель → FLUX Fill, вариантов: 2$/);
  // Открытый редактор этого чата сам показывает запущенную агентом генерацию
  await expect(page.getByText('Рисуем 2 варианта').first()).toBeVisible();
  await expect(page.locator('[data-agent-tag]').filter({ hasText: /запустил Claude|запуск:/ })).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'agent-launch-1440.png') });

  // «Отменить» в карточке: сервер отвечает отменой и рассылает её событием
  await launchCard.getByRole('button', { name: 'Отменить' }).click();
  await inject({ type: 'image_edit_failed', jobId: 'job-agent', projectId: project.id, outcome: 'cancelled', charged: false, chatSessionId: chat.id, initiator: 'agent' });
  await expect(launchCard).toContainText('Генерация отменена');
  await expect(launchCard).toContainText('Деньги не списаны. Промпт остался в поле сверху.');
  await page.getByRole('button', { name: 'Вернуть мой текст' }).click();
  await expect(promptBox).toHaveValue('мой набросок');
  await expect(page.locator('[data-prompt-card] [data-agent-tag]')).toHaveCount(0);
  await expect(page.getByText('Рисуем 2 варианта')).toHaveCount(0);

  // Сценарий Б: агент предлагает промпт — «Вставить в промпт», затем «Сгенерировать»
  const suggestion = 'Обложка блога: светлая гостиная, мягкий утренний свет, много воздуха';
  await toolCall(inject, chat.id, 'toolu_suggest', 'image_suggest_prompt', { prompt: suggestion, count: 1 },
    { prompt: suggestion, count: 1, model: null, note: 'Промпт показан человеку карточкой' });
  const promptCardInFeed = page.locator('[data-image-chat] [data-image-card]').filter({ hasText: 'Промпт' });
  await expect(promptCardInFeed.locator('[data-image-prompt]')).toHaveText(suggestion);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'agent-suggest-1440.png') });
  await promptCardInFeed.getByRole('button', { name: 'Вставить в промпт' }).click();
  await expect(promptBox).toHaveValue(suggestion);
  await expect(promptCardInFeed.getByRole('button', { name: 'Вставлено' })).toBeVisible();
  await expect(page.locator('[data-count-agent]')).toBeVisible();
  const gen = promptCardInFeed.getByRole('button', { name: /^Сгенерировать · ≈ \$0\.08$/ });
  await expect(gen).toBeEnabled({ timeout: 10_000 });
  await gen.click();
  await expect.poll(() => started.length).toBe(1);
  expect(started[0]).toContain(suggestion);
  await expect(page.getByText('Рисуем 1 вариант').first()).toBeVisible();

  // Тихая строка ручного запуска — сервер пишет её после старта задачи
  await inject({ type: 'image_launch', sessionId: chat.id, by: 'human', prompt: suggestion, provider: 'fal', model: FILL, count: 1,
    estimate: { amount: 0.08, unit: 'usd', approx: true, source: 'catalog' }, jobId: 'job-manual', timestamp: Date.now() });
  await expect(page.locator('[data-image-chat] [data-image-sysline]'))
    .toHaveText(`Вы запустили: «${suggestion}» · FLUX Fill · ≈ $0.08 · 1 вариант`);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'agent-generate-1440.png') });

  // Значок чата картинки в списке чатов проекта: клик открывает редактор с этим чатом
  await page.getByRole('button', { name: 'К файлам' }).click();
  const card = page.locator('[data-image-chat-tag]').first();
  await expect(card).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-image-chat-thumb]').first()).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'chat-list-1440.png') });
  await card.click();
  await expect(page.getByRole('button', { name: 'К файлам' })).toBeVisible();
  await expect(page.getByText(`Открыт чат этой картинки: «${chat.name}»`).last()).toBeVisible();
  await expect(page.locator('[data-image-chat-lead]').getByRole('button', { name: 'Открыть в полном чате' })).toBeVisible();
  await request.dispose();
});

test('телефон 390: правка агента в поле промпта, карточка в шторке чата, значок в списке', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  const chat = await ensureChat(request, token, project.id);
  await mockProvider(page);
  const inject = await hubMock(page);

  await page.setViewportSize({ width: 390, height: 844 });
  await openEditor(page, project.id, token);
  await expect(page.getByText(`Открыт чат этой картинки: «${chat.name}»`)).toBeVisible();
  const agentPrompt = 'Сделай небо закатным, остальное не трогай';
  const changes = await agentWrites(request, token, project.id, chat.id, inject,
    { prompt: agentPrompt, promptAuthor: 'agent', model: FILL, count: 3 });
  await expect(page.locator('[data-prompt-card] textarea')).toHaveValue(agentPrompt);
  await expect(page.locator('[data-prompt-card] [data-agent-tag]')).toBeVisible();
  await toolCall(inject, chat.id, 'toolu_m', 'image_generate', { prompt: agentPrompt, count: 3 }, {
    jobId: 'job-agent-m',
    quote: { provider: 'fal', model: FILL, estimate: { amount: 0.24, unit: 'usd', approx: true, source: 'catalog' }, expectedSeconds: 30 },
    changes: changes.filter(c => c.field !== 'promptAuthor'),
  });
  // Шторка чата закрыта — карточки нет на экране, задачу редактор узнаёт из события прогресса
  await inject({ type: 'image_edit_progress', jobId: 'job-agent-m', projectId: project.id, stage: 'running', chatSessionId: chat.id, initiator: 'agent' });
  await expect(page.getByText('Рисуем 3 варианта').first()).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'agent-launch-390.png') });
  await page.getByRole('button', { name: 'Чат', exact: true }).click();
  const launchCard = page.locator('[data-image-chat] [data-image-card]').first();
  await expect(launchCard).toContainText('Запущена генерация');
  await expect(launchCard).toContainText('≈ $0.24 · 3 варианта');
  const box = (await launchCard.boundingBox())!;
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'agent-card-390.png') });
  await request.dispose();
});
