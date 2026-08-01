// Локальная копия wait-sync-repro: BASE — vite dev (:5173, прокси /api и /hubs на :5000),
// логин локального инстанса. Оригинал — C:/Users/naych/.claude/plans/ClaudeCodeServer/wait-sync-repro.mjs
// A (наблюдатель) переключает чаты кликами по списку — как живой пользователь.
// B (отправитель) грузит чат диплинком и шлёт сообщения.
// 1) База: оба в чате-1, B шлёт — A должен увидеть live.
// 2) Репро: A открывает чат-2 в офлайне (join падает), сеть возвращается,
//    B шлёт в чат-2 — смотрим, увидит ли A без переключения.
// 3) Лечение: A кликает чат-1 и обратно чат-2 — контент должен появиться.
import { createRequire } from 'module';
const require = createRequire('C:/Sources/ClaudeCodeServer/frontend/package.json');
const { chromium } = require('playwright');

// Куда бьём: :5000 — статика бэкенда (сборка в wwwroot), :5173 — vite dev с текущим кодом
const BASE = process.argv[2] || 'http://localhost:5173';
const USER = 'admin', PASS = '12345';
const SHOT_DIR = 'C:/Users/naych/AppData/Local/Temp/claude/ccs-repro';
const ts = () => new Date().toISOString().slice(11, 19);
const log = (...a) => console.log(ts(), ...a);
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function login(ctx, tag) {
  const page = await ctx.newPage();
  page.on('console', m => {
    const t = m.text();
    if (t.includes('[wait-sync]')) log(`[${tag}] КОНСОЛЬ:`, t);
  });
  await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
  await page.fill('input[placeholder="Имя пользователя"]', USER);
  await page.fill('input[placeholder="Пароль"]', PASS);
  await page.click('button:has-text("Войти")');
  await page.waitForFunction(
    () => !!(localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token')),
    null, { timeout: 20000 });
  log(`[${tag}] залогинен`);
  return page;
}

const rest = (page, path, opts = {}) => page.evaluate(async ({ path, opts }) => {
  const tok = localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token');
  const r = await fetch('/api' + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + tok, ...(opts.headers || {}) },
  });
  const text = await r.text();
  try { return { status: r.status, body: JSON.parse(text) }; } catch { return { status: r.status, body: text }; }
}, { path, opts });

// Жёсткая загрузка чата диплинком (отправитель B — сеть у него всегда есть)
async function openChatHard(page, id) {
  await page.goto(BASE + '/#/chats/' + id, { waitUntil: 'domcontentloaded' });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('textarea', { timeout: 20000 });
}

// Клик по чату в списке (наблюдатель A — работает и в офлайне, список уже отрисован)
async function clickChat(page, name) {
  await page.locator(`text=${name}`).first().click();
  await sleep(1500);
}

const bodyHas = (page, text) => page.evaluate(t => document.body.innerText.includes(t), text);
const waitDiag = page => page.evaluate(() => (window.__ccWaitDiag ? { ...window.__ccWaitDiag } : null));

async function sendFrom(page, chatId, text) {
  await openChatHard(page, chatId);
  await page.fill('textarea', text);
  await page.keyboard.press('Enter');
  log(`[B] отправлено в ${chatId.slice(0, 8)}: «${text.slice(0, 50)}…»`);
}

const browser = await chromium.launch({ headless: true });
const ctxA = await browser.newContext();
const ctxB = await browser.newContext();
let chat1, chat2;
const pageB = await login(ctxB, 'B');
const pageA = await login(ctxA, 'A');

try {
  log('__ccWaitDiag:', JSON.stringify(await waitDiag(pageA)) ?? 'ОТСУТСТВУЕТ');

  // Уборка сирот от прошлых запусков
  const existing = await rest(pageB, '/chats');
  for (const ch of (Array.isArray(existing.body) ? existing.body : [])) {
    if (ch.name?.startsWith('diag-wait-')) {
      const r = await rest(pageB, '/chats/' + ch.id, { method: 'DELETE' });
      log('убран сирота', ch.id.slice(0, 8), r.status);
    }
  }

  const c1 = await rest(pageB, '/chats', { method: 'POST', body: JSON.stringify({ mode: 'auto', name: 'diag-wait-1' }) });
  const c2 = await rest(pageB, '/chats', { method: 'POST', body: JSON.stringify({ mode: 'auto', name: 'diag-wait-2' }) });
  if (c1.status >= 300 || c2.status >= 300) throw new Error('создание чатов: ' + JSON.stringify([c1, c2]));
  chat1 = c1.body.id; chat2 = c2.body.id;
  log('чаты созданы:', chat1.slice(0, 8), chat2.slice(0, 8));

  // A: экран чатов со свежим списком (полная загрузка, чтобы список включал новые чаты)
  await pageA.goto(BASE + '/#/chats/' + chat1, { waitUntil: 'domcontentloaded' });
  await pageA.reload({ waitUntil: 'domcontentloaded' });
  await pageA.waitForSelector('textarea', { timeout: 20000 });
  const listOk = await bodyHas(pageA, 'diag-wait-2');
  log('[A] чат-1 открыт, чат-2 в списке:', listOk ? 'да' : 'НЕТ (репро кликом не выйдет)');

  // Маркер прячем за 100+ символов наполнителя: сервер режет превью lastMessage
  // до 100 символов (SessionManager), значит в сайдбаре маркер оказаться не может —
  // только в настоящей ленте чата. Иначе bodyHas ловит превью списка чатов.
  const pad = 'Это диагностическое сообщение для проверки доставки событий, наполнитель до лимита превью чата. ';

  // --- 1. База: live-доставка в открытый чат ---
  const marker1 = 'диаг-база-' + Date.now();
  await sendFrom(pageB, chat1, `Ответь строго одним словом: ок. ${pad}${pad}Маркер: ${marker1}`);
  let seen = false;
  for (let i = 0; i < 20 && !seen; i++) { await sleep(1000); seen = await bodyHas(pageA, marker1); }
  log(`БАЗА: A видит сообщение B live: ${seen ? 'ДА (доставка в норме работает)' : 'НЕТ (сломано даже без офлайна!)'}`);

  // --- 2. Репро: открыть чат-2 при РЕАЛЬНО упавшем соединении ---
  // SignalR замечает смерть канала только через serverTimeout (60с без сообщений
  // при keepalive 15с) — короткий блип он переживает буферизацией TCP. Держим офлайн
  // 70с, чтобы соединение ушло в Reconnecting, и лишь потом открываем чат-2.
  await ctxA.setOffline(true);
  log('[A] сеть отрублена, ждём 70с — пока клиент заметит смерть канала');
  await sleep(70000);
  await clickChat(pageA, 'diag-wait-2');
  log('[A] чат-2 открыт в офлайне (join должен упасть по таймауту 8с)');
  await sleep(10000);
  await ctxA.setOffline(false);
  log('[A] сеть возвращена, ждём реконнект');
  await sleep(15000);

  const marker2 = 'диаг-репро-' + Date.now();
  await sendFrom(pageB, chat2, `Ответь строго одним словом: ок. ${pad}${pad}Маркер: ${marker2}`);
  let seenA = false, seenB = false;
  for (let i = 0; i < 40; i++) {
    await sleep(1000);
    if (!seenB) seenB = await bodyHas(pageB, marker2);
    seenA = await bodyHas(pageA, marker2);
    if (seenA) break;
  }
  log(`РЕПРО: B видит своё сообщение: ${seenB ? 'да' : 'НЕТ'}; A видит без переключения: ${seenA ? 'ДА (бага нет)' : 'НЕТ — ЧАТ ЗАМЕР, дефект подтверждён'}`);
  await pageA.screenshot({ path: SHOT_DIR + '/repro-frozen.png' });

  // --- 3. Лечение переключением ---
  await clickChat(pageA, 'diag-wait-1');
  await sleep(1000);
  await clickChat(pageA, 'diag-wait-2');
  let healed = false;
  for (let i = 0; i < 15 && !healed; i++) { await sleep(1000); healed = await bodyHas(pageA, marker2); }
  log(`ЛЕЧЕНИЕ: после переключения туда-обратно A видит контент: ${healed ? 'ДА' : 'нет (что-то ещё)'}`);
  await pageA.screenshot({ path: SHOT_DIR + '/repro-healed.png' });

  log('итоговый __ccWaitDiag A:', JSON.stringify(await waitDiag(pageA)));
  log('итоговый __ccWaitDiag B:', JSON.stringify(await waitDiag(pageB)));
} finally {
  for (const id of [chat1, chat2]) {
    if (!id) continue;
    try { const r = await rest(pageB, '/chats/' + id, { method: 'DELETE' }); log('удалён чат', id.slice(0, 8), r.status); }
    catch (e) { log('не удалился чат', id, String(e)); }
  }
  await browser.close();
}
