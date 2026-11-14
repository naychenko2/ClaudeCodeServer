// QA · ночь вики на нескольких исполнителях: запуск одной ночи по плану из current.json.
// Сценарий задаётся файлом .cc-attachments/qa-night-exec/current.json:
//   { "mode": "parallel" | "withdraw" | "probe" | "both-dead" | "empty", "group": "n2a" | … }
// Спек делает ровно «подготовить пометки → запустить ночь → (погасить движок) → дождаться»,
// и складывает метки в run.json. Проверки — в wiki-night-exec-verify.qa.spec.ts: так падение
// проверки не сжигает ночь (перезапускается только проверка).
//
// Гашение движка — SIGTERM своего прокси по PID из proxy-*.pid: для продукта это сетевой отказ
// движка. Модель за прокси (общая локальная) не трогается.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { login } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import {
  ATT, group, lastNightFile, logSize, markGroup, ownerApi, proxyCalls, readSeed, waitNightEnd,
} from './wiki-night-exec-helpers';

type Mode = 'parallel' | 'withdraw' | 'probe' | 'both-dead' | 'empty';
type Plan = { mode: Mode; group: string; order?: 'night2-first' };

const plan = JSON.parse(fs.readFileSync(path.join(ATT, 'current.json'), 'utf8')) as Plan;
const NIGHTLY = '/api/admin/local-actions/wiki-nightly/executors';
// Порядок списка — приоритет выдачи (контракт стора). Для выбывания второй движок ставится
// первым: иначе он получает чат только когда занят первый, и трёх отказов подряд не набрать.
const two = (order?: 'night2-first') => ({
  maxTotal: 2,
  executors: order === 'night2-first'
    ? [{ engine: 'night2', parallel: 1 }, { engine: 'local', parallel: 1 }]
    : [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }],
});

test.setTimeout(480_000);

let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

const kill = (pidFile: string) => {
  const pid = Number(fs.readFileSync(path.join(ATT, pidFile), 'utf8').trim());
  expect(pid, `PID из ${pidFile}`).toBeGreaterThan(100);
  process.kill(pid, 'SIGTERM');
  return pid;
};

test(`ночь по плану: ${plan.mode} / ${plan.group}`, async () => {
  const seed = readSeed();
  const g = group(seed, plan.group);
  const owner = await ownerApi(request, seed);
  const adminToken = await login(request);

  // Исполнители: пустой список — сценарий «прежнее поведение», иначе две модели.
  const put = await request.put(NIGHTLY, {
    headers: { Authorization: `Bearer ${adminToken}` },
    data: plan.mode === 'empty' ? { maxTotal: 0, executors: [] } : two(plan.order),
  });
  expect(put.status(), 'список исполнителей записан').toBe(200);

  // Очередь: все группы «не разбирать», целевая — «важный». Так ночь видит ровно эту группу.
  for (const other of seed.groups) await markGroup(owner.client, other, 'skip');
  await markGroup(owner.client, g, 'important');

  // Повторный error той же даты владельцу второй раз не сообщается (правило J-O1/J-J4).
  // Для сценария «оба погашены» убираем прошлый итог: проверяется уведомление прохода,
  // а не правило дедупликации.
  if (plan.mode === 'both-dead') {
    const last = lastNightFile(seed.ownerId);
    if (fs.existsSync(last)) fs.rmSync(last);
  }

  const before = { local: proxyCalls('local').length, night2: proxyCalls('night2').length };
  const logMark = logSize();
  const startedAt = Date.now();
  const run = await owner.client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
  expect(run.started, 'ручной запуск принят').toBe(true);

  // Гашение движка посреди ночи: ждём, пока второй движок отработает хотя бы один вызов,
  // и только тогда гасим — иначе это «мёртв с начала», а не «погасили посреди ночи».
  const killed: { night2?: number; local?: number } = {};
  if (plan.mode === 'both-dead') {
    // Оба погашены с начала ночи: «живой хоть один» — условие продолжения, и проверяется
    // именно оно (ряд отказов у каждого движка набирается тремя разными чатами).
    killed.night2 = kill('proxy-night2.pid');
    killed.local = kill('proxy-local.pid');
  } else if (plan.mode === 'withdraw' || plan.mode === 'probe') {
    const deadline = Date.now() + 180_000;
    while (Date.now() < deadline) {
      const done = proxyCalls('night2').slice(before.night2)
        .filter(c => c.path.includes('/v1/chat/completions') && c.status === 200);
      if (done.length >= 1) break;
      await new Promise(r => setTimeout(r, 400));
    }
    killed.night2 = kill('proxy-night2.pid');
  }

  await waitNightEnd(owner.client, 420_000);

  fs.writeFileSync(path.join(ATT, 'run.json'), JSON.stringify({
    ...plan, logMark, startedAt, before, killed,
  }, null, 2), 'utf8');
});
