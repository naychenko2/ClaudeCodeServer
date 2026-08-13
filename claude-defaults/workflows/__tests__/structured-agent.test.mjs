// Хёрнесс structuredAgent: покрывает ЧЕТЫРЕ ветки отказов агента:
//   1) первая попытка бросила → ретрай состоялся;
//   2) первая попытка вернула null → ретрай состоялся;
//   3) обе попылки бросили → одна запись в лог, возврат null, стадия не падает;
//   4) обе попытки вернули null → одна запись в лог, возврат null, стадия не падает.
//
// Идея: импортируем каждый workflow-файл в среде с моками Workflow-API
// (agent / log / phase / parallel / pipeline) и подменяем `agent` сценаристами.
// КРИТИЧНО: structuredAgent определён ВНУТРИ каждого workflow-файла как локальная
// async function — снаружи недоступен. Поэтому грузим файл через data: URL c подменой
// исходника: перехватываем `import()` через Module._load нельзя (ESM), но можем
// вынести определение хелпера через динамический eval: читаем файл, вырезаем
// structuredAgent в eval'ом скопированное тело и зовём напрямую. Заодно проверяем
// вызовы через `agent`-mock, как их видит реальный прогон (через те же мок-функции).

import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import path from 'node:path';
import assert from 'node:assert/strict';

// ---- Утилита: вытащить тело функции structuredAgent из исходника ----
// Хелпер — async function в каждом workflow-файле; ловим от `async function structuredAgent`
// до закрывающей `}` с балансом скобок (наивный, но файл у нас стабильный).
function extractStructuredAgent(src) {
  const marker = 'async function structuredAgent(';
  const start = src.indexOf(marker);
  if (start < 0) throw new Error('structuredAgent not found');
  let i = src.indexOf('{', start);
  assert.notStrictEqual(i, -1, 'open brace');
  let depth = 1;
  let j = i + 1;
  while (depth > 0 && j < src.length) {
    const c = src[j];
    if (c === '{') depth++;
    else if (c === '}') depth--;
    j++;
  }
  return src.slice(start, j);
}

// ---- Загрузить хелпер в контекст с моками agent/log и прогнать сценарий ----
// Чтобы тест был детерминированным, делаем минимальный ESM-«контекст» через
// `vm.createContext` + `vm.Script` (исполняет синхронно, но функция async возвращает
// Promise — это работает в vm).
function makeEnv(agentMock) {
  const calls = { log: [] };
  const ctx = {
    agent: agentMock,
    log: (msg) => calls.log.push(String(msg)),
    // подавляем обращения к phase/parallel/pipeline: structuredAgent их не зовёт
    phase: () => {},
    parallel: (xs) => Promise.all(xs.map(f => f())),
    pipeline: (xs, stage) => Promise.all(xs.map(s => stage(s, s, 0))),
  };
  vm.createContext(ctx);
  return { ctx, calls };
}

async function runHelper(src, agentMock) {
  const body = extractStructuredAgent(src);
  const { ctx, calls } = makeEnv(agentMock);
  // Заворачиваем вызов в IIFE async, чтобы получить Promise
  const script = new vm.Script(`(async () => { ${body}; return await structuredAgent('PROMPT', { label: 'TEST-LABEL', schema: {} }); })()`);
  const promise = script.runInContext(ctx);
  const result = await promise;
  return { result, calls };
}

// ---- Загрузка исходников ----
const WORKFLOWS_DIR = path.resolve('claude-defaults/workflows');
const FILES = [
  'red-team.js',
  'panel-of-experts.js',
  'review-consilium.js',
  'team-implement.js',
];
const sources = {};
for (const f of FILES) {
  sources[f] = await readFile(path.join(WORKFLOWS_DIR, f), 'utf8');
}

// ---- Сценарии ----
// Каждый сценарий: мок agent возвращает функцию, которая по очереди выдаёт
// заданные ответы (или кидает). Фиксируем вызовы мока.
function sequence(responses) {
  const calls = [];
  const fn = async (prompt, opts) => {
    calls.push({ prompt, label: opts && opts.label });
    const idx = calls.length - 1;
    const r = responses[idx];
    if (r && r.throw) throw r.throw;
    return r ? r.value : undefined;
  };
  fn.calls = calls;
  return fn;
}

// ---- Тесты ----
const results = [];
async function runTest(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log(`  ✓ ${name}`);
  } catch (err) {
    results.push({ name, ok: false, err });
    console.log(`  ✗ ${name}\n      ${err.message}`);
  }
}

console.log('structuredAgent harness — 4 ветки × 4 файла\n');

for (const file of FILES) {
  console.log(`\n# ${file}`);
  const src = sources[file];

  // Ветка 1: первая попытка бросила → ретрай состоялся, вернулся результат ретрая
  await runTest('1) первая попытка бросила → ретрай состоялся (возврат)', async () => {
    const agentMock = sequence([
      { throw: new Error('boom-1') },
      { value: { ok: true, from: 'retry' } },
    ]);
    const { result, calls } = await runHelper(src, agentMock);
    assert.deepStrictEqual(result, { ok: true, from: 'retry' });
    assert.strictEqual(agentMock.calls.length, 2, 'должно быть два вызова');
    assert.ok(agentMock.calls[1].label.includes('TEST-LABEL · повтор'), `retry label: ${agentMock.calls[1].label}`);
    assert.strictEqual(calls.log.length, 0, 'log не должен сработать при успешном ретрае');
  });

  // Ветка 2: первая попытка вернула null → ретрай состоялся, вернулся результат ретрая
  await runTest('2) первая попытка вернула null → ретрай состоялся (возврат)', async () => {
    const agentMock = sequence([
      { value: null },
      { value: { ok: true, from: 'retry' } },
    ]);
    const { result, calls } = await runHelper(src, agentMock);
    assert.deepStrictEqual(result, { ok: true, from: 'retry' });
    assert.strictEqual(agentMock.calls.length, 2);
    assert.ok(agentMock.calls[1].label.includes('TEST-LABEL · повтор'));
    assert.strictEqual(calls.log.length, 0);
  });

  // Ветка 3: обе попылки бросили → одна запись в лог, возврат null, стадия не падает
  await runTest('3) обе попылки бросили → log + null, стадия не падает', async () => {
    const agentMock = sequence([
      { throw: new Error('first-fail') },
      { throw: new Error('second-fail') },
    ]);
    const { result, calls } = await runHelper(src, agentMock);
    assert.strictEqual(result, null, 'возврат должен быть null');
    assert.strictEqual(agentMock.calls.length, 2);
    assert.strictEqual(calls.log.length, 1, `должна быть ровно одна запись в log (получено ${calls.log.length})`);
    const msg = calls.log[0];
    assert.ok(msg.includes('TEST-LABEL'), 'log должен содержать label');
    assert.ok(msg.includes('упал с ошибкой: first-fail'), `log должен различать причину первой попытки: ${msg}`);
    assert.ok(msg.includes('упал с ошибкой: second-fail'), `log должен различать причину второй попытки: ${msg}`);
    assert.ok(!msg.includes('не вызвал StructuredOutput'), 'при чистых throw в логе не должно быть формулировки "не вызвал StructuredOutput"');
  });

  // Ветка 4: обе попылки вернули null → одна запись в лог, возврат null, стадия не падает
  await runTest('4) обе попытки вернули null → log + null, стадия не падает', async () => {
    const agentMock = sequence([
      { value: null },
      { value: null },
    ]);
    const { result, calls } = await runHelper(src, agentMock);
    assert.strictEqual(result, null);
    assert.strictEqual(agentMock.calls.length, 2);
    assert.strictEqual(calls.log.length, 1);
    const msg = calls.log[0];
    assert.ok(msg.includes('TEST-LABEL'));
    assert.ok(msg.includes('не вызвал StructuredOutput'), `log должен использовать формулировку "не вызвал StructuredOutput": ${msg}`);
  });
}

const failed = results.filter(r => !r.ok);
console.log(`\nИтог: ${results.length - failed.length}/${results.length} прошло`);
if (failed.length) {
  for (const f of failed) console.error(`FAILED: ${f.name}\n${f.err.stack || f.err.message}`);
  process.exit(1);
}