import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Карточка нити звука в ленте (шаг 2.8б): версии ‹ ›, A/B без сброса позиции, выделение куска,
// мини-микшер стемов со «Свести N из M» (живой ffmpeg), файлы версии, значок лицензии, вариант
// запуска и «Взять», «Сохранить как…» с 409 name_taken, личный чат — «Скачать». Стенд — на
// ВРЕМЕННОЙ data: нить, исходник и правка без ИИ — живыми ручками; версия запуска со стемами
// (генерации на стенде нет) — фикстурой в файле нитей и рабочей папке задачи; якорь запуска в
// ленте — подменой ответа истории.
//
//   AE_DATA_DIR=/tmp/audio-card-stand/data AE_PROJECT_ROOT=/tmp/audio-card-stand/proj \
//     PLAYWRIGHT_BASE_URL=http://127.0.0.1:5098 AE_SHOTS_DIR=../.cc-attachments/audio-card \
//     npx playwright test e2e/audio-editor-card.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const DATA = process.env.AE_DATA_DIR || '';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.AE_SHOTS_DIR || '';
const FILE = 'music/intro.mp3';
const JOB = 'fxjob0001';
const PJOB = 'fxjob0002';

interface Ctx { token: string; projectId: string; sid: string; threadId: string; v1: string; psid: string }
let ctx: Ctx;

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const tone = (out: string, freq: number, sec = 8) => {
  fs.mkdirSync(path.dirname(out), { recursive: true });
  execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `sine=frequency=${freq}:duration=${sec}`,
    '-af', `volume='0.2+0.8*abs(sin(t*${freq / 200}))':eval=frame`, out]);
};

// Файл нитей чата — data/audio-threads/{owner}/{session}.json; владельца знает только сервер
function stateFile(sid: string): string {
  const root = path.join(DATA, 'audio-threads');
  for (const owner of fs.readdirSync(root)) {
    const f = path.join(root, owner, `${sid}.json`);
    if (fs.existsSync(f)) return f;
  }
  throw new Error(`нет файла нитей чата ${sid}`);
}

const ownerOf = (file: string) => path.basename(path.dirname(file));

function patchState(sid: string, fn: (s: { revision: number; threads: Record<string, unknown>[] }) => void) {
  const f = stateFile(sid);
  const s = JSON.parse(fs.readFileSync(f, 'utf8'));
  fn(s);
  s.revision += 1;
  fs.writeFileSync(f, JSON.stringify(s));
}

const ver = (id: string, number: number, jobId: string, variant: number, base: string | null, files: { role: string; path: string }[], license: string) =>
  ({ id, number, jobId, variant, baseVersionId: base, files, license, createdAt: new Date().toISOString() });

