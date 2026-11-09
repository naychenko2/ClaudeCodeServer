import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
import { __resetPrefs, ensurePrefs, getPrefs, prefsApi, prefsSettled, setPrefs, type ImagePrefsChangedEvent, type ProjectPrefs } from './prefs';

// Умолчания проекта полосы «Картинки» живут на сервере, localStorage — кэш
const DEF: ProjectPrefs = { provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null };
const FAL: ProjectPrefs = { ...DEF, provider: 'fal', model: 'flux', characterSlug: 'masha' };

let emitEvent: (e: ImagePrefsChangedEvent) => void = () => {};

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}
const tick = () => new Promise(r => setTimeout(r, 0));

beforeEach(() => {
  localStorage.clear();
  __resetPrefs();
  vi.restoreAllMocks();
  vi.spyOn(prefsApi, 'subscribe').mockImplementation(h => { emitEvent = h; return () => {}; });
});

describe('prefs проекта на сервере', () => {
  it('вход в проект берёт prefs с сервера и кладёт их в кэш устройства', async () => {
    localStorage.setItem('cc-image-prefs-migrated:p1', '1');
    vi.spyOn(prefsApi, 'get').mockResolvedValue(FAL);
    await ensurePrefs('p1');
    expect(getPrefs('p1')).toEqual(FAL);
    expect(JSON.parse(localStorage.getItem('cc-image-prefs:p1')!)).toEqual(FAL);
  });

  it('правка уходит PUT полным значением, быстрые правки сливаются в следующий запрос', async () => {
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    await ensurePrefs('p1');
    const first = deferred<ProjectPrefs>();
    const put = vi.spyOn(prefsApi, 'put')
      .mockImplementationOnce(() => first.promise)
      .mockImplementation((_p, v) => Promise.resolve(v));
    setPrefs('p1', { provider: 'fal' });
    setPrefs('p1', { model: 'flux' });
    setPrefs('p1', { characterSlug: 'masha' });
    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenLastCalledWith('p1', { ...DEF, provider: 'fal' });
    first.resolve({ ...DEF, provider: 'fal' });
    await tick();
    expect(put).toHaveBeenCalledTimes(2);
    expect(put).toHaveBeenLastCalledWith('p1', FAL);
    // Ответ первого PUT не откатил быстрые правки
    expect(getPrefs('p1')).toEqual(FAL);
  });

  it('событие image_prefs_changed обновляет стор', async () => {
    localStorage.setItem('cc-image-prefs-migrated:p1', '1');
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    await ensurePrefs('p1');
    emitEvent({ type: 'image_prefs_changed', projectId: 'p1', prefs: FAL });
    expect(getPrefs('p1')).toEqual(FAL);
  });

  it('событие, пришедшее пока своя правка в пути, её не перетирает', async () => {
    localStorage.setItem('cc-image-prefs-migrated:p1', '1');
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    await ensurePrefs('p1');
    const put = deferred<ProjectPrefs>();
    vi.spyOn(prefsApi, 'put').mockImplementation(() => put.promise);
    setPrefs('p1', { provider: 'fal' });
    emitEvent({ type: 'image_prefs_changed', projectId: 'p1', prefs: DEF });
    expect(getPrefs('p1').provider).toBe('fal');
    put.resolve({ ...DEF, provider: 'fal' });
    await tick();
    expect(getPrefs('p1').provider).toBe('fal');
  });

  it('prefs устройства переносятся на сервер один раз, когда там умолчания', async () => {
    localStorage.setItem('cc-image-prefs:p1', JSON.stringify(FAL));
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    const put = vi.spyOn(prefsApi, 'put').mockImplementation((_p, v) => Promise.resolve(v));
    await ensurePrefs('p1');
    await tick();
    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenCalledWith('p1', FAL);
    expect(getPrefs('p1')).toEqual(FAL);

    // Другое устройство сбросило prefs в умолчания — второго переноса нет
    __resetPrefs();
    vi.spyOn(prefsApi, 'subscribe').mockImplementation(() => () => {});
    await ensurePrefs('p1');
    await tick();
    expect(put).toHaveBeenCalledTimes(1);
    expect(getPrefs('p1')).toEqual(DEF);
  });
});


describe('формат проекта в префах (ADR-025)', () => {
  const wish = { mode: 'aspect' as const, aspect: '16:9', tier: 'high' as const, by: 'human' as const, at: '2026-10-06T12:00:00Z' };
  const AGENT = { ...wish, by: 'agent' as const, aspect: '9:16' };

  it('чужой формат (агента) не уезжает в PUT вместе с несвязанной правкой: сервер штампует format человеком', async () => {
    localStorage.setItem('cc-image-prefs-migrated:p1', '1');
    vi.spyOn(prefsApi, 'get').mockResolvedValue({ ...DEF, format: AGENT });
    await ensurePrefs('p1');
    // Сервер, как на бою, отвечает полным сохранённым значением — формат агента в нём остался
    const put = vi.spyOn(prefsApi, 'put').mockImplementation((_p, v) => Promise.resolve({ ...v, format: AGENT }));
    setPrefs('p1', { provider: 'fal' });
    await tick();
    expect(put.mock.calls[0][1]).not.toHaveProperty('format');
    // Кэш не потерял ✦: желание агента осталось
    expect(getPrefs('p1').format).toEqual(AGENT);
  });

  it('свой формат уходит в PUT, и prefsSettled резолвится после ответа сервера', async () => {
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    await ensurePrefs('p1');
    const first = deferred<ProjectPrefs>();
    const put = vi.spyOn(prefsApi, 'put').mockImplementation(() => first.promise);
    setPrefs('p1', { format: wish });
    expect(put.mock.calls[0][1]).toMatchObject({ format: wish });
    let settled = false;
    void prefsSettled('p1').then(() => { settled = true; });
    await tick();
    expect(settled).toBe(false);
    first.resolve({ ...DEF, format: wish });
    await tick();
    expect(settled).toBe(true);
  });

  it('prefsSettled без правок в полёте резолвится сразу', async () => {
    vi.spyOn(prefsApi, 'get').mockResolvedValue(DEF);
    await ensurePrefs('p1');
    await expect(prefsSettled('p1')).resolves.toBeUndefined();
  });

  it('эхо сервера с другим временем записи — не правка: кэш не перезаписывается', async () => {
    vi.spyOn(prefsApi, 'get').mockResolvedValue({ ...DEF, format: wish });
    await ensurePrefs('p1');
    const before = getPrefs('p1');
    emitEvent({ type: 'image_prefs_changed', projectId: 'p1', prefs: { ...DEF, format: { ...wish, at: '2026-10-06T12:00:05Z' } } });
    expect(getPrefs('p1')).toBe(before);
  });
});
