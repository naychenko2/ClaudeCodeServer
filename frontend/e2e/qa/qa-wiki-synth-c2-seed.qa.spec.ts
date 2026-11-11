// QA С2 · ночной синтез на пяти чатах эксперимента (задача b98fee32, эпик 66b5e075).
// Спек 1/2: посев (проект + шесть чатов эксперимента с историей), настройки ночи, запуск ночи.
// Уборка — в спеке 2 (после проверок), здесь послеAll НИЧЕГО не сносит: ночь идёт долго,
// между прогонами посев должен пережить.
//
// Каталог данных стенда: DataPath из appsettings.Local.json (или QA_DATA_DIR) — стенд поднят
// с отдельным каталогом данных, а не боевым.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, seed, type Api } from './helpers';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const PREFIX = 'qa-c2-';
const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
const stamp = Date.now();

const resolveDataDir = (): string => {
  const fromEnv = process.env.QA_DATA_DIR;
  if (fromEnv) return fromEnv;
  const local = path.join(ROOT, 'backend', 'ClaudeHomeServer', 'appsettings.Local.json');
  if (fs.existsSync(local)) {
    try {
      const cfg = JSON.parse(fs.readFileSync(local, 'utf8')) as { DataPath?: string };
      if (cfg.DataPath) return path.dirname(path.resolve(ROOT, cfg.DataPath));
    } catch { /* читаем как есть */ }
  }
  return path.join(ROOT, 'backend', 'ClaudeHomeServer', 'bin', 'Debug', 'net10.0', 'data');
};

const DATA = resolveDataDir();

type Unit = { unit: string; key: string; label: string; title: string; user: number; assist: number };
const manifest = JSON.parse(fs.readFileSync(path.join(ATT, 'manifest.json'), 'utf8')) as Unit[];

let request: APIRequestContext;
let token: string;
let client: Api;
let ownerId = '';
let projectId = '';
const chatIds: Record<string, string> = {};
const marks: Record<string, string> = {};

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  ownerId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub as string;
  expect(fs.existsSync(DATA), `каталог данных стенда должен существовать: ${DATA}`).toBeTruthy();

  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  const seeded = seed(client);
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `${os.tmpdir()}/${PREFIX}${stamp}`);
  projectId = project.id;

  // Чаты эксперимента: шесть файлов исходников на пять источников (S2 = два чата).
  for (const u of manifest) {
    const s = await client.post<{ id: string }>(`/api/projects/${projectId}/sessions`,
      { name: `${PREFIX}${u.key}-${stamp}` });
    chatIds[u.key] = s.id;
    // История чата — файлом до первого обращения к аккумулятору (его создаёт только ход).
    const dir = path.join(DATA, 'sessions', s.id);
    fs.mkdirSync(dir, { recursive: true });
    fs.copyFileSync(path.join(ATT, `history-${u.key}.json`), path.join(dir, 'history.json'));
  }

  // Вики заводим (вики владельца — общая на все проекты).
  await client.post('/api/wiki/initialize').catch(() => undefined);

  // Настройки ночи: лимиты выше умолчаний (иначе шесть чатов одного раздела упрутся в 6 пакетов),
  // бюджет входа — максимум разрешённого.
  const settings = await client.put<{ nightly: { enabled: boolean; maxPackagesPerSection: number; maxPackagesPerNight: number; maxChatsPerSection: number; inputTokenBudget: number } }>(
    '/api/wiki/settings', {
      nightly: {
        enabled: true,
        maxPackagesPerSection: 20,
        maxPackagesPerNight: 20,
        maxChatsPerSection: 10,
        inputTokenBudget: 5_000_000,
      },
    });
  expect(settings.nightly.enabled).toBe(true);

  // Пометка «важный» — чат попадает в очередь ночи без ступеней отбора.
  for (const u of manifest) {
    const marked = await client.put<{ mark: string }>(`/api/wiki/chats/${chatIds[u.key]}/mark`, { mark: 'important' });
    marks[u.key] = marked.mark;
  }

  // Паспорт посева — для спека 2 и для разбора после ночи.
  fs.writeFileSync(path.join(ATT, 'seed.json'), JSON.stringify({
    stamp, ownerId, projectId, dataDir: DATA,
    chats: manifest.map(u => ({ ...u, sessionId: chatIds[u.key] })),
  }, null, 2), 'utf8');
});

test.afterAll(async () => {
  await request?.dispose();
});

test('вики заведена и чаты помечены важными', async () => {
  const s = await client.get<{ nightly: { enabled: boolean } }>('/api/wiki/settings');
  expect(s.nightly.enabled).toBe(true);
  for (const u of manifest) {
    expect(marks[u.key], `чат ${u.key} помечен`).toBe('important');
  }
});