test.beforeAll(async ({ playwright, baseURL }) => {
  expect(DATA && ROOT, 'нужны AE_DATA_DIR и AE_PROJECT_ROOT стенда на временной data').toBeTruthy();
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const headers = { Authorization: `Bearer ${token}` };
  await request.put('/api/feature-flags/audio-editor', { headers, data: { enabled: true } });

  if (!fs.existsSync(path.join(ROOT, FILE))) tone(path.join(ROOT, FILE), 330);
  // Занятое имя для «Сохранить как…»
  if (!fs.existsSync(path.join(ROOT, 'music/hit.mp3'))) fs.copyFileSync(path.join(ROOT, FILE), path.join(ROOT, 'music/hit.mp3'));
  fs.rmSync(path.join(ROOT, 'music/hit.v2.mp3'), { force: true });
  fs.rmSync(path.join(ROOT, 'music/hit.v2.stems'), { recursive: true, force: true });

  const projects = (await (await request.get('/api/projects', { headers })).json()) as { id: string; rootPath: string }[];
  let projectId = projects.find(p => p.rootPath === ROOT)?.id;
  if (!projectId) {
    const r = await request.post('/api/projects', { headers, data: { name: 'Подкаст', rootPath: ROOT } });
    projectId = (await r.json()).id as string;
  }
  const sid = (await (await request.post(`/api/projects/${projectId}/sessions`, { headers, data: { name: `Звук ${Date.now()}` } })).json()).id as string;
  const base = `/api/projects/${projectId}/audio-editor/sessions/${sid}`;
  const opened = await request.post(`${base}/threads`, { headers, data: { file: FILE, mode: 'process', revision: 0 } });
  expect(opened.ok(), `нить должна открыться: ${await opened.text()}`).toBeTruthy();
  const threadId = (await opened.json()).focus as string;
  // Версия 1 — живая правка без ИИ (ffmpeg на хосте)
  const edited = await request.post(`${base}/threads/${threadId}/edit`, { headers, data: { op: 'gainFade', fadeInSec: 2, gainDb: -6 } });
  expect(edited.ok(), `правка без ИИ: ${await edited.text()}`).toBeTruthy();
  const v1 = (await edited.json()).versionId as string;

  // Запуск ИИ с двумя вариантами: у первого — стемы, ноты и текст, лицензия CC BY-NC
  const owner = ownerOf(stateFile(sid));
  const job = path.join(DATA, 'audio-editor', owner, JOB);
  tone(path.join(job, '1/main.mp3'), 330);
  tone(path.join(job, '1/stem-vocals.mp3'), 440);
  tone(path.join(job, '1/stem-drums.mp3'), 120);
  tone(path.join(job, '1/stem-bass.mp3'), 80);
  fs.writeFileSync(path.join(job, '1/score.abc'), 'X:1\nT:intro\nK:C\nCDEF|\n');
  fs.writeFileSync(path.join(job, '1/text.txt'), 'Добро пожаловать в подкаст\n');
  tone(path.join(job, '2/main.mp3'), 392, 6);
  patchState(sid, s => {
    const t = s.threads[0] as { versions: unknown[]; launches: unknown[]; currentVersionId: string };
    t.versions.push(
      ver('fxv2', 2, JOB, 1, v1, [
        { role: 'main', path: '1/main.mp3' }, { role: 'stem:vocals', path: '1/stem-vocals.mp3' },
        { role: 'stem:drums', path: '1/stem-drums.mp3' }, { role: 'stem:bass', path: '1/stem-bass.mp3' },
        { role: 'score', path: '1/score.abc' }, { role: 'text', path: '1/text.txt' },
      ], 'CC BY-NC 4.0'),
      ver('fxv3', 3, JOB, 2, v1, [{ role: 'main', path: '2/main.mp3' }], 'CC BY-NC 4.0'),
    );
    t.launches.push({ jobId: JOB, baseVersionId: v1, at: new Date().toISOString(), status: 'done', initiator: 'human', prompt: 'тёплый ламповый звук', license: 'CC BY-NC 4.0' });
    t.currentVersionId = 'fxv2';
  });

  // Личный чат: черновик и версия с субтитрами, лицензия GPL
  const psid = (await (await request.post('/api/chats', { headers, data: { mode: 'auto', name: `Звук личный ${Date.now()}` } })).json()).id as string;
  const draft = await request.post(`/api/audio-editor/chats/${psid}/threads`, { headers, data: { draftFolder: '', mode: 'voice', revision: 0 } });
  expect(draft.ok(), `черновик личного чата: ${await draft.text()}`).toBeTruthy();
  const pjob = path.join(DATA, 'audio-editor', ownerOf(stateFile(psid)), PJOB);
  tone(path.join(pjob, '1/main.mp3'), 262, 5);
  fs.writeFileSync(path.join(pjob, '1/subtitles.srt'), '1\n00:00:00,000 --> 00:00:02,000\nПривет\n');
  fs.writeFileSync(path.join(pjob, '1/lyrics.lrc'), '[00:00.00]Привет\n');
  patchState(psid, s => {
    const t = s.threads[0] as { versions: unknown[]; launches: unknown[]; currentVersionId: string };
    t.versions.push(ver('pv1', 1, PJOB, 1, null, [
      { role: 'main', path: '1/main.mp3' }, { role: 'subtitles', path: '1/subtitles.srt' }, { role: 'lyrics', path: '1/lyrics.lrc' },
    ], 'GPL-3.0'));
    t.launches.push({ jobId: PJOB, baseVersionId: null, at: new Date().toISOString(), status: 'done', initiator: 'agent', prompt: null, license: 'GPL-3.0' });
    t.currentVersionId = 'pv1';
  });

  await request.dispose();
  ctx = { token, projectId, sid, threadId, v1, psid };
});

