import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';
import {
  createMockApi, imageEditErrorCode, isStaleRevision, MOCK_SOURCE_SIZE, nameTakenSuggestion,
  type ImageEditorApi,
} from './imageEditor';

const P = 'p1';

// Таймеры задачи мок ставит через window.setTimeout, а окружение тестов — node
vi.stubGlobal('window', globalThis);

async function mockJob(api: ImageEditorApi): Promise<string> {
  const catalog = await api.catalog(P);
  const q = await api.quote(P, {
    provider: catalog.providers[0].key, model: 'auto', mode: 'auto', op: 'edit',
    count: 1, hasMask: false, references: 0, hasCharacter: false,
  });
  const { jobId } = await api.startJob(P, { quoteId: q.quoteId, prompt: 'кот' });
  // Для сохранения задаче достаточно существовать: гасим её таймеры
  await api.cancelJob(P, jobId);
  return jobId;
}

async function rejection(p: Promise<unknown>): Promise<unknown> {
  try {
    await p;
  } catch (e) {
    return e;
  }
  throw new Error('ожидался отказ');
}

describe('мок save/check', () => {
  it('свободное имя: расширение по формату, вписанное руками срезается', async () => {
    const api = createMockApi('fal');
    expect(await api.saveCheck(P, { folder: 'images', name: 'hero.png', format: 'webp' }))
      .toEqual({ path: 'images/hero.webp', taken: false, suggestion: null });
    expect(await api.saveCheck(P, { name: 'hero' })).toEqual({ path: 'hero.png', taken: false, suggestion: null });
  });

  it('имя на taken занято и получает ближайшее свободное', async () => {
    const api = createMockApi('fal');
    expect(await api.saveCheck(P, { folder: 'images', name: 'hero-taken', format: 'jpeg' }))
      .toEqual({ path: 'images/hero-taken.jpg', taken: true, suggestion: 'images/hero-taken.v2.jpg' });
  });
});

describe('мок save', () => {
  it('«Сохранить как» с именем на taken — 409 name_taken с suggestion', async () => {
    const api = createMockApi('fal');
    const jobId = await mockJob(api);
    const e = await rejection(api.save(P, { jobId, variant: 0, mode: 'as', folder: 'images', fileName: 'taken' }));
    expect((e as { status: number }).status).toBe(409);
    expect(imageEditErrorCode(e)).toBe('name_taken');
    expect(nameTakenSuggestion(e)).toBe('images/taken.v2.png');
  });

  it('повтор уже сохранённого имени — 409, suggestion следующий номер', async () => {
    const api = createMockApi('fal');
    const jobId = await mockJob(api);
    expect(await api.save(P, { jobId, variant: 0, mode: 'as', folder: 'images', fileName: 'hero.png' }))
      .toEqual({ path: 'images/hero.png' });
    expect(await api.saveCheck(P, { folder: 'images', name: 'hero' }))
      .toEqual({ path: 'images/hero.png', taken: true, suggestion: 'images/hero.v2.png' });
    const e = await rejection(api.save(P, { jobId, variant: 0, mode: 'as', folder: 'images', fileName: 'hero' }));
    expect(nameTakenSuggestion(e)).toBe('images/hero.v2.png');
  });

  it('источник — шаг истории, чат картинки переезжает на новый файл', async () => {
    const api = createMockApi('fal');
    const chat = await api.createChat(P, { sourcePath: 'images/hero.png' });
    const step = await api.transform(P, { base: { path: 'images/hero.png' }, ops: [{ type: 'rotate', degrees: 90 }] });
    const saved = await api.save(P, {
      stepId: step.stepId, variant: 0, mode: 'as', folder: 'images', fileName: 'hero-evening',
      encode: { format: 'webp', quality: 80 }, chatSessionId: chat.id,
    });
    expect(saved.path).toBe('images/hero-evening.webp');
    const found = await api.findChats(P, 'images/hero-evening.webp');
    expect(found.current?.imageChat).toEqual({ currentPath: 'images/hero-evening.webp', lineage: ['images/hero.png'] });
  });

  it('несуществующий источник — 404 job_not_found', async () => {
    const api = createMockApi('fal');
    expect(imageEditErrorCode(await rejection(api.save(P, { stepId: 'nope', variant: 0, mode: 'as', fileName: 'x' }))))
      .toBe('job_not_found');
  });
});

describe('мок transform', () => {
  it('dryRun не пишет шаг и отдаёт правдоподобный вес', async () => {
    const api = createMockApi('fal');
    const base = { path: 'images/hero.png' };
    const png = await api.transform(P, { base, ops: [] }, { dryRun: true });
    const jpeg = await api.transform(P, { base, ops: [], encode: { format: 'jpeg', quality: 80 } }, { dryRun: true });
    const webp = await api.transform(P, { base, ops: [], encode: { format: 'webp', quality: 80 } }, { dryRun: true });
    expect(png.stepId).toBeNull();
    expect(png).toMatchObject(MOCK_SOURCE_SIZE);
    // 1600×1200: PNG — единицы МБ, JPEG — сотни КБ, WebP ещё легче
    expect(png.bytes).toBeGreaterThan(2_000_000);
    expect(jpeg.bytes).toBeGreaterThan(100_000);
    expect(jpeg.bytes).toBeLessThan(png.bytes / 3);
    expect(webp.bytes).toBeLessThan(jpeg.bytes);
    const lowQuality = await api.transform(P, { base, ops: [], encode: { format: 'jpeg', quality: 40 } }, { dryRun: true });
    expect(lowQuality.bytes).toBeLessThan(jpeg.bytes);
  });

  it('шаги цепляются: размеры считаются от шага-базы', async () => {
    const api = createMockApi('fal');
    const crop = await api.transform(P, {
      base: { path: 'images/hero.png' },
      ops: [{ type: 'crop', rect: { x: 0.25, y: 0, width: 0.5, height: 0.5 } }],
    });
    expect(crop).toMatchObject({ width: 800, height: 600 });
    expect(crop.stepId).toBeTruthy();
    const next = await api.transform(P, {
      base: { stepId: crop.stepId! },
      ops: [{ type: 'rotate', degrees: 270 }, { type: 'resize', width: 300, lockAspect: true }],
    });
    expect(next).toMatchObject({ width: 300, height: 400 });
  });

  it('поворот не на прямой угол — 400', async () => {
    const api = createMockApi('fal');
    const e = await rejection(api.transform(P, {
      base: { path: 'a.png' }, ops: [{ type: 'rotate', degrees: 45 as 90 }],
    }));
    expect((e as { status: number }).status).toBe(400);
  });
});

describe('мок чатов картинки', () => {
  it('создание и поиск: current по пути, continued по lineage', async () => {
    const api = createMockApi('fal');
    const chat = await api.createChat(P, { sourcePath: 'images/hero.png', personaId: 'kira' });
    expect(chat).toMatchObject({ projectId: P, personaId: 'kira', name: 'hero.png · правка' });
    expect((await api.findChats(P, 'images/hero.png')).current?.id).toBe(chat.id);

    const moved = await api.setChatPath(P, chat.id, { path: 'images/hero.v2.png' });
    expect(moved.imageChat).toEqual({ currentPath: 'images/hero.v2.png', lineage: ['images/hero.png'] });
    expect(moved.updatedAt).toBe(chat.updatedAt);
    const old = await api.findChats(P, 'images/hero.png');
    expect(old.current).toBeNull();
    expect(old.continued.map(c => c.id)).toEqual([chat.id]);
    expect((await api.findChats('other', 'images/hero.v2.png')).current).toBeNull();
  });

  it('состояние: запись со старой revision — 409', async () => {
    const api = createMockApi('fal');
    const chat = await api.createChat(P, { sourcePath: 'hero.png' });
    const s0 = await api.getChatState(P, chat.id);
    expect(s0.revision).toBe(0);
    const s1 = await api.putChatState(P, chat.id, { ...s0, prompt: 'закат' });
    expect(s1).toMatchObject({ prompt: 'закат', revision: 1 });
    const e = await rejection(api.putChatState(P, chat.id, { ...s0, prompt: 'рассвет' }));
    expect(isStaleRevision(e)).toBe(true);
    expect((await api.getChatState(P, chat.id)).prompt).toBe('закат');
  });

  it('чужой или несуществующий чат — 404', async () => {
    const api = createMockApi('fal');
    const chat = await api.createChat(P, { sourcePath: 'hero.png' });
    expect(((await rejection(api.getChatState('other', chat.id))) as { status: number }).status).toBe(404);
    expect(((await rejection(api.setChatPath(P, 'nope', { path: 'x.png' }))) as { status: number }).status).toBe(404);
  });

  it('поля состояния — один в один с C# ImageChatState', async () => {
    const api = createMockApi('fal');
    const chat = await api.createChat(P, { sourcePath: 'hero.png' });
    const cs = readFileSync(fileURLToPath(new URL(
      '../../../backend/ClaudeHomeServer.Core/Services/ImageEditor/ImageEditDtos.cs', import.meta.url)), 'utf-8');
    const params = /public record ImageChatState\(([^;]*?)\);/s.exec(cs)![1];
    const csFields = [...params.matchAll(/\s(\w+)(?:,|$)/g)].map(m => m[1][0].toLowerCase() + m[1].slice(1));
    expect(Object.keys(await api.getChatState(P, chat.id)).sort()).toEqual(csFields.sort());
  });
});
