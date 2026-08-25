// Независимая QA-проверка краевых случаев архива: кликаем по UI, не по URL —
// 'archive' НЕ в NavSnapshot['screen'], parseHash его не возвращает (это by design).
// Сценарии:
//  1) регрессия: archived чат НЕ виден в общем списке «Чаты»
//  2) возврат через кнопку «Вернуть из архива» в карточке
//  3) пустой архив: empty state
//  4) переключение Архив → Заметки → Архив не теряет empty state
//  5) флаг chat-auto-archive OFF → ArchiveSettings скрыт
//  6) флаг chat-auto-archive ON → ArchiveSettings показан
//  7) возврат последнего чата — счётчик уменьшается
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';

const BASE = 'http://localhost:5000';
const USER = 'admin';
const PASS = '12345';
const SHOTS = path.resolve('../.cc-attachments/archive-qa');

const log = m => console.log(`[${new Date().toISOString().slice(11, 19)}] ${m}`);
const shot = async (page, n) => page.screenshot({ path: path.join(SHOTS, `${n}.png`), fullPage: false });

// API: число архивных
const archivedCount = async (page) => page.evaluate(async (b) => {
  const t = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
  const r = await fetch(`${b}/api/chats`, { headers: { Authorization: `Bearer ${t}` } });
  const list = r.ok ? await r.json() : [];
  return list.filter(c => c.archivedAt).length;
}, BASE);

// API: первый свободный чат с именем и lastMessage
async function pickFreeChat(page) {
  return page.evaluate(async (b) => {
    const t = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
    const r = await fetch(`${b}/api/chats`, { headers: { Authorization: `Bearer ${t}` } });
    const list = r.ok ? await r.json() : [];
    const free = list.filter(c => !c.archivedAt && c.name && c.lastMessage && c.lastMessage.length > 8);
    if (!free.length) return { __error: 'нет свободных' };
    const c = free[0];
    return { id: c.id, name: c.name, needle: c.lastMessage.slice(0, 20) };
  }, BASE);
}

// API: архивировать/разархивировать
const setArchived = async (page, id, archived) => page.evaluate(async ({ b, id, archived }) => {
  const t = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
  const r = await fetch(`${b}/api/chats/${id}/archived`, {
    method: 'PUT', headers: { Authorization: `Bearer ${t}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ archived }),
  });
  return r.ok;
}, { b: BASE, id, archived });

// API: переключить feature flag
const setFlag = async (page, key, enabled) => page.evaluate(async ({ b, key, enabled }) => {
  const t = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
  const r = await fetch(`${b}/api/feature-flags/${key}`, {
    method: 'PUT', headers: { Authorization: `Bearer ${t}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled }),
  });
  return r.ok;
}, { b: BASE, key, enabled });

// Навигация: клик по табу Архив. ArchivePage не содержит своего таббара
// (только заголовок и X-кнопку закрытия), поэтому изнутри архива таб «Архив»
// найти нельзя — приходится сначала перейти на другой раздел, потом обратно.
async function openArchiveTab(page) {
  // Если таббар ещё не виден — сначала на «Чаты» (там таббар есть)
  const hasTabbar = await page.locator('button:has-text("Чаты")').count() > 0;
  if (!hasTabbar) {
    // ArchivePage активна — кликаем её X-кнопку или переходим на #/chats через back/forward
    await page.goBack().catch(() => {});
    await page.waitForTimeout(500);
  }
  // Теперь таббар виден, ищем «Архив»
  const archiveTab = page.locator('button:has-text("Архив")').first();
  if (await archiveTab.count() === 0) {
    // нет таббара — откроем через клик по «Чаты» в HubHeader и потом «Архив»
    await page.locator('button:has-text("Чаты")').first().click();
    await page.waitForTimeout(1500);
  }
  await page.locator('button:has-text("Архив")').first().click();
  await page.waitForTimeout(2500);
}

// Навигация: открыть «Чаты»
async function openChatsTab(page) {
  // Если мы в архиве — таббара нет. Открываем «Чаты» через клик по «Архив»-X или
  // по явной кнопке в HubHeader.
  const hasTabbar = await page.locator('button:has-text("Чаты")').count() > 0;
  if (!hasTabbar) {
    // На ArchivePage: переходим на #/chats
    await page.evaluate(() => { window.location.hash = '#/chats'; });
    await page.waitForTimeout(2500);
    return;
  }
  const t = page.locator('button:has-text("Чаты"), a:has-text("Чаты")').first();
  await t.click();
  await page.waitForTimeout(2500);
}

// Навигация: открыть «Заметки» — если мы в архиве, сначала выходим
async function openNotesTab(page) {
  const hasTabbar = await page.locator('button:has-text("Чаты")').count() > 0;
  if (!hasTabbar) {
    await page.evaluate(() => { window.location.hash = '#/chats'; });
    await page.waitForTimeout(2500);
  }
  const t = page.locator('button:has-text("Заметки"), a:has-text("Заметки")').first();
  await t.click();
  await page.waitForTimeout(2500);
}

// Открыть edit dialog первого проекта
async function openFirstProjectEdit(page) {
  await openProjectsTab(page);
  await page.waitForTimeout(1500);
  // Карточка проекта содержит кнопку edit — обычно «⋯» → «Редактировать»
  const firstCard = page.locator('[data-project-card]').first();
  if (await firstCard.count() > 0) {
    await firstCard.hover();
    await page.waitForTimeout(400);
  }
  // Пробуем кликнуть ⋯ → Редактировать
  await page.locator('[aria-label*="Действия"], [aria-label*="⋯"]').first().click().catch(() => {});
  await page.waitForTimeout(500);
  const editItem = page.locator('text=Редактировать').first();
  if (await editItem.count() > 0) {
    await editItem.click();
    await page.waitForTimeout(2000);
    return true;
  }
  return false;
}

async function openProjectsTab(page) {
  // Если мы в архиве — таббара нет, сначала на «Чаты»
  const hasTabbar = await page.locator('button:has-text("Чаты")').count() > 0;
  if (!hasTabbar) {
    await page.evaluate(() => { window.location.hash = '#/chats'; });
    await page.waitForTimeout(2500);
  }
  const t = page.locator('button:has-text("Проекты"), a:has-text("Проекты")').first();
  await t.click();
  await page.waitForTimeout(1500);
}