// Якорь запуска внизу ленты: генерации на стенде нет — дописываем запись в ответ истории
async function withLaunchRecord(page: Page) {
  await page.route(`**/sessions/${ctx.sid}/history**`, async r => {
    const res = await r.fetch();
    const body = await res.json();
    const rec = {
      kind: 'module_record', module: 'audioeditor', recordType: 'audio_launch_versions',
      fallback: 'Вы запустили: «тёплый ламповый звук» · ACE-Step 1.5', timestamp: Date.now(),
      data: {
        threadId: ctx.threadId, jobId: JOB, mode: 'music', op: 'cover', provider: 'local', model: 'ACE-Step 1.5', count: 2,
        price: { amount: null, unit: 'free', approx: false, source: 'catalog', eta: null, queueLength: null },
        license: 'CC BY-NC 4.0', initiator: 'human', baseVersionId: ctx.v1,
      },
    };
    if (Array.isArray(body)) body.push(rec);
    else if (Array.isArray(body?.messages)) body.messages.push(rec);
    await r.fulfill({ response: res, json: body });
  });
}

async function open(page: Page, url: string, width: number, theme: 'light' | 'dark') {
  await page.setViewportSize({ width, height: width < 500 ? 780 : 900 });
  await page.addInitScript(([tk, th]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('theme-mode', th as string);
  }, [ctx.token, theme]);
  await page.goto(url);
}

// Системный тост «Доступна новая версия claude CLI» перекрывает карточку на узком экране
async function closeToasts(page: Page) {
  const close = page.locator('[data-cc-src*="NotificationToasts"] [title="Закрыть"]');
  for (let i = 0; i < 5 && await close.count(); i++) await close.first().click().catch(() => {});
}

async function dragOn(page: Page, el: ReturnType<Page['locator']>, from: number, to: number) {
  await el.scrollIntoViewIfNeeded();
  const box = (await el.boundingBox())!;
  await page.mouse.move(box.x + box.width * from, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * to, box.y + box.height / 2, { steps: 6 });
  await page.mouse.up();
}

const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: false });
};

