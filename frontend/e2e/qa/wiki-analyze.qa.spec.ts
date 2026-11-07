import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, login, seed, type Api } from './helpers';
import { existsSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

// QA · явный разбор чата: новая страница в вики, человеческий итог в меню (доработки 05.11).
// Стенд из worktree вручную (:5560); локальный движок — 127.0.0.1:18020 (qwen3.8-27b).

const PREFIX = 'qa-wikian-';
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

const wikiTrigger = (page: Page) =>
  page.locator('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]');

function listFiles(dir: string, depth = 0): string[] {
  if (depth > 5 || !existsSync(dir)) return [];
  const out: string[] = [];
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) out.push(...listFiles(p, depth + 1));
    else out.push(p);
  }
  return out;
}

// реплика человека в чат: ждём, пока сервер её увидит (messageCount)
async function sendMessage(page: Page, chatId: string, text: string): Promise<boolean> {
  for (let attempt = 0; attempt < 3; attempt++) {
    await page.goto(`/#/project/${projectId}/chat/${chatId}`);
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(4000);
    const box = page.locator('textarea').first();
    await box.waitFor({ state: 'visible', timeout: 20000 });
    await box.click();
    await box.fill(text);
    await page.keyboard.press('Enter');
    for (let i = 0; i < 20; i++) {
      const list = await client.get<Array<{ id: string; messageCount: number }>>(`/api/projects/${projectId}/sessions`);
      const s = list.find(x => x.id === chatId);
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
  // ночь выключена: она занимает локальный движок и мешает явному разбору
  await client.put('/api/wiki/settings', { nightly: { enabled: false } }).catch(() => undefined);
  await client.put('/api/me/model-tiers', { strong: 'qwen3.8-27b', medium: 'qwen3.8-27b', weak: 'qwen3.8-27b' }).catch(() => undefined);
});

test.afterAll(async () => {
  try { await client.put('/api/wiki/settings', { nightly: { enabled: false } }); } catch { /* best-effort */ }
  await seeded?.cleanup();
  await request?.dispose();
});

test('«Разобрать этот чат»: в вики появляется новая страница, итог — человеческим текстом', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const sent = await sendMessage(page, chatId,
    'Наша компания делает продукт «Кедр» — платформа для командной работы. Первый релиз вышел 5 ноября 2026 года, порт локального движка — 18020. Запиши это как новое знание в вики.');
  expect(sent, 'реплика человека ушла в чат').toBeTruthy();

  const pagesDir = join(DATA, 'wiki', ownerId, 'pages');
  const before = listFiles(pagesDir);

  await page.goto(`/#/project/${projectId}/chat/${chatId}`);
  await page.waitForTimeout(2500);
  await wikiTrigger(page).click();
  const analyzeItem = page.getByRole('menuitem', { name: /Разобрать этот чат/ });
  await expect(analyzeItem, 'пункт «Разобрать этот чат» доступен').toBeVisible({ timeout: 20000 });
  await analyzeItem.click();

  // ждём исхода разбора (он идёт к локальной модели — минуты)
  let last: { outcome: string; reason: string | null; packageIds: string[] } | null = null;
  const deadline = Date.now() + 360_000;
  while (Date.now() < deadline) {
    const st = await client.get<{ running: boolean; last: typeof last }>(`/api/wiki/chats/${chatId}/analyze-status`);
    if (!st.running && st.last) { last = st.last; break; }
    await page.waitForTimeout(4000);
  }
  test.info().annotations.push({ type: 'note', description: `исход разбора: ${JSON.stringify(last)}` });
  expect(last, 'разбор завершился').not.toBeNull();
  expect(last!.outcome, 'исход разбора — применён, а не отказ «карточка источника»').toBe('applied');
  expect(last!.reason ?? '', 'в исходе нет отказа «карточка источника»').not.toMatch(/карточк/i);
  expect(last!.packageIds.length, 'разбор принёс пакет операций').toBeGreaterThan(0);

  // в вики появились новые страницы: карточка источника, страница знания и хроника
  const after = listFiles(pagesDir);
  const fresh = after.filter(f => !before.includes(f));
  test.info().annotations.push({ type: 'note', description: `новые файлы вики: ${fresh.join(', ')}` });
  expect(fresh.length, 'в вики появились новые страницы').toBeGreaterThan(0);
  const knowledge = fresh.filter(f => f.includes('Источники') === false && f.includes('Хроники') === false && f.endsWith('.md') && !f.endsWith('_index.md') && !f.endsWith('_log.md'));
  test.info().annotations.push({ type: 'note', description: `страницы знания: ${knowledge.join(', ')}` });
  expect(knowledge.length, 'создана страница знания (операция create), а не только карточка источника').toBeGreaterThan(0);

  // хроника чата появилась (применённый разбор её создаёт)
  const list = await client.get<{ items: Array<{ sessionId: string; chroniclePath: string | null }> }>('/api/wiki/chats');
  const rec = list.items.find(i => i.sessionId === chatId);
  test.info().annotations.push({ type: 'note', description: `запись чата в списке отбора: ${JSON.stringify(rec ?? null)}` });
  expect(rec?.chroniclePath ?? null, 'у чата появилась хроника').not.toBeNull();

  // в шапке нет сырой причины отказа «разбор окна m…»
  const bodyText = await page.locator('body').innerText();
  expect(bodyText, 'в шапке нет сырой причины отказа «разбор окна m…»').not.toMatch(/разбор окна m\d/);

  // итог разбора — человеческим текстом в меню
  await wikiTrigger(page).click();
  const row = page.getByRole('menuitem', { name: /Разобрать/ });
  await expect(row, 'пункт разбора показывает человеческий итог').toBeVisible({ timeout: 10000 });
  const rowText = await row.innerText();
  test.info().annotations.push({ type: 'note', description: `строка пункта разбора: ${rowText}` });
  expect(rowText, 'итог разбора — человеческим текстом').toMatch(/Разобрано|Есть спорное|Нового нет/i);

  // пункт «Открыть хронику» появился
  await expect(page.getByRole('menuitem', { name: /^Открыть хронику/ }), 'пункт «Открыть хронику» появился').toBeVisible();

  // точка на кнопке погасла, когда меню открыли
  const dots = await wikiTrigger(page).locator('span[style*="position: absolute"]').count();
  test.info().annotations.push({ type: 'note', description: `точек-индикаторов на кнопке после открытия меню: ${dots}` });
  expect(dots, 'точка на кнопке погасла после открытия меню').toBe(0);
});