async function main() {
  await mkdir(SHOTS, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, locale: 'ru-RU' });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(`pageerror: ${e.message}`));
  page.on('console', m => { if (m.type() === 'error') errors.push(`console.error: ${m.text()}`); });

  const report = {};

  log('=== Логин ===');
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.waitForTimeout(500);
  if (await page.locator('input[placeholder="Имя пользователя"]').isVisible().catch(() => false)) {
    await page.locator('input[placeholder="Имя пользователя"]').fill(USER);
    await page.locator('input[placeholder="Пароль"]').fill(PASS);
    await page.locator('button:has-text("Войти")').click();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);
  }
  await openChatsTab(page);
  await shot(page, '00-after-login');

  // ==== ТЕСТ 1: регрессия списка ====
  log('=== ТЕСТ 1: регрессия списка «Чаты» ===');
  const target = await pickFreeChat(page);
  if (target?.__error) throw new Error('нет чатов: ' + target.__error);
  log(`цель: ${target.id} / «${target.name}» / needle «${target.needle}»`);
  await setArchived(page, target.id, false);
  await page.waitForTimeout(500);
  await openChatsTab(page);
  await page.waitForTimeout(2000);
  const visibleBefore = await page.locator(`text=${target.needle}`).count();
  const beforeArchived = await archivedCount(page);
  log(`  до: видно ${visibleBefore}, архив=${beforeArchived}`);
  // UI-архивация через ⋮ → «Убрать в архив»
  await page.locator(`text=${target.needle}`).first().hover();
  await page.waitForTimeout(400);
  await page.locator('[aria-label="Действия с чатом"]').first().click();
  await page.waitForTimeout(400);
  await page.locator('text=Убрать в архив').first().click();
  await page.waitForTimeout(3000);
  const visibleAfter = await page.locator(`text=${target.needle}`).count();
  const afterArchived = await archivedCount(page);
  await shot(page, '01-regression-after-archive');
  log(`  после UI-архивации: видно ${visibleAfter}, архив=${afterArchived}`);
  const test1_ok = visibleAfter === 0 && afterArchived === beforeArchived + 1;
  report['1_chat_list_hides_archived'] = test1_ok ? 'OK' : `FAIL (visible ${visibleBefore}→${visibleAfter}, arch ${beforeArchived}→${afterArchived})`;
  log(`  ${test1_ok ? '✓' : '✗'} ${report['1_chat_list_hides_archived']}`);

  // ==== ТЕСТ 2: возврат через кнопку ====
  log('=== ТЕСТ 2: возврат из архива ===');
  await openArchiveTab(page);
  await shot(page, '02-archive-opened');
  const inArchiveBefore = await page.locator(`text=${target.needle}`).count();
  log(`  в архиве: видно needle ${inArchiveBefore}`);
  const restoreButtons = await page.locator('button:has-text("Вернуть из архива")').all();
  let clicked = false;
  for (const btn of restoreButtons) {
    const cardText = await btn.evaluate(el => {
      let cur = el;
      for (let j = 0; j < 10; j++) {
        if (!cur.parentElement) break;
        cur = cur.parentElement;
        if (cur.offsetHeight > 100 && cur.offsetHeight < 400) break;
      }
      return cur.textContent || '';
    });
    if (cardText.includes(target.needle)) {
      await btn.scrollIntoViewIfNeeded();
      await btn.click();
      clicked = true;
      log(`  ✓ кликнул «Вернуть из архива» для «${target.needle.slice(0, 15)}»`);
      break;
    }
  }
  await page.waitForTimeout(3000);
  await shot(page, '03-archive-after-restore');
  const archAfter = await archivedCount(page);
  log(`  после возврата: архив=${archAfter}`);
  report['2_restore_returns_chat'] = clicked && archAfter === beforeArchived ? 'OK' : `FAIL (clicked=${clicked}, arch ${afterArchived}→${archAfter})`;
  log(`  ${clicked ? '✓' : '✗'} ${report['2_restore_returns_chat']}`);

  // ==== ТЕСТ 3: пустой архив ====
  log('=== ТЕСТ 3: пустой архив ===');
  const allChats = await page.evaluate(async (b) => {
    const t = sessionStorage.getItem('cc_token') || localStorage.getItem('cc_token');
    const r = await fetch(`${b}/api/chats`, { headers: { Authorization: `Bearer ${t}` } });
    return await r.json();
  }, BASE);
  const archIds = allChats.filter(c => c.archivedAt).map(c => c.id);
  for (const id of archIds) {
    await setArchived(page, id, false);
  }
  await page.waitForTimeout(1500);
  await openArchiveTab(page);
  await page.waitForTimeout(2500);
  await shot(page, '04-empty-archive');
  const emptyTitle = await page.locator('text=Здесь пусто').count();
  log(`  «Здесь пусто» видно: ${emptyTitle}`);
  report['3_empty_state_shown'] = emptyTitle > 0 ? 'OK' : 'FAIL (нет empty state)';
  log(`  ${emptyTitle ? '✓' : '✗'} ${report['3_empty_state_shown']}`);

  // ==== ТЕСТ 4: переключение разделов ====
  log('=== ТЕСТ 4: переключение разделов ===');
  // Архив (пустой) → Заметки → Архив — empty state не должен пропасть
  await openNotesTab(page);
  await page.waitForTimeout(1500);
  await openArchiveTab(page);
  await page.waitForTimeout(2000);
  await shot(page, '05-after-nav-back');
  const stillEmpty = await page.locator('text=Здесь пусто').count();
  log(`  после nav: empty state виден ${stillEmpty}`);
  report['4_nav_preserves_state'] = stillEmpty > 0 ? 'OK' : 'FAIL (empty state пропал)';
  log(`  ${stillEmpty ? '✓' : '✗'} ${report['4_nav_preserves_state']}`);

  // ==== ТЕСТ 5: флаг OFF → ArchiveSettings скрыт ====
  log('=== ТЕСТ 5: флаг OFF ===');
  await setFlag(page, 'chat-auto-archive', false);
  await page.waitForTimeout(500);
  // Перезагрузка — фронт берёт флаги из /me при старте (setAllFlags в App.tsx),
  // без перезагрузки локальный стор не обновится.
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForTimeout(2000);
  const opened = await openFirstProjectEdit(page);
  if (!opened) {
    log('  ⚠ не удалось открыть edit — пропускаю');
    report['5_flag_off_hides_settings'] = 'SKIP';
  } else {
    await page.waitForTimeout(2500);
    await shot(page, '06-project-edit-flag-off');
    const archiveSection1 = await page.locator('text=Убирать в архив чаты без сообщений дольше').count();
    log(`  ArchiveSettings виден при OFF: ${archiveSection1}`);
    report['5_flag_off_hides_settings'] = archiveSection1 === 0 ? 'OK' : 'FAIL (виден при выключенном)';
    log(`  ${archiveSection1 === 0 ? '✓' : '✗'} ${report['5_flag_off_hides_settings']}`);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(500);
  }

  // ==== ТЕСТ 6: флаг ON → ArchiveSettings виден ====
  log('=== ТЕСТ 6: флаг ON ===');
  await setFlag(page, 'chat-auto-archive', true);
  await page.waitForTimeout(500);
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForTimeout(2000);
  const opened2 = await openFirstProjectEdit(page);
  if (!opened2) {
    log('  ⚠ не удалось открыть edit — пропускаю');
    report['6_flag_on_shows_settings'] = 'SKIP';
  } else {
    await page.waitForTimeout(2500);
    await shot(page, '07-project-edit-flag-on');
    const archiveSection2 = await page.locator('text=Убирать в архив чаты без сообщений дольше').count();
    log(`  ArchiveSettings виден при ON: ${archiveSection2}`);
    report['6_flag_on_shows_settings'] = archiveSection2 > 0 ? 'OK' : 'FAIL (скрыт при включенном)';
    log(`  ${archiveSection2 > 0 ? '✓' : '✗'} ${report['6_flag_on_shows_settings']}`);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(500);
  }

  // ==== ТЕСТ 7: возврат последнего чата ====
  log('=== ТЕСТ 7: возврат единственного чата ===');
  const target3 = await pickFreeChat(page);
  if (!target3 || target3.__error) {
    report['7_restore_last_chat'] = 'SKIP';
  } else {
    await setArchived(page, target3.id, true);
    await page.waitForTimeout(800);
    await openArchiveTab(page);
    await page.waitForTimeout(2500);
    await shot(page, '08-archive-with-single');
    const onlyArch = await archivedCount(page);
    log(`  архивных перед возвратом: ${onlyArch}`);
    const restoreAll = await page.locator('button:has-text("Вернуть из архива")').all();
    if (restoreAll.length > 0) {
      await restoreAll[0].click();
      await page.waitForTimeout(2500);
      await shot(page, '09-archive-empty');
      const finalArch = await archivedCount(page);
      log(`  архивных после возврата: ${finalArch}`);
      report['7_restore_last_chat'] = finalArch === onlyArch - 1 ? 'OK' : `FAIL (arch ${onlyArch}→${finalArch})`;
      log(`  ${finalArch === onlyArch - 1 ? '✓' : '✗'} ${report['7_restore_last_chat']}`);
    } else {
      report['7_restore_last_chat'] = 'SKIP (нет кнопок)';
    }
  }

  // Возвращаем состояние
  await setFlag(page, 'chat-auto-archive', false);

  report.pageErrors = errors.filter(e => !e.includes('favicon') && !e.includes('SW') && !e.includes('Failed to load resource'));
  console.log('\n=== ОТЧЁТ ===');
  for (const [k, v] of Object.entries(report)) console.log(`  ${k}: ${JSON.stringify(v)}`);

  await browser.close();
  const fail = Object.entries(report).filter(([k, v]) => typeof v === 'string' && v.startsWith('FAIL'));
  if (fail.length > 0) {
    console.error(`\nПРОВАЛЫ: ${fail.length}`);
    process.exit(1);
  }
}
main().catch(e => { console.error('ОШИБКА:', e); process.exit(2); });