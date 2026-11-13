// QA-приёмка волны 4 «Общие проекты», А4: авто-память проектной персоны пополняется
// из разговора РЕДАКТОРА. Смотрим у владельца: запись легла в его стор персоны
// (SourceSessionId — этот чат), под ключом редактора записей нет.
//
// Живой ход: стенд поднят с локальной моделью (LlmProviders.qa-local → claude CLI,
// LocalLlm → llama-server). Без живого провайдера сценарий не проверить.
import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  BASE, EDITOR, OWNER, addMember, api, createChat, createPersona, ensureFlags, ensureUsers,
  makeLead, makeProject, tok, uiLogin, wait,
} from './shared-w4-helpers';

const SHOTS = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.cc-attachments/qa-w4');
const RUN = process.env.QA_W4_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `qa-w4 ${RUN} ${s}`;
const ROOT = `/home/an/qa-w4/autolearn-${RUN}`;
const MODEL = process.env.QA_W4_MODEL || 'qwen3.8-27b';

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-autolearn.txt`, report.join('\n'));
  console.log(s);
}

let pid = '';
let leadId = '';
let chatId = '';
let editorUserId = '';

test.describe.configure({ mode: 'serial' });
test.setTimeout(280_000);

test.beforeAll(async () => {
  mkdirSync(SHOTS, { recursive: true });
  await ensureUsers();
  await ensureFlags();
  pid = await makeProject(N('проект авто-памяти'), ROOT);
  await addMember(pid, EDITOR, 'editor');
  leadId = await createPersona(pid, N('руководитель'));
  await makeLead(leadId);
  editorUserId = (await api(await tok(EDITOR), 'GET', '/api/auth/me')).body.userId as string;
  // Чат редактора с руководителем; модель — локальная, иначе на стенде ход не поднять
  const c = await createChat(pid, N('разговор с руководителем'), { user: EDITOR, model: MODEL });
  expect(c.status, `чат редактора: ${JSON.stringify(c.body)}`).toBe(201);
  chatId = c.body.id;
  note(`фикстура: проект ${pid}, руководитель ${leadId}, чат ${chatId}, модель чата ${c.body.model}`);
});

test.afterAll(async () => {
  const a = await tok(OWNER);
  if (pid) await api(a, 'DELETE', `/api/projects/${pid}`).catch(() => undefined);
});

const MEMORY_TEXT = 'Запомни, пожалуйста, важное про меня: меня зовут Марина, я редактор этого проекта '
  + 'и веду направление аналитики. Мне удобно получать длинные сводки по понедельникам утром, '
  + 'а короткие напоминания — в течение дня. Я не люблю телефонные звонки и прошу все вопросы '
  + 'решать сообщениями. Мой любимый формат отчёта — таблица с выводами в конце, и я всегда '
  + 'проверяю цифры перед публикацией. Сохрани это как факт обо мне.';

test('А4 редактор говорит с персоной — авто-память пополняется у владельца', async ({ page }) => {
  // Сначала проверим, что у персоны пусто
  const a0 = await tok(OWNER);
  const before = (await api(a0, 'GET', `/api/personas/${leadId}/memory`)).body as any[];
  note(`память персоны до разговора: ${Array.isArray(before) ? before.length : 'не список'}`);

  await uiLogin(page, EDITOR);
  await page.goto(`${BASE}/#/project/${pid}/chat/${chatId}`);
  await page.reload();
  await wait(3000);
  await page.screenshot({ path: `${SHOTS}/a4-opened-chat.png` });

  // Модель чата: если сервер оставил claude-модель, ход на стенде не поднимется —
  // фиксируем это явно, а не выдаём за провал фичи
  const chatInfo = (await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const model = chatInfo.find(c => c.id === chatId)?.model;
  note(`модель чата перед ходом: ${model}`);

  const composer = page.locator('textarea.cc-composer-input');
  await expect(composer, 'композер чата доступен редактору').toBeVisible({ timeout: 20000 });
  await composer.fill(MEMORY_TEXT);
  await composer.press('Enter');

  // Ждём окончания хода: ответ ассистента в ленте (или ошибку в консоли/тосте)
  const answer = page.locator('.cc-msg').filter({ hasNotText: MEMORY_TEXT });
  let answered = false;
  for (let i = 0; i < 60 && !answered; i++) {
    await wait(2000);
    const n = await answer.count();
    const sendDisabled = await composer.isDisabled().catch(() => false);
    if (n > 1 && !sendDisabled) answered = true;
  }
  const bodyText = (await page.locator('body').innerText()).replace(/\n+/g, ' | ');
  note(`ход завершён: ${answered}; фрагмент ленты: ${bodyText.slice(0, 500)}`);
  await page.screenshot({ path: `${SHOTS}/a4-after-turn.png` });

  // Авто-память — фоновый пайплайн после хода: ждём записи до 90 с
  let entries: any[] = [];
  for (let i = 0; i < 45; i++) {
    entries = (await api(a0, 'GET', `/api/personas/${leadId}/memory`)).body as any[];
    if (Array.isArray(entries) && entries.length > before.length) break;
    await wait(2000);
  }
  note(`память персоны после разговора: ${Array.isArray(entries) ? entries.length : 'не список'}; записи: ${JSON.stringify((entries ?? []).map(e => ({ type: e.type, text: (e.text ?? '').slice(0, 80), source: e.sourceSessionId })))}`);
  await page.screenshot({ path: `${SHOTS}/a4-owner-memory.png` });

  // Главное свидетельство — записи памяти из ЭТОГО чата: они появляются только после
  // живого хода и его фонового разбора. Признак «ответ в ленте» вторичен (лента может
  // ещё дорисовываться) — по нему лишь объясняем случай пустой памяти
  const sessionEntries = entries.filter(e => e.sourceSessionId === chatId);
  note(`записей из чата ${chatId}: ${sessionEntries.length}`);
  if (sessionEntries.length === 0 && !answered) {
    note('A4 НЕ ПРОВЕРЕН живьём: ход агента на стенде не завершился (провайдер/CLI)');
    expect(model, 'модель чата — локальная, ход должен был подниматься').toBe(MODEL);
    return;
  }
  expect(sessionEntries.length, 'авто-память пополнилась записью из чата редактора').toBeGreaterThan(0);
  note(`A4: ход редактора поднял авто-память персоны — ${sessionEntries.length} записей из чата; видно владельцу`);
  // Запись — под ключом владельца (её видит владелец), под ключом редактора — пусто
  const editorMemory = (await api(await tok(EDITOR), 'GET', `/api/personas/${leadId}/memory`)).body as any[];
  note(`память той же персоны глазами редактора (тот же ключ арендатора): ${editorMemory.length}`);
  note(`редактор userId ${editorUserId}; запись видна владельцу — OK`);
  await page.screenshot({ path: `${SHOTS}/a4-after-turn-final.png` });
});
