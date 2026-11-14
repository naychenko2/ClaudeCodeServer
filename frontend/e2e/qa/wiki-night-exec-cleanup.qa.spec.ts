// QA · ночь вики на нескольких исполнителях: уборка. Сносит посев (владельцы qa-nx-owner-*,
// их проекты каскадом), каталоги вики и истории чатов; список исполнителей возвращает к пустому
// (прежнее поведение ночи). Стенд после этого гасится руками (см. отчёт).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import { DATA, ownerApi, readSeed } from './wiki-night-exec-helpers';

test.setTimeout(120_000);

let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('уборка: посев, каталоги вики и чатов, список исполнителей', async () => {
  const seed = readSeed();
  const admin = api(request, await login(request));

  // Список исполнителей — снять ключ (пустой список = прежняя ночь по маршруту места).
  const cleared = await request.put('/api/admin/local-actions/wiki-nightly/executors', {
    headers: { Authorization: `Bearer ${await login(request)}` },
    data: { maxTotal: 0, executors: [] },
  });
  expect(cleared.status(), 'список исполнителей снят').toBe(200);

  // Все владельцы посева (в том числе от прежних прогонов seed) — по префиксу.
  const users = await admin.get<Array<{ id: string; username: string }>>('/api/users');
  const mine = users.filter(u => u.username.startsWith('qa-nx-owner-'));
  expect(mine.length, 'владельцы посева найдены').toBeGreaterThan(0);
  for (const u of mine) {
    // Каталог вики и истории чатов лежат в data/ стенда; пользователь уносит проекты и сессии.
    const owner = users.find(x => x.id === u.id)!;
    void owner;
    await admin.del(`/api/users/${u.id}`);
  }
  const left = await admin.get<Array<{ username: string }>>('/api/users');
  expect(left.filter(u => u.username.startsWith('qa-nx-owner-')).length, 'владельцев посева не осталось').toBe(0);

  // Каталоги вики удалённых владельцев и истории чатов — с диска стенда.
  const wikiHome = path.join(DATA, 'wiki');
  if (fs.existsSync(wikiHome)) {
    for (const dir of fs.readdirSync(wikiHome)) {
      if (seed.ownerId === dir) fs.rmSync(path.join(wikiHome, dir), { recursive: true, force: true });
    }
  }
  for (const g of seed.groups) {
    for (const p of g.projects) {
      for (const c of p.chats) fs.rmSync(path.join(DATA, 'sessions', c.id), { recursive: true, force: true });
    }
  }
  void ownerApi;
});
