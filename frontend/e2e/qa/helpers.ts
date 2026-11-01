import { rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { expect, type APIRequestContext, type Page } from '@playwright/test';

// Общие хелперы QA-спеков (docs/operations/qa-stand.md). Учётка — только из окружения,
// дефолты те же, что в остальных e2e.
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

export async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), `логин ${USER} должен пройти (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

// Тонкая обёртка над request: Bearer, проверка кода и разбор JSON (пустое тело → undefined).
export function api(request: APIRequestContext, token: string) {
  const headers = { Authorization: `Bearer ${token}` };
  async function call<T = unknown>(method: 'get' | 'post' | 'put' | 'patch' | 'delete', url: string, data?: unknown, okCodes: number[] = []): Promise<T> {
    const r = await request[method](url, { headers, ...(data === undefined ? {} : { data }) });
    const ok = r.ok() || okCodes.includes(r.status());
    expect(ok, `${method.toUpperCase()} ${url} → ${r.status()} ${ok ? '' : await r.text()}`).toBeTruthy();
    const text = await r.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
  return {
    get: <T = unknown>(url: string) => call<T>('get', url),
    post: <T = unknown>(url: string, data?: unknown) => call<T>('post', url, data),
    put: <T = unknown>(url: string, data?: unknown) => call<T>('put', url, data),
    patch: <T = unknown>(url: string, data?: unknown) => call<T>('patch', url, data),
    // 404 при уборке не ошибка: сущность могла уйти каскадом вместе с проектом
    del: (url: string) => call('delete', url, undefined, [404]),
  };
}

export type Api = ReturnType<typeof api>;

// Засев: каждая функция возвращает id и кладёт его в `created` — afterAll чистит в обратном порядке.
export function seed(client: Api) {
  const created: Array<() => Promise<unknown>> = [];
  const dirs: string[] = [];
  return {
    // rootPath по умолчанию — каталог в os.tmpdir(); каталоги под tmpdir cleanup() сносит с диска
    async project(name: string, rootPath = `${tmpdir()}/${name}`): Promise<{ id: string; name: string }> {
      if (rootPath.startsWith(tmpdir() + '/')) dirs.push(rootPath);
      const p = await client.post<{ id: string; name: string }>('/api/projects', { name, rootPath, createDirectory: true });
      created.push(() => client.del(`/api/projects/${p.id}`));
      return p;
    },
    async session(projectId: string, name: string): Promise<{ id: string }> {
      const s = await client.post<{ id: string }>(`/api/projects/${projectId}/sessions`, { name });
      created.push(() => client.del(`/api/projects/${projectId}/sessions/${s.id}`));
      return s;
    },
    // projectId бэкенд игнорирует: POST /api/tasks создаёт личную задачу (TasksController.cs:310),
    // к проекту её этим вызовом не привязать
    async task(title: string, projectId?: string): Promise<{ id: string; title: string }> {
      const t = await client.post<{ id: string; title: string }>('/api/tasks', { title, ...(projectId ? { projectId } : {}) });
      created.push(() => client.del(`/api/tasks/${t.id}`));
      return t;
    },
    // Уборка: от последнего созданного к первому; сбой одного шага не мешает остальным
    async cleanup(): Promise<void> {
      for (const undo of created.splice(0).reverse()) {
        try { await undo(); } catch { /* уборка best-effort */ }
      }
      for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
    },
  };
}

// Ошибки консоли и необработанные исключения страницы. Предупреждения не берём:
// их шум на стенде скрывает настоящие провалы (нужны — смотри notes-module.spec.ts).
export function collectConsoleErrors(page: Page): string[] {
  const sink: string[] = [];
  page.on('console', m => { if (m.type() === 'error') sink.push(`[error] ${m.text()}`); });
  page.on('pageerror', e => sink.push(`[pageerror] ${e}`));
  return sink;
}
