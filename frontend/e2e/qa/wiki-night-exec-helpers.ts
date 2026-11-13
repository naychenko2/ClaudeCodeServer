// QA · ночь вики на нескольких исполнителях (эпик e5c5f97b): общие куски спеков.
// Стенд, прокси-движки и посев — руками/спеком; здесь только чтение состояния и журналов.

import { expect, type APIRequestContext } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { api, type Api } from './helpers';

export const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
export const ATT = path.join(ROOT, '.cc-attachments', 'qa-night-exec');
export const DATA = path.join(ROOT, 'backend', 'ClaudeHomeServer', 'bin', 'Debug', 'net10.0', 'data');
export const STAND_LOG = path.join(ATT, 'stand.log');

export type SeedGroup = {
  group: string;
  projects: Array<{ id: string; name: string; chats: Array<{ id: string; name: string }> }>;
};
export type Seed = {
  stamp: string;
  ownerId: string;
  owner: string;
  password: string;
  userId: string;
  groups: SeedGroup[];
};

export function readSeed(): Seed {
  return JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
}

export function group(seed: Seed, name: string): SeedGroup {
  const g = seed.groups.find(x => x.group === name);
  expect(g, `в посеве есть группа ${name}`).toBeTruthy();
  return g!;
}

// Вход владельцем (не админом): ночь и экран настроек проверяются на его вики.
export async function ownerApi(request: APIRequestContext, seed: Seed): Promise<{ client: Api; token: string }> {
  const r = await request.post('/api/auth/login', { data: { username: seed.owner, password: seed.password } });
  expect(r.ok(), `вход владельца ${seed.owner}`).toBeTruthy();
  const token = (await r.json()).token as string;
  return { client: api(request, token), token };
}

export function wikiRoot(ownerId: string): string {
  return path.join(DATA, 'wiki', ownerId);
}

export function lastNightFile(ownerId: string): string {
  return path.join(wikiRoot(ownerId), 'keeper', 'nightly-last.json');
}

// Пометки очереди: группа ночи — «важный», отработавшая — «не разбирать» (из очереди уходит).
export async function markGroup(client: Api, g: SeedGroup, mark: 'important' | 'skip'): Promise<void> {
  for (const p of g.projects) {
    for (const c of p.chats) {
      const r = await client.put<{ mark: string }>(`/api/wiki/chats/${c.id}/mark`, { mark });
      expect(r.mark, `пометка ${mark} у ${c.name}`).toBe(mark);
    }
  }
}

export type LastNight = {
  date: string; selected: number; analyzed: number; applied: number; outcome: string;
  rejected: number; inputTokens?: number;
  executors?: Array<{ engine: string; title?: string; analyzed: number; rejected: number; inputTokens?: number; withdrawnAt?: string | null }> | null;
};

export async function readLastNight(ownerId: string): Promise<LastNight | null> {
  const f = lastNightFile(ownerId);
  if (!fs.existsSync(f)) return null;
  return JSON.parse(fs.readFileSync(f, 'utf8')) as LastNight;
}

// Ждём конца прохода: nightlyRunning снимается после записи итога и уведомления.
export async function waitNightEnd(client: Api, timeoutMs = 240_000): Promise<void> {
  const started = Date.now();
  let running = true;
  while (Date.now() - started < timeoutMs) {
    const s = await client.get<{ nightlyRunning: boolean }>('/api/wiki/settings');
    running = s.nightlyRunning;
    if (!running) return;
    await new Promise(r => setTimeout(r, 2000));
  }
  expect(running, `проход ночи уложился в ${timeoutMs} мс`).toBe(false);
}

// Строки итога ночи из журнала стенда: единственная строка прохода — часть инварианта фичи.
export function logSize(): number {
  return fs.existsSync(STAND_LOG) ? fs.statSync(STAND_LOG).size : 0;
}

// Строки итога этого прохода: журнал стенда общий на все прогоны, поэтому читаем только
// хвост после метки, снятой перед запуском ночи.
// Хвост журнала стенда от метки: общий журнал на все прогоны, поэтому читаем только своё окно.
export function logTail(sinceBytes = 0): string {
  if (!fs.existsSync(STAND_LOG)) return '';
  const fd = fs.openSync(STAND_LOG, 'r');
  try {
    const size = fs.fstatSync(fd).size;
    const start = Math.min(sinceBytes, size);
    const buf = Buffer.alloc(size - start);
    fs.readSync(fd, buf, 0, buf.length, start);
    return buf.toString('utf8');
  } finally {
    fs.closeSync(fd);
  }
}

export function summaryLines(ownerId: string, sinceBytes = 0): string[] {
  if (!fs.existsSync(STAND_LOG)) return [];
  const fd = fs.openSync(STAND_LOG, 'r');
  try {
    const size = fs.fstatSync(fd).size;
    const start = Math.min(sinceBytes, size);
    const buf = Buffer.alloc(size - start);
    fs.readSync(fd, buf, 0, buf.length, start);
    return buf.toString('utf8').split('\n').filter(l => l.includes(`Ночь вики ${ownerId} `));
  } finally {
    fs.closeSync(fd);
  }
}

// Запросы прокси-движка: для доказательства одновременной работы двух исполнителей.
export type ProxyCall = { engine: string; path: string; start: number; end: number; status: number };

export function proxyCalls(engine: 'local' | 'night2'): ProxyCall[] {
  const f = path.join(ATT, `exec-${engine}.jsonl`);
  if (!fs.existsSync(f)) return [];
  return fs.readFileSync(f, 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line) as ProxyCall);
}

// Пересечение по времени двух запросов разных движков: ночь реально шла на обеих сразу.
export function hasOverlap(a: ProxyCall[], b: ProxyCall[], minMs = 500): boolean {
  for (const x of a) {
    for (const y of b) {
      const overlap = Math.min(x.end, y.end) - Math.max(x.start, y.start);
      if (overlap * 1000 >= minMs) return true;
    }
  }
  return false;
}

export function notificationCount(items: Array<{ type?: string; createdAt?: string; title?: string; body?: string }>,
  type: string, sinceMs: number): number {
  return items.filter(n => n.type === type && Date.parse(n.createdAt ?? '') >= sinceMs).length;
}
