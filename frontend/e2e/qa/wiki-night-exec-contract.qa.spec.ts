// QA · фича «Ночь вики на нескольких локальных моделях»: контракт админ-API и права.
// Без ночи — быстрые проверки, которые нужны по постановке отдельно от сценариев прохода:
// валидатор списка (И3: только локальные движки), форма снимка /api/usage, отказ владельцу,
// и две одновременные записи без потери и без порчи файла.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import { DATA, ownerApi, readSeed } from './wiki-night-exec-helpers';

const NIGHTLY = '/api/admin/local-actions/wiki-nightly/executors';
const STORE = path.join(DATA, 'local-action-executors.json');

test.setTimeout(120_000);

let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('контракт: валидатор, снимок, права, две одновременные записи', async () => {
  const seed = readSeed();
  const adminToken = await login(request);
  const admin = api(request, adminToken);
  const owner = await ownerApi(request, seed);
  const put = async (body: unknown) => {
    const r = await request.put(NIGHTLY, { headers: { Authorization: `Bearer ${adminToken}` }, data: body });
    return { status: r.status(), body: r.ok() ? await r.json().catch(() => null) : await r.text() };
  };

  // И3: облачные формы в списке исполнителей не проходят — с названной причиной.
  const bad: Array<[string, string, unknown]> = [
    ['direct:minimax', 'облачный адаптер', { maxTotal: 1, executors: [{ engine: 'direct:minimax', parallel: 1 }] }],
    ['tier:strong', 'слот тира', { maxTotal: 1, executors: [{ engine: 'tier:strong', parallel: 1 }] }],
    ['qwen3.8-27b', 'неизвестный локальный движок', { maxTotal: 1, executors: [{ engine: 'qwen3.8-27b', parallel: 1 }] }],
    ['night2×2', 'потолок движка', { maxTotal: 2, executors: [{ engine: 'night2', parallel: 2 }] }],
    ['maxTotal=5', 'maxTotal', { maxTotal: 5, executors: [{ engine: 'local', parallel: 1 }] }],
    ['пустое имя', 'без имени движка', { maxTotal: 1, executors: [{ engine: ' ', parallel: 1 }] }],
    ['дубль', 'назван дважды', { maxTotal: 2, executors: [{ engine: 'local', parallel: 1 }, { engine: 'local', parallel: 1 }] }],
    ['null-элемент', 'пустая запись', { maxTotal: 1, executors: [null] }],
    ['null-список', 'Не прислан', { maxTotal: 1, executors: null }],
  ];
  for (const [name, fragment, body] of bad) {
    const res = await put(body);
    expect(res.status, `400 на «${name}»`).toBe(400);
    expect(JSON.stringify(res.body).toLowerCase(), `причина названа (${name})`).toContain(fragment.toLowerCase());
  }

  // Запись и форма снимка: usage отдаёт { maxTotal, items:[{engine,parallel}] } в порядке стора.
  expect((await put({ maxTotal: 2, executors: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] })).status).toBe(200);
  const usage = await admin.get<{ ollama: { actions: Array<{ key: string; executors: unknown }>; localEngines: Array<{ name: string }> } }>('/api/usage');
  const row = usage.ollama.actions.find(a => a.key === 'wiki-nightly')!;
  expect(row.executors).toEqual({ maxTotal: 2, items: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] });
  // UI предлагает только локальные движки: в списке нет облачных имён, только реестр LocalEngines.
  expect(usage.ollama.localEngines.map(e => e.name).sort()).toEqual(['local', 'night2']);

  // Владелец (не админ): видит список, но записать не может.
  const settings = await owner.client.get<{ nightly: { executors: Array<{ engine: string; title: string }> } }>('/api/wiki/settings');
  expect(settings.nightly.executors.map(e => e.engine), 'владелец видит список').toEqual(['local', 'night2']);
  const denied = await request.put(NIGHTLY, {
    headers: { Authorization: `Bearer ${owner.token}` },
    data: { maxTotal: 1, executors: [{ engine: 'local', parallel: 1 }] },
  });
  expect(denied.status(), `владельцу запись запрещена (${denied.status()})`).toBe(403);
  // Список не изменился от попытки владельца.
  const after = await admin.get<{ ollama: { actions: Array<{ key: string; executors: unknown }> } }>('/api/usage');
  expect(after.ollama.actions.find(a => a.key === 'wiki-nightly')!.executors)
    .toEqual({ maxTotal: 2, items: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] });

  // Две одновременные записи (двойной клик и две вкладки): файл остаётся целым JSON, в нём —
  // ровно одно из двух состояний, и снимок usage совпадает с файлом (нет «в памяти одно, на диске другое»).
  const a = { maxTotal: 2, executors: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] };
  const b = { maxTotal: 1, executors: [{ engine: 'night2', parallel: 1 }] };
  const [ra, rb] = await Promise.all([put(a), put(b)]);
  expect([ra.status, rb.status], 'обе одновременные записи приняты').toEqual([200, 200]);
  const file = JSON.parse(fs.readFileSync(STORE, 'utf8')) as Record<string, unknown>;
  const saved = file['wiki-nightly'] as { maxTotal: number; executors: Array<{ engine: string }> };
  expect(saved, 'в файле есть запись места').toBeTruthy();
  const diskKey = JSON.stringify(saved);
  expect([JSON.stringify(a), JSON.stringify(b)], 'в файле ровно одно из двух состояний').toContain(diskKey);
  const usage2 = await admin.get<{ ollama: { actions: Array<{ key: string; executors: { maxTotal: number; items: Array<{ engine: string }> } }> } }>('/api/usage');
  const snap = usage2.ollama.actions.find(x => x.key === 'wiki-nightly')!.executors;
  expect(JSON.stringify({ maxTotal: snap.maxTotal, executors: snap.items }), 'снимок совпадает с файлом').toBe(diskKey);
});
