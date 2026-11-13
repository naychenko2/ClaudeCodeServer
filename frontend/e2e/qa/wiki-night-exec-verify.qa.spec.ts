// QA · ночь вики на нескольких исполнителях: проверки по итогу ночи, запущенной
// wiki-night-exec-run.qa.spec.ts. Сценарий — из run.json (тот же current.json + метки запуска).
// Проверка не повторяет ночь: падение проверки перезапускается отдельно и ночь не сжигает.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import {
  ATT, group, lastNightFile, logTail, ownerApi, proxyCalls, readLastNight, readSeed, summaryLines,
  type LastNight,
} from './wiki-night-exec-helpers';

type Mode = 'parallel' | 'withdraw' | 'probe' | 'both-dead' | 'empty';
type Run = {
  mode: Mode; group: string; logMark: number; startedAt: number;
  before: { local: number; night2: number }; killed: { night2?: number; local?: number };
};

test.setTimeout(120_000);

let request: APIRequestContext;
let run: Run;

test.beforeAll(async ({ playwright, baseURL }) => {
  // run.json пишет спек запуска; читается здесь, а не на уровне модуля: Playwright грузит
  // файлы прогона до выполнения тестов, и модульное чтение падало бы при первом прогоне пары.
  run = JSON.parse(fs.readFileSync(path.join(ATT, 'run.json'), 'utf8')) as Run;
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

const chatCalls = (engine: 'local' | 'night2', from: number) =>
  proxyCalls(engine).slice(from).filter(c => c.path.includes('/v1/chat/completions'));

const overlap = (a: ReturnType<typeof chatCalls>, b: ReturnType<typeof chatCalls>) => {
  for (const x of a) for (const y of b) if (Math.min(x.end, y.end) - Math.max(x.start, y.start) > 0.5) return true;
  return false;
};

test('проверка ночи по меткам запуска', async () => {
  const seed = readSeed();
  const groupChats = group(seed, run.group).projects.reduce((n, p) => n + p.chats.length, 0);
  const owner = await ownerApi(request, seed);
  const last = await readLastNight(seed.ownerId);
  expect(last, 'итог ночи записан').toBeTruthy();
  const lines = summaryLines(seed.ownerId, run.logMark);
  expect(lines.length, 'строка итога прохода есть').toBeGreaterThanOrEqual(1);

  // Уведомление: одно на проход (фильтр kind у ручки — это вид info/warning, не type).
  const notifications = await owner.client.get<{
    items: Array<{ type?: string; createdAt?: string; title?: string; body?: string }>
  }>('/api/notifications?limit=20');
  const mine = notifications.items.filter(n => n.type === 'wiki_nightly' && Date.parse(n.createdAt ?? '') >= run.startedAt);
  expect(mine.length, 'одно уведомление о проходе').toBe(1);
  const body = mine[0].body ?? '';

  if (run.mode === 'parallel') {
    expect(last!.outcome, `исход ночи (${last!.outcome})`).toBe('completed');
    expect(last!.executors?.map(e => e.engine), 'разбивка по двум движкам в порядке стора').toEqual(['local', 'night2']);
    for (const e of last!.executors!) {
      expect(e.analyzed, `движок ${e.engine} получил работу`).toBeGreaterThan(0);
      expect(e.withdrawnAt ?? null, `движок ${e.engine} не выбывал`).toBeNull();
    }
    expect(last!.executors!.reduce((s, e) => s + e.analyzed, 0), 'попыток не меньше, чем чатов в группе')
      .toBeGreaterThanOrEqual(groupChats);
    expect(last!.selected, 'чатов прошло').toBeGreaterThanOrEqual(groupChats);
    expect(last!.executors!.reduce((s, e) => s + (e.inputTokens ?? 0), 0), 'вход по движкам = входу ночи')
      .toBe(last!.inputTokens ?? 0);

    expect(lines[0], 'строка журнала называет оба движка').toContain('исполнители local ');
    expect(lines[0], 'второй движок в строке').toContain('night2 ');
    expect(lines[0], 'при двух исполнителях считается попытками').toContain('попыток');
    expect(lines[0], 'выбывших в обычной ночи нет').not.toContain('выбыл');
    expect(body, 'в уведомлении нет фразы о выбывании').not.toContain('выбыла в');

    const local = chatCalls('local', run.before.local).filter(c => c.status === 200);
    const second = chatCalls('night2', run.before.night2).filter(c => c.status === 200);
    expect(local.length, 'вызовы на основной локали').toBeGreaterThan(0);
    expect(second.length, 'вызовы на втором движке').toBeGreaterThan(0);
    expect(overlap(local, second), 'оба движка работали одновременно').toBeTruthy();
    return;
  }

  if (run.mode === 'withdraw') {
    expect(last!.outcome, `ночь доехала на живом (${last!.outcome})`).toBe('completed');
    const byEngine = Object.fromEntries((last!.executors ?? []).map(e => [e.engine, e]));
    expect(byEngine.local, 'живой движок в разбивке').toBeTruthy();
    expect(byEngine.local.analyzed, 'живой движок добрал чаты').toBeGreaterThan(0);
    expect(byEngine.local.withdrawnAt ?? null, 'живой движок не выбыл').toBeNull();
    expect(byEngine.night2, 'погашенный движок в разбивке').toBeTruthy();
    expect(byEngine.night2.withdrawnAt, 'погашенный движок выбыл со временем').toBeTruthy();
    expect(byEngine.night2.analyzed, 'у погашенного есть попытки').toBeGreaterThanOrEqual(3);

    expect(lines[0], 'выбывание в строке журнала').toContain('night2');
    expect(lines[0], 'время выбывания в строке журнала').toMatch(/night2 \d+\/\d+ выбыл \d{2}:\d{2}/);
    expect(body, 'фраза о выбывшей модели в уведомлении').toMatch(/Модель .* сделала \d+ попы?т?к?и?, выбыла в \d{2}:\d{2}/);
    expect(body, 'уведомление говорит, кто продолжил').toContain('разбор продолжила');

    // Повтор — не больше одного на чат: в журнале каждый чат назван повтором максимум раз.
    const retries = logTail(run.logMark).split('\n').filter(l => l.includes('первый повтор'));
    const ids = retries.map(l => (l.match(/чат ([0-9a-f-]{36})/) ?? [])[1]).filter(Boolean);
    expect(new Set(ids).size, 'каждый чат повторён не больше раза').toBe(ids.length);
    expect(ids.length, 'повторы были — иначе нечего проверять').toBeGreaterThan(0);

    // Движок действительно погашен: прокси после SIGTERM не ответил ни на один запрос —
    // в его журнале ровно один успешный вызов (тот, после которого его и погасили), а отказы
    // трёх чатов видны в журнале стенда одинаковой причиной «локальная модель недоступна».
    const done2 = chatCalls('night2', run.before.night2).filter(c => c.status === 200);
    expect(done2.length, 'погашенный прокси ответил ровно один раз').toBe(1);
    const down = logTail(run.logMark).split('\n')
      .filter(l => l.includes('отказан:') && l.includes('локальная модель недоступна'));
    expect(down.length, 'три отказа «модель недоступна» в журнале стенда').toBeGreaterThanOrEqual(3);
    return;
  }

  if (run.mode === 'probe') {
    expect(last!.outcome, `ночь доехала на живом (${last!.outcome})`).toBe('completed');
    const byEngine = Object.fromEntries((last!.executors ?? []).map(e => [e.engine, e]));
    expect(byEngine.local?.analyzed ?? 0, 'живой движок работал').toBeGreaterThan(0);
    expect(byEngine.night2?.withdrawnAt ?? null, 'неготовый движок не выбывает — его пропускают').toBeNull();
    expect(lines[0], 'выбывания в строке нет').not.toContain('выбыл');
    expect(body, 'фразы о выбывании нет').not.toContain('выбыла в');
    // После гашения второй движок новых вызовов не получает: проба готовности его отсекла.
    const afterKill = chatCalls('night2', run.before.night2).filter(c => c.start * 1000 > run.startedAt + 60_000);
    expect(afterKill.length, 'после гашения новых вызовов нет').toBe(0);
    return;
  }

  if (run.mode === 'both-dead') {
    expect(last!.outcome, `оба погашены — итог error (${last!.outcome})`).toBe('error');
    expect(last!.executors?.length, 'разбивка есть').toBe(2);
    for (const e of last!.executors!) {
      expect(e.withdrawnAt, `движок ${e.engine} выбыл`).toBeTruthy();
    }
    expect(lines.some(l => l.includes('выбыл')), 'выбывание в строке журнала').toBeTruthy();
    expect(body, 'уведомление говорит, что разбор не продолжен').toContain('разбор не продолжен');
    return;
  }

  // empty: пустой список — прежнее поведение ночи (маршрут места), блока исполнителей нет.
  expect(last!.outcome, `исход ночи (${last!.outcome})`).toBe('completed');
  // Пустой список — тоже «исполнителей нет»: фронт рисует блок только при length > 0.
  expect(last!.executors ?? [], 'разбивки по исполнителям нет').toEqual([]);
  expect(lines[0], 'в строке журнала нет хвоста об исполнителях').not.toContain('исполнители');
  const settings = await owner.client.get<{ nightly: { executors: unknown[] } }>('/api/wiki/settings');
  expect(settings.nightly.executors, 'список исполнителей у владельца пуст').toEqual([]);

  // Старый файл итога без поля executors читается: GET не падает, блок не рисуется.
  const raw = JSON.parse(fs.readFileSync(lastNightFile(seed.ownerId), 'utf8')) as Record<string, unknown>;
  delete raw.executors;
  fs.writeFileSync(lastNightFile(seed.ownerId), JSON.stringify(raw, null, 2), 'utf8');
  const legacy = await owner.client.get<{ lastNight: LastNight | null }>('/api/wiki/settings');
  expect(legacy.lastNight, 'старый итог прочитан').toBeTruthy();
  expect(legacy.lastNight!.executors ?? null, 'исполнителей в старом итоге нет').toBeNull();
  expect(legacy.lastNight!.selected, 'числа старого итога читаются').toBe(last!.selected);

  // Возврат файла как был: ночь закончилась, дальше проверяется интерфейс.
  fs.writeFileSync(lastNightFile(seed.ownerId), JSON.stringify(last, null, 2), 'utf8');
});
