import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, voiceRef, w, P } from './contextRowMock';

// 2з-3 (в конце файла): наполнение контекста из ленты и «Голосов» — сценарий 5 макета composer-actions-v1 и
// сценарий 4 макета строки (серый голос при «Стемах») на 1440, 1024 и 360.
// Редактор звука из панели «Контекст» (ADR-023, шаг 2з-2): «Редактор» → волна → выделение куска → «Применить»
// (правка без ИИ даёт новую версию) → «Готово» → чип «Перегенерировать кусок» из серого стал живым. Бэкенд не нужен:
// контекст чата, хабы и API звука — моки (contextRowMock), манифест модуля регистрируется со страницы. Запуск:
//   cd frontend; npx vite --port 5326 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5326 CA_SHOTS_DIR=../.cc-attachments/audio-editor npx playwright test e2e/context-actions-audio.spec.ts

const SHOTS = process.env.CA_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });
const D = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };
const SONG = 'thread-song';

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

const songThread = () => ({
  id: SONG, file: 'music/intro.mp3', lineage: [], draftFolder: null, createdAt: '2026-10-03T09:00:00Z', currentVersionId: 'origin', launches: [],
  settings: { mode: 'music', operation: null, provider: null, model: null, fields: null },
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, license: null, createdAt: '2026-10-03T09:00:00Z', files: [{ role: 'main', path: 'music/intro.mp3' }] }],
});

async function registerAudio(page: Page) {
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/audioEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-audio', core: true, tab: undefined });
  });
}

const actions = (page: Page) => page.locator('[data-composer-actions]');
const repaint = (page: Page) => actions(page).locator('[data-action-chip="repaint"]');
const editor = (page: Page) => page.locator('[data-audio-editor-wave]');

async function drag(page: Page, from: number, to: number) {
  const box = await page.locator('[data-audio-editor-wave] [role="slider"]').boundingBox();
  if (!box) throw new Error('нет волны');
  const y = box.y + box.height / 2;
  await page.mouse.move(box.x + box.width * from, y);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * ((from + to) / 2), y, { steps: 4 });
  await page.mouse.move(box.x + box.width * to, y, { steps: 4 });
  await page.mouse.up();
}

for (const { name, vp } of [{ name: '1440', vp: D }, { name: '360', vp: M }] as const) {
  test(`редактор звука · ${name}: кусок на волне → «Применить» даёт версию → «Готово» → «Перегенерировать кусок» живой`, async ({ page }) => {
    newWorld({
      flags: { 'audio-editor': true }, audio: [songThread()],
      ctx: { primary: primary({ kind: 'audio', ref: { threadId: SONG, versionId: 'origin' }, label: 'intro.mp3', version: 'origin' }) },
    });
    await openChat(page, { vp });
    await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
    await registerAudio(page);
    await expect(actions(page)).toBeVisible({ timeout: 15_000 });
    // Песня: «Перегенерировать кусок» первым, но серый — куска нет
    await expect(repaint(page)).toHaveAttribute('aria-disabled', 'true');

    await page.locator('[data-context-row] [data-chip="primary"]').click();
    await expect(page.locator('[data-context-panel]')).toBeVisible({ timeout: 10_000 });
    await page.locator('[data-context-panel]').getByRole('button', { name: 'Редактор' }).click();
    await expect(editor(page)).toBeVisible({ timeout: 10_000 });
    // Телефон: окно во весь экран, «Готово» на виду
    const done = page.getByRole('button', { name: 'Готово' });
    await expect(done).toBeVisible();
    const title = page.getByText(/^Редактор · /).first();
    const tb = await title.boundingBox();
    expect(tb!.y).toBeLessThan(vp.height * 0.15);
    const wb = await editor(page).boundingBox();
    if (vp.width < 768) expect(wb!.width).toBeGreaterThanOrEqual(vp.width - 60);
    const db = await done.boundingBox();
    expect(db!.y + db!.height).toBeLessThanOrEqual(vp.height);
    expect(db!.x + db!.width).toBeLessThanOrEqual(vp.width);

    // Выделение: протяжка по волне; поле «Кусок» показывает те же цифры, «Применить» оживает
    const apply = page.locator('[data-editor-apply] button');
    await expect(apply).toBeDisabled();
    await drag(page, 0.2, 0.5);
    await expect(page.locator('[data-wave-selection]')).toBeVisible();
    await expect(apply).toBeEnabled();
    await shot(page, `editor-${name}.png`);
    await apply.click();
    await expect.poll(() => w().audioEdits.length, { timeout: 10_000 }).toBe(1);
    expect(w().audioEdits[0]).toMatchObject({ op: 'trim' });
    const e0 = w().audioEdits[0] as { startSec: number; endSec: number };
    expect(e0.endSec).toBeGreaterThan(e0.startSec);
    // Новая версия: кусок снят, «Применить» снова серая
    await expect(page.locator('[data-wave-selection]')).toHaveCount(0);
    await expect(apply).toBeDisabled();

    // Кусок на новой версии → «Готово» → чип живой
    await drag(page, 0.3, 0.6);
    await expect(page.locator('[data-wave-selection]')).toBeVisible();
    await done.click();
    await expect(editor(page)).toHaveCount(0);
    await expect(repaint(page)).not.toHaveAttribute('aria-disabled', 'true');
    await shot(page, `repaint-live-${name}.png`);
  });
}

// ── 2з-3: наполнение контекста (макет composer-actions-v1, сценарий 5; макет строки, сценарий 4) ──

const VIEWPORTS = [
  { name: '1440', vp: D },
  { name: '1024', vp: { width: 1024, height: 800 } },
  { name: '360', vp: M },
] as const;

const anchor = {
  kind: 'module_record', module: 'audioeditor', recordType: 'audio_thread', data: { threadId: SONG, versionId: 'origin' }, fallback: '',
  timestamp: Date.parse('2026-10-03T10:00:00Z') - 30_000,
};
const card = (page: Page) => page.locator('[data-audio-card="origin"]');
const runBtn = (page: Page) => page.locator('[data-composer-mode-bar] button').last();
const ids = (page: Page) => actions(page).locator('[data-action-chip]').evaluateAll(els => els.map(e => e.getAttribute('data-action-chip')));

async function openFeed(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', o: { voice?: boolean; flags?: Record<string, boolean> } = {}) {
  newWorld({
    feed: [anchor], audio: [songThread()], flags: { 'audio-editor': true, ...o.flags },
    ctx: { refs: o.voice ? [voiceRef()] : [] },
  });
  await openChat(page, { vp, theme });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await registerAudio(page);
}

// Серый референс в строке: у пилюли в имени «не используется в операции …»; если пилюля ушла в «+N» — подсказка
// строки в меню списка
async function rowVoiceIsGray(page: Page): Promise<boolean> {
  const row = page.locator('[data-context-row]');
  // Подсказка серой пилюли — «Не используется в операции …» (без имени), живой — «Марина · голос»
  const pill = row.locator('[title]').filter({ hasText: /^Марина$/ });
  // Пилюля в строке есть (на телефоне строка прокручивается — она может быть за краем): серость — в её подсказке
  if (await pill.count()) return /не используется в операции/i.test(await pill.first().getAttribute('title') ?? '');
  await row.getByText(/^\+\d/).first().click();
  const gray = await page.getByText(/не используется в операции/i).count() > 0;
  // Меню закрывает щелчок по подложке: Escape его не снимает
  await page.mouse.click(2, 2);
  await page.waitForTimeout(200);
  return gray;
}

for (const { name, vp } of VIEWPORTS) {
  for (const theme of ['light', 'dark'] as const) {
    test.describe(`наполнение контекста звука · ${name} · ${theme}`, () => {
      test('5 · песня: «Работать с этой» → «Стемы», голос серый → набор «4 стема» → запуск → «Свести» без ИИ', async ({ page }) => {
        await openFeed(page, vp, theme, { voice: true });
        await expect(card(page)).toBeVisible({ timeout: 15_000 });
        await expect(actions(page)).toHaveCount(0);
        await card(page).getByRole('button', { name: 'Работать с этой' }).click();
        await expect(actions(page)).toBeVisible();
        expect(await ids(page)).toEqual(['__chat', 'repaint', 'stems', 'denoise', 'concat']);
        // «Стемы» выбраны, «Перегенерировать кусок» серый без выделения, «Склеить» серая без кусков
        await expect(actions(page).locator('[data-action-chip="stems"]')).toHaveAttribute('aria-checked', 'true');
        await expect(repaint(page)).toHaveAttribute('aria-disabled', 'true');
        await expect(card(page)).toContainText('В работе');
        // Телефон: «Работать с этой» панель не поднимает; десктоп — открывает «Контекст»
        if (name === '360') await expect(page.locator('[data-context-panel]')).toHaveCount(0);
        // Вопрос «Набор» под чипами, голос Марины серый («Стемы» голоса не берут)
        await expect(actions(page).getByText('Набор')).toBeVisible();
        expect(await rowVoiceIsGray(page)).toBe(true);
        await expect(runBtn(page)).toContainText('✦ Стемы');
        await expect(runBtn(page)).toBeEnabled();
        // «Набор: 4» (4 дорожки) — «Чем» сам стал HTDemucs
        await actions(page).getByRole('button', { name: 'Набор: 4 стема' }).click();
        // На телефоне чип без подписи — модель в его подсказке
        await expect(page.locator('[data-context-row] [data-chip="exec"]')).toHaveAttribute('title', /HTDemucs/, { timeout: 10_000 });
        await shot(page, `s5-stems-${name}-${theme}.png`);
        await runBtn(page).click();
        await expect.poll(() => w().audioJobs.length, { timeout: 10_000 }).toBe(1);
        expect(w().audioQuotes.at(-1)).toMatchObject({ operation: 'separate' });
        // Версия со стемами: чипы «Чат · Свести · Склеить»
        await expect.poll(() => ids(page), { timeout: 10_000 }).toEqual(['__chat', 'mix', 'concat']);
        await actions(page).locator('[data-action-chip="mix"]').click();
        await expect(page.locator('[data-context-row] [data-chip="exec"]')).toHaveAttribute('title', /Без ИИ/, { timeout: 10_000 });
        await shot(page, `s5-mix-${name}-${theme}.png`);
      });

      test('4 (строка) · голос из «Голосов»: серый при «Стемы», живой в «Чате»', async ({ page }) => {
        await openFeed(page, vp, theme, { voice: true });
        await expect(card(page)).toBeVisible({ timeout: 15_000 });
        await card(page).getByRole('button', { name: 'Работать с этой' }).click();
        await expect(actions(page).locator('[data-action-chip="stems"]')).toHaveAttribute('aria-checked', 'true');
        expect(await rowVoiceIsGray(page)).toBe(true);
        // В «Чате» серых нет: Claude видит всё подключённое
        await actions(page).locator('[data-action-chip="__chat"]').click();
        expect(await rowVoiceIsGray(page)).toBe(false);
        await shot(page, `s4-gray-${name}-${theme}.png`);
      });
    });
  }
}

