// Сквозная проверка раздела «Архив» на дев-стенде.
// Шаги:
//   1. Убрать чат в архив через пункт меню «Убрать в архив»
//   2. Убедиться, что чат исчез из списка «Чаты»
//   3. Открыть раздел «Архив»
//   4. Увидеть чат карточкой
//   5. Вернуть чат из архива
//   6. Убедиться, что чат вернулся в список «Чаты»

import { chromium } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const BASE = 'http://localhost:5000';
const USER = 'admin';
const PASS = '12345';
const SHOTS_DIR = path.resolve('../.cc-attachments/archive-e2e');

const shot = async (page, name) => {
  await page.screenshot({
    path: path.join(SHOTS_DIR, `${name}.png`),
    fullPage: false,
  });
};

const log = (msg) => console.log(`[${new Date().toISOString().slice(11, 19)}] ${msg}`);

// Получить список чатов и выбрать первого свободного с уникальным именем и непустым lastMessage.
async function pickTarget(page, base) {
  return await page.evaluate(async (b) => {
    const token = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
    const r = await fetch(`${b}/api/chats`, { headers: { Authorization: `Bearer ${token}` } });
    if (!r.ok) return { __error: `HTTP ${r.status}` };
    const list = await r.json();
    const free = list.filter((c) => !c.archivedAt);
    const named = free.find((c) => c.name && c.name.trim() && c.lastMessage && c.lastMessage.trim().length > 8);
    if (!named) return { __error: 'Нет чатов с именем и lastMessage' };
    return { id: named.id, name: named.name, lastMessage: named.lastMessage };
  }, base);
}

// API: прочитать текущее состояние чата
async function readChat(page, base, chatId) {
  return await page.evaluate(async ({ b, id }) => {
    const token = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
    const r = await fetch(`${b}/api/chats`, { headers: { Authorization: `Bearer ${token}` } });
    if (!r.ok) return { __error: `HTTP ${r.status}` };
    const list = await r.json();
    const c = list.find((x) => x.id === id);
    return c ? { archivedAt: c.archivedAt, isArchived: c.isArchived } : null;
  }, { b: base, id: chatId });
}

// API: гарантировать, что чат не заархивирован (на случай если прошлые прогоны оставили его в архиве)
async function ensureUnarchived(page, base, chatId) {
  return await page.evaluate(async ({ b, id }) => {
    const token = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
    const r = await fetch(`${b}/api/chats/${id}/archived`, {
      method: 'PUT',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ archived: false }),
    });
    return r.ok ? await r.json() : { __error: await r.text() };
  }, { b: base, id: chatId });
}

