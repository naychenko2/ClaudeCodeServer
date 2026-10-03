import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, w } from './contextRowMock';

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