test('карточка нити: версии ‹ ›, A/B, выделение, сведение, вариант «Взять», «Сохранить как…» с 409', async ({ page }) => {
  await withLaunchRecord(page);
  const peaks = page.waitForRequest(/\/versions\/[^/]+\/peaks\?/);
  await open(page, `/#/project/${ctx.projectId}/chat/${ctx.sid}`, 1280, 'light');

  const card = page.locator('[data-audio-card]').first();
  await expect(card).toBeVisible({ timeout: 20_000 });
  await closeToasts(page);
  // Волна — с сервера, а не декодом в браузере
  await peaks;
  await expect(card.locator('[data-audio-nav]')).toHaveText('версия 2 из 3');
  await expect(card.locator('[data-audio-license="CC BY-NC"]')).toBeVisible();
  await expect(card.getByText('в работе')).toBeVisible();
  await expect(card.locator('[data-stem-row]')).toHaveCount(3);
  await expect(card.locator('[data-audio-file]')).toHaveCount(2);
  await expect(card.getByText('Сохранятся папкой')).toBeVisible();
  await shot(page, 'card-desktop-light');

  // Выделение куска протяжкой по волне — у нити в работе
  await dragOn(page, card.getByRole('slider', { name: /^Волна/ }).first(), 0.2, 0.6);
  await expect(card.getByText(/^Выделено /)).toBeVisible();
  await expect(card.getByRole('button', { name: 'Перегенерировать кусок' })).toBeVisible();

  // ‹ — версия 1 (правка без ИИ): A/B с исходником, переключение не сбивает позицию
  await card.getByRole('button', { name: 'Предыдущая версия' }).click();
  await expect(card.locator('[data-audio-nav]')).toHaveText('версия 1 из 3');
  await expect(card.getByText(/^Без ИИ · /)).toBeVisible();
  await expect(card.getByRole('button', { name: 'A · исх.' })).toBeVisible();
  const wave1 = card.getByRole('slider', { name: /^Волна/ }).first();
  await wave1.scrollIntoViewIfNeeded();
  const b1 = (await wave1.boundingBox())!;
  await page.mouse.click(b1.x + b1.width * 0.5, b1.y + b1.height / 2);
  const time = card.locator('[data-player-time]').first();
  await expect(time).toHaveText(/^0:04\.0 \//);
  await card.getByRole('button', { name: 'A · исх.' }).click();
  await expect.poll(() => wave1.getAttribute('aria-valuenow')).toBe('4');
  await expect(time).toHaveText(/^0:04\.0 \//);
  await expect(wave1).toHaveAttribute('aria-valuetext', /0:04\.0 из 0:08\.0/);

  // › — обратно к версии 2: заглушить барабаны и свести 2 из 3 живым ffmpeg
  await card.getByRole('button', { name: 'Следующая версия' }).click();
  await card.getByRole('button', { name: 'Заглушить drums' }).click();
  const mix = card.getByRole('button', { name: 'Свести 2 из 3 в новую версию' });
  await expect(mix).toBeEnabled();
  await mix.click();
  await expect(card.locator('[data-audio-nav]')).toHaveText('версия 4 из 4', { timeout: 30_000 });
  await expect(card.getByText(/Свёл стемы \(vocals, bass\)/)).toBeVisible();

  // Запуск: два варианта, «Взять» второй
  const launch = page.locator('[data-audio-launch="done"]');
  await expect(launch).toBeVisible();
  await expect(launch.getByText('бесплатно', { exact: false })).toBeVisible();
  await expect(launch.locator('[data-audio-variant]')).toHaveCount(2);
  await launch.locator('[data-audio-variant="2"]').getByRole('button', { name: 'Взять' }).click();
  await expect(launch.locator('[data-audio-variant="2"]')).toHaveAttribute('data-current', 'true');
  await expect(card.locator('[data-audio-nav]')).toHaveText('версия 3 из 4');

  // «Сохранить как…» на занятое имя: 409 name_taken, подсказка, «Взять», запись
  await card.getByRole('button', { name: 'Сохранить как…' }).click();
  const dialog = page.locator('[data-audio-save-as]');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('textbox').last().fill('hit');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  const taken = page.locator('[data-audio-save-taken]');
  await expect(taken).toContainText('music/hit.v2.mp3');
  await shot(page, 'save-as-taken');
  await taken.getByRole('button', { name: 'Взять «hit.v2.mp3»' }).click();
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(fs.existsSync(path.join(ROOT, 'music/hit.v2.mp3'))).toBeTruthy();
});

test('360 px, тёмная тема: карточка без горизонтального скролла, M/S — тач-цель 32 px', async ({ page }) => {
  await withLaunchRecord(page);
  await open(page, `/#/project/${ctx.projectId}/chat/${ctx.sid}`, 360, 'dark');
  const card = page.locator('[data-audio-card]').first();
  await expect(card).toBeVisible({ timeout: 20_000 });
  await closeToasts(page);
  // Карточка с микшером — версия 2 (стемы): листаем назад от текущей
  for (let i = 0; i < 4 && (await card.locator('[data-audio-nav]').textContent()) !== 'версия 2 из 4'; i++) {
    await card.getByRole('button', { name: 'Предыдущая версия' }).click();
  }
  await expect(card.locator('[data-audio-nav]')).toHaveText('версия 2 из 4');
  await expect(card.locator('[data-stem-row]')).toHaveCount(3);
  const m = (await card.getByRole('button', { name: 'Заглушить vocals' }).boundingBox())!;
  expect(m.width).toBeGreaterThanOrEqual(32);
  expect(m.height).toBeGreaterThanOrEqual(32);
  const cardBox = (await card.boundingBox())!;
  expect(cardBox.x + cardBox.width).toBeLessThanOrEqual(360);
  await card.scrollIntoViewIfNeeded();
  await shot(page, 'card-360-dark');
  await page.locator('[data-audio-launch]').scrollIntoViewIfNeeded();
  await shot(page, 'launch-360-dark');
});

for (const theme of ['light', 'dark'] as const) {
  test(`личный чат, 360 px, ${theme}: «Скачать» вместо сохранения, субтитры и слова списком`, async ({ page }) => {
    await open(page, `/#/chats/${ctx.psid}`, 360, theme);
    const card = page.locator('[data-audio-card]').first();
    await expect(card).toBeVisible({ timeout: 20_000 });
    await closeToasts(page);
    await expect(card.getByRole('button', { name: 'Скачать', exact: true })).toBeVisible();
    await expect(card.getByRole('button', { name: 'Сохранить в проект' })).toHaveCount(0);
    await expect(card.getByRole('button', { name: 'Сохранить как…' })).toHaveCount(0);
    await expect(card.locator('[data-audio-license="GPL-3.0"]')).toBeVisible();
    await expect(card.locator('[data-audio-file="subtitles"]')).toContainText('.srt');
    await expect(card.locator('[data-audio-file="lyrics"]')).toContainText('.lrc');
    await expect(card.getByText('Claude', { exact: true })).toBeVisible();
    const download = page.waitForEvent('download');
    await card.getByRole('button', { name: 'Скачать', exact: true }).click();
    expect((await download).suggestedFilename()).toMatch(/\.mp3$/);
    await shot(page, `personal-360-${theme}`);
  });
}