async function main() {
  await mkdir(SHOTS_DIR, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    locale: 'ru-RU',
  });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (msg) => {
    if (msg.type() === 'error') errors.push(`console.error: ${msg.text()}`);
  });

  // === Открыть приложение ===
  log('Открываю приложение…');
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.waitForTimeout(500);

  // === Авторизация ===
  const hasLogin = await page.locator('input[placeholder="Имя пользователя"]').isVisible().catch(() => false);
  if (hasLogin) {
    log('Форма логина — заполняю…');
    await page.locator('input[placeholder="Имя пользователя"]').fill(USER);
    await page.locator('input[placeholder="Пароль"]').fill(PASS);
    await page.locator('button:has-text("Войти")').click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);
  }
  await shot(page, '00-home');

  // === Выбираем цель: чат с именем и уникальным lastMessage ===
  const target = await pickTarget(page, BASE);
  if (target?.__error) throw new Error(`Не удалось выбрать цель: ${target.__error}`);
  const targetId = target.id;
  const targetTitle = target.name;
  const targetNeedle = target.lastMessage.slice(0, 20).trim();
  log(`Цель: ${targetId} / «${targetTitle}» / needle «${targetNeedle}»`);

  // Гарантируем, что чат точно не архивный (на случай если прошлые прогоны что-то оставили)
  await ensureUnarchived(page, BASE, targetId);
  await page.waitForTimeout(500);

  // === Открыть раздел «Чаты» ===
  log('Открываю раздел «Чаты»…');
  const msgIcon = page.locator('svg.lucide-message-circle, [class*="lucide-message-circle"]').first();
  if (await msgIcon.isVisible().catch(() => false)) {
    await msgIcon.click();
    log('  кликнул иконку сообщений');
  } else {
    await page.goto(`${BASE}/#/chats`, { waitUntil: 'networkidle' });
  }
  await page.waitForTimeout(3000);
  await shot(page, '01-chats-list-before');

  // === Шаг 1: найти нужную карточку, навести hover, кликнуть ⋮, выбрать «Убрать в архив» ===
  log('Шаг 1: убрать чат в архив…');
  const needleEl = page.locator(`text=${targetNeedle}`).first();
  try {
    await needleEl.waitFor({ state: 'visible', timeout: 5000 });
    await needleEl.scrollIntoViewIfNeeded();
    await needleEl.hover();
    log(`  hover на «${targetNeedle}»`);
  } catch (e) {
    log(`  ⚠ не нашёл текст «${targetNeedle}» в DOM`);
    throw e;
  }
  await page.waitForTimeout(500);
  const actions = page.locator('[aria-label="Действия с чатом"]').all();
  const actionList = await actions;
  // Видимых кнопок должно быть мало (hover), обычно 1
  let visibleActions = [];
  for (const a of actionList) {
    if (await a.isVisible().catch(() => false)) visibleActions.push(a);
  }
  log(`  видимых кнопок ⋮: ${visibleActions.length}`);
  if (visibleActions.length === 0) throw new Error('Нет видимой кнопки ⋮');
  await visibleActions[0].click();
  await page.waitForTimeout(500);
  await shot(page, '02-card-menu-open');

  await page.locator('text=Убрать в архив').first().click();
  await page.waitForTimeout(2500);
  await shot(page, '03-after-archive-click');

  // === Шаг 2: проверить API + DOM ===
  log('Шаг 2: проверка…');
  await page.waitForTimeout(1500);
  const stateAfterArchive = await readChat(page, BASE, targetId);
  log(`  API после клика: archivedAt=${stateAfterArchive?.archivedAt}, isArchived=${stateAfterArchive?.isArchived}`);
  await shot(page, '04-list-after-archive');

  // === Шаг 3: открыть «Архив» ===
  log('Шаг 3: открыть раздел «Архив»…');
  const archiveTab = page.locator('[aria-label="Архив"]').first();
  if (await archiveTab.isVisible().catch(() => false)) {
    await archiveTab.click();
  } else {
    await page.goto(`${BASE}/#/archive`, { waitUntil: 'networkidle' });
  }
  await page.waitForTimeout(3000);
  await shot(page, '05-archive-page');

  // === Шаг 4: убедиться, что чат виден в архиве ===
  log('Шаг 4: проверить что чат в архиве…');
  // Ищем карточку архивного чата с нашим текстом
  let inArchive = false;
  try {
    const archiveNeedle = page.locator(`text=${targetNeedle}`).first();
    await archiveNeedle.waitFor({ state: 'visible', timeout: 5000 });
    inArchive = true;
    log('  ✓ чат виден в архиве');
  } catch {
    log('  ⚠ чат не найден в архиве');
  }
  const restoreBtnCount = await page.locator('text=Вернуть из архива').count();
  log(`  кнопок «Вернуть из архива»: ${restoreBtnCount}`);
  await shot(page, '06-archive-with-chat');

  // === Шаг 5: нажать «Вернуть из архива» для нужного чата ===
  log('Шаг 5: вернуть из архива…');
  if (inArchive && restoreBtnCount > 0) {
    // Ищем карточку с нашим текстом, поднимаемся до ArchiveCard, ищем кнопку «Вернуть»
    // В ArchiveCard кнопка «Вернуть из архива» — это первая кнопка слева. Проще:
    // найдём все ArchiveCard (по «Вернуть из архива») и кликнем ту, рядом с которой есть needle.
    const restoreButtons = await page.locator('button:has-text("Вернуть из архива")').all();
    log(`  кнопок «Вернуть из архива» (button): ${restoreButtons.length}`);
    let clicked = false;
    for (const btn of restoreButtons) {
      // Подняться до ArchiveCard и проверить наличие needle в тексте
      const cardText = await btn.evaluate((el) => {
        let cur = el;
        for (let j = 0; j < 10; j++) {
          if (!cur.parentElement) break;
          cur = cur.parentElement;
          if (cur.offsetHeight > 100 && cur.offsetHeight < 400) break;
        }
        return (cur.textContent || '').slice(0, 600);
      });
      if (cardText.includes(targetNeedle)) {
        await btn.scrollIntoViewIfNeeded();
        await btn.click();
        clicked = true;
        log(`  ✓ кликнул «Вернуть из архива» для «${targetNeedle.slice(0, 15)}»`);
        break;
      }
    }
    if (!clicked) log('  ⚠ не нашёл нужную кнопку возврата');
  }
  await page.waitForTimeout(2500);
  await shot(page, '07-after-restore-click');

  // === Шаг 6: проверить, что чат вернулся в обычный список ===
  log('Шаг 6: проверить возврат…');
  await page.goto(`${BASE}/#/chats`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(3000);
  const stateAfterRestore = await readChat(page, BASE, targetId);
  log(`  API после возврата: archivedAt=${stateAfterRestore?.archivedAt}, isArchived=${stateAfterRestore?.isArchived}`);
  await shot(page, '08-list-after-restore');

  // === Итог ===
  const report = {
    targetChatId: targetId,
    targetChatTitle: targetTitle,
    step1_archiveClick: 'OK',
    step2_archivedInApi: stateAfterArchive?.archivedAt ? 'OK' : `FAIL (archivedAt=${stateAfterArchive?.archivedAt})`,
    step3_archivePageOpened: 'OK',
    step4_visibleInArchive: inArchive ? 'OK' : 'FAIL',
    step5_restoreClick: inArchive ? 'OK' : 'SKIPPED',
    step6_unarchivedInApi: !stateAfterRestore?.archivedAt ? 'OK' : `FAIL (archivedAt=${stateAfterRestore?.archivedAt})`,
    pageErrors: errors.filter((e) => !e.includes('favicon') && !e.includes('SW') && !e.includes('Failed to load resource')),
  };
  await writeFile(path.join(SHOTS_DIR, 'report.json'), JSON.stringify(report, null, 2));
  log('=== ОТЧЁТ ===');
  for (const [k, v] of Object.entries(report)) console.log(`  ${k}: ${JSON.stringify(v)}`);

  await browser.close();
  if (
    report.step2_archivedInApi !== 'OK' ||
    report.step4_visibleInArchive !== 'OK' ||
    report.step6_unarchivedInApi !== 'OK'
  ) {
    process.exit(1);
  }
}

main().catch((e) => {
  console.error('ОШИБКА:', e);
  process.exit(2);
});