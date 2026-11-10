import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Приёмка волны C0+C1: учёт чтений страниц вики (keeper/reads.json).
// Стенд: поднятый стенд ветки feat/wiki-reads, свои данные. Путь к данным стенда —
// QA_DATA_DIR (по умолчанию каталог данных стенда из worktree), id владельца — из /api/auth/me.

const DATA_DIR = process.env.QA_DATA_DIR
  ?? path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../backend/ClaudeHomeServer/bin/Debug/net10.0/data');

type Page = { recall: number; query: number; read: number; human: number };

let readsPath = '';
let reads: () => Record<string, Page> = () => ({});

let request: APIRequestContext;
let token: string;
let client: Api;
let projectId = '';

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);

  const me = await client.get<{ userId: string }>('/api/auth/me');
  readsPath = path.join(DATA_DIR, 'wiki', me.userId, 'keeper', 'reads.json');
  reads = () => {
    if (!fs.existsSync(readsPath)) return {};
    try {
      return (JSON.parse(fs.readFileSync(readsPath, 'utf8')).pages ?? {}) as Record<string, Page>;
    } catch {
      return {};
    }
  };

  const projects = await client.get<Array<{ id: string }>>('/api/projects');
  projectId = projects[0]?.id ?? '';
});

test.afterAll(async () => {
  await request?.dispose();
});

const KEY = 'Общее/Общая-страница.md';
const CARD = 'Общее/Карточка-источника.md';

test.describe('Учёт чтений страниц вики', () => {
  test('Сценарий 1: человек открывает страницу во вкладке «Вики» — human +1', async ({ page, context }) => {
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    const before = reads()[KEY]?.human ?? 0;

    await page.goto('/#/notes');
    await expect(page.locator('#root')).not.toBeEmpty();
    // Единая разкладка «Заметки + Вики»: страница вики — строка дерева слева
    const row = page.locator('[data-testid="unified-row"]', { hasText: 'Общая страница' }).first();
    await expect(row, 'страница вики видна в дереве').toBeVisible({ timeout: 30000 });
    await row.click();
    await expect(page.getByTestId('wiki-doc')).toBeVisible({ timeout: 30000 });

    await expect.poll(() => reads()[KEY]?.human ?? 0, { timeout: 15000 }).toBe(before + 1);
  });

  test('Сценарий 4: hover-превью вики-ссылки (GET /api/notes/resolve) не считается', async () => {
    const before = reads()[KEY]?.human ?? 0;
    const res = await client.get<{ note: { path: string } }>(
      `/api/notes/resolve?name=${encodeURIComponent('Общая-страница')}`);
    expect(res.note.path).toBe(KEY);
    await new Promise(r => setTimeout(r, 800));
    expect(reads()[KEY]?.human ?? 0).toBe(before);
  });

  test('Сценарий 4: клик по сноске открывает карточку источника — она считается как открытие', async () => {
    const before = reads()[CARD]?.human ?? 0;
    // Фронт грузит карточку источника через GET /api/wiki/page — тем же путём, что и вкладка «Вики»
    await client.get(`/api/wiki/page?section=common&page=${encodeURIComponent(CARD)}`);
    await new Promise(r => setTimeout(r, 800));
    expect(reads()[CARD]?.human ?? 0).toBe(before + 1);
  });

  test('Сценарий 5: повреждённый reads.json не ломает страницы и не перезаписывается', async () => {
    const good = fs.readFileSync(readsPath, 'utf8');
    fs.writeFileSync(readsPath, '{"v":1,"since":');
    try {
      const res = await client.get(`/api/wiki/page?section=common&page=${encodeURIComponent(KEY)}`);
      expect(res).toBeTruthy();
      expect(fs.readFileSync(readsPath, 'utf8')).toBe('{"v":1,"since":');
    } finally {
      fs.writeFileSync(readsPath, good);
    }
  });

  test('Стык: MCP notes_read пишет тот же ключ страницы, что и вкладка «Вики»', async () => {
    const before = reads()[KEY];
    const id = 'd2lraXzQntCx0YnQtdC1L9Ce0LHRidCw0Y8t0YHRgtGA0LDQvdC40YbQsC5tZA';
    const session = await client.post<{ id: string }>(
      `/api/projects/${projectId}/sessions`, { title: 'qa-reads-keys' });
    const rpc = await request.post(`/mcp/notes/${session.id}`, {
      headers: { Authorization: `Bearer ${token}`, 'X-Caller-Session-Id': session.id },
      data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'notes_read', arguments: { id } } },
    });
    expect(rpc.ok()).toBeTruthy();
    await new Promise(r => setTimeout(r, 800));
    const after = reads()[KEY];
    expect(after?.read ?? 0).toBe((before?.read ?? 0) + 1);
  });
});