// Библиотека «Голоса» отдельной панелью зоны: только десктоп (на телефоне панелей зоны нет)
for (const { name, vp } of VIEWPORTS.filter(v => v.name !== '360')) {
  test(`«Голоса» · ${name}: «В контекст» → «В контексте ✓», «Обучить голос» запускает обучение`, async ({ page }) => {
    newWorld({
      feed: [anchor], audio: [songThread()], flags: { 'audio-editor': true },
      ctx: { primary: primary({ kind: 'audio', ref: { threadId: SONG }, label: 'intro.mp3', version: 'origin' }) },
    });
    await openChat(page, { vp });
    await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
    await registerAudio(page);
    await expect(actions(page)).toBeVisible({ timeout: 15_000 });
    // «Добавить из… → из «Голосов»» открывает отдельную панель
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const panel = page.locator('[data-context-panel]');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await panel.getByRole('button', { name: /Добавить из/ }).click();
    await page.getByText('Из «Голосов»').click();
    const voice = page.locator('[data-voice="marina"]');
    await expect(voice).toBeVisible({ timeout: 10_000 });
    await voice.getByRole('button').first().click();
    await voice.locator('[data-context-add="add"] button').click();
    await expect(voice.locator('[data-context-add="in"]')).toBeVisible();
    expect(w().mutations.some(m => m.method === 'POST' && m.path === '/refs'
      && (m.body as { kind?: string; role?: string }).kind === 'audio-voice' && (m.body as { role?: string }).role === 'voice')).toBe(true);
    // Повторный клик снимает референс
    await voice.locator('[data-context-add="in"] button').click();
    await expect(voice.locator('[data-context-add="add"]')).toBeVisible();
    await shot(page, `voices-${name}.png`);

    // «Обучить голос»: имя и запись → котировка trainVoice → запуск с clipPaths
    await page.getByRole('button', { name: 'Обучить голос' }).click();
    await expect(page.locator('[data-train-voice]')).toBeVisible();
    await page.locator('[data-train-voice]').getByPlaceholder('Например, Андрей').fill('Андрей');
    await page.locator('[data-train-voice] [data-field="clips"]').getByRole('button', { name: 'Запись' }).click();
    await page.locator('[data-train-voice] [data-field="clips"] input').fill('records/andrey-1.wav');
    await page.locator('[data-train-voice]').getByRole('button', { name: /Обучить · бесплатно/ }).click();
    await expect.poll(() => w().audioJobs.length, { timeout: 10_000 }).toBe(1);
    expect(w().audioQuotes.find(q => q.operation === 'trainVoice')).toMatchObject({ mode: 'voice', operation: 'trainVoice', prompt: 'Андрей' });
    expect(w().audioJobs[0]).toMatchObject({ prompt: 'Андрей', clipPaths: 'records/andrey-1.wav' });
    await expect(page.locator('[data-train-voice]')).toHaveCount(0);
  });
}
