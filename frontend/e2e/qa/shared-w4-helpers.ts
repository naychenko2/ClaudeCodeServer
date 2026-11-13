// Общая фикстура QA-приёмки волны 4 «Общие проекты» (персоны у редактора,
// удаление общих чатов, приватный чат при создании). Не спек: по имени
// *.qa-helpers.ts конфиг его не подхватит (testMatch — только *.qa.spec.ts).
//
// Все сущности — с префиксом qa-w4-, уборка в afterAll. Учётки создаются админом
// через POST /api/users; пароль у всех QA-учёток один.
import { expect, type Page } from '@playwright/test';

export const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5501';
export const ADMIN_PASS = process.env.E2E_PASS || '12345';
export const QA_PASS = 'qa-pass-1234';
export const OWNER = 'admin';
export const EDITOR = 'qa_w4_editor';
export const READER = 'qa_w4_reader';

export type Json = any;

export async function login(user: string, pass?: string): Promise<string> {
  const pwd = pass ?? (user === OWNER ? ADMIN_PASS : QA_PASS);
  for (let i = 0; i < 10; i++) {
    const r = await fetch(`${BASE}/api/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: user, password: pwd }),
    });
    if (r.status === 200) return (await r.json()).token as string;
    if (r.status !== 429) throw new Error(`login ${user}: ${r.status} ${(await r.text()).slice(0, 200)}`);
    await new Promise(r => setTimeout(r, 12000));
  }
  throw new Error(`login ${user}: лимит входа не прошёл`);
}

export async function api(token: string, method: string, path: string, body?: unknown) {
  const r = await fetch(`${BASE}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(120_000),
  });
  const txt = await r.text();
  let parsed: Json = txt;
  try { parsed = JSON.parse(txt); } catch { /* текстовый ответ */ }
  return { status: r.status, body: parsed };
}

export const tokens: Record<string, string> = {};
export async function tok(user: string): Promise<string> {
  if (!tokens[user]) tokens[user] = await login(user);
  return tokens[user];
}

// Учётки: админ создаёт отсутствующие. Повторное создание того же username — 400,
// поэтому сначала смотрим список.
export async function ensureUsers(): Promise<void> {
  const a = await tok(OWNER);
  const list = (await api(a, 'GET', '/api/users')).body as Json[];
  const names = new Set((Array.isArray(list) ? list : []).map(u => u.username));
  for (const u of [EDITOR, READER]) {
    if (names.has(u)) continue;
    const r = await api(a, 'POST', '/api/users', { username: u, password: QA_PASS, role: 'user' });
    expect([200, 201], `создание ${u}: ${JSON.stringify(r.body)}`).toContain(r.status);
  }
}

// Рубильник инстанса включён в appsettings.Local.json стенда; здесь — пользовательский флаг.
export async function ensureFlags(): Promise<void> {
  for (const u of [OWNER, EDITOR, READER]) {
    const t = await tok(u);
    const r = await api(t, 'PUT', '/api/feature-flags/shared-projects', { enabled: true });
    expect(r.status, `флаг shared-projects у ${u}: ${JSON.stringify(r.body)}`).toBe(200);
  }
}

export async function makeProject(name: string, root: string): Promise<string> {
  const a = await tok(OWNER);
  const r = await api(a, 'POST', '/api/projects', { name, rootPath: root, createDirectory: true });
  expect(r.status, `создание проекта ${name}: ${JSON.stringify(r.body)}`).toBe(201);
  return r.body.id as string;
}

export async function addMember(pid: string, username: string, role: 'editor' | 'reader'): Promise<void> {
  const a = await tok(OWNER);
  const r = await api(a, 'POST', `/api/projects/${pid}/members`, { username, role });
  expect(r.status, `участник ${username}: ${JSON.stringify(r.body)}`).toBe(200);
}

export async function createPersona(pid: string, name: string, owner = OWNER): Promise<string> {
  const t = await tok(owner);
  const r = await api(t, 'POST', '/api/personas', {
    name, scope: 'project', projectId: pid, voice: {},
  });
  expect(r.status, `создание персоны ${name}: ${JSON.stringify(r.body)}`).toBe(200);
  return r.body.id as string;
}

export async function makeLead(personaId: string): Promise<void> {
  const a = await tok(OWNER);
  const r = await api(a, 'POST', `/api/personas/${personaId}/make-default`);
  expect(r.status, `назначение руководителя: ${JSON.stringify(r.body)}`).toBe(200);
}

export async function createChat(pid: string, name: string, opts: { private?: boolean; user?: string; model?: string } = {}) {
  const t = await tok(opts.user ?? OWNER);
  return api(t, 'POST', `/api/projects/${pid}/sessions`, {
    name,
    ...(opts.private ? { private: true } : {}),
    ...(opts.model ? { model: opts.model } : {}),
  });
}

// Вход в приложение под токеном учётки: токен и userId в sessionStorage, затем
// полная перезагрузка (иначе сторы поднимаются со старым пользователем).
export async function uiLogin(page: Page, user: string): Promise<void> {
  const t = await tok(user);
  await page.goto(`${BASE}/#/home`);
  await page.evaluate(async (token) => {
    sessionStorage.setItem('cc_token', token);
    localStorage.setItem('cc_token', token);
    const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${token}` } });
    const m: any = await me.json();
    sessionStorage.setItem('cc_user_id', m.userId || '');
    if (m.role) sessionStorage.setItem('cc_role', m.role);
  }, t);
  await page.reload();
  await page.waitForTimeout(1800);
}

export async function openProject(page: Page, pid: string): Promise<void> {
  await page.goto(`${BASE}/#/project/${pid}`);
  await page.reload();
  await page.waitForTimeout(2500);
}

export const wait = (ms = 1200) => new Promise(r => setTimeout(r, ms));

// Меню карточки чата в списке: навести на карточку, затем ⋯.
export async function openChatCardMenu(page: Page, chatName: string): Promise<void> {
  await page.getByText(chatName, { exact: false }).first().hover();
  await page.waitForTimeout(600);
  const trigger = page.locator('button[title="Действия с чатом"]').first();
  await expect(trigger, 'триггер меню карточки чата').toBeVisible({ timeout: 10000 });
  await trigger.click();
  await page.waitForTimeout(500);
}
