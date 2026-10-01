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
// Тосты и просьба открыть панель уходят событием окна — в node хватит заглушки
const dispatched: Event[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = { dispatchEvent: (e: Event) => { dispatched.push(e); return true; } };
import { audioApi, type AudioThread, type AudioThreadVersion, type AudioThreadsState } from '../api';
import { EMPTY_MIXER, mixPlan, toggleMute, setGain } from '../player/mix';
import { lruGet, lruSet, normalizeJoint } from '../player/peaks';
import { dropNode } from '../player/StemMixer';
import { mixStems, saveVersion, takeVersion } from './actions';
import {
  abSides, currentIndex, doneText, extraFiles, launchEndNote, licenseBadge, navText, orderedVersions, priceText, saveKind,
  splitSuggestion, versionStems,
} from './model';
import { __peaksCacheSize, __resetPeaksCache, CACHE_MAX, loadPeaks } from './serverPeaks';
import {
  __applyThreads, __resetAudioStore, getOperationRequest, getPieceFieldOpen, getSelection, getThreadsState, requestOperation,
  setPieceFieldOpen, setSelection,
} from './threadStore';

const ver = (id: string, number: number, extra: Partial<AudioThreadVersion> = {}): AudioThreadVersion => ({
  id, number, jobId: id === 'origin' ? null : `job-${id}`, variant: 1, baseVersionId: null,
  files: [{ role: 'main', path: `${id}.mp3` }], license: null, createdAt: '', ...extra,
});
const thread = (versions: AudioThreadVersion[], extra: Partial<AudioThread> = {}): AudioThread => ({
  id: 't1', file: 'music/intro.mp3', lineage: [], draftFolder: null, createdAt: '', versions,
  currentVersionId: versions[versions.length - 1]?.id ?? null, launches: [], settings: null, ...extra,
});
const st = (revision: number, threads: AudioThread[]): AudioThreadsState => ({ focus: 't1', revision, threads });

beforeEach(() => {
  dispatched.length = 0;
  localStorage.clear();
  __resetAudioStore();
  __resetPeaksCache();
  vi.restoreAllMocks();
});

describe('карточка: версии ‹ ›', () => {
  it('исходник первым, дальше по номеру; текущая — индекс нити, без текущей — последняя', () => {
    const t = thread([ver('v2', 2), ver('origin', 0), ver('v1', 1)], { currentVersionId: 'v1' });
    const list = orderedVersions(t);
    expect(list.map(v => v.id)).toEqual(['origin', 'v1', 'v2']);
    expect(currentIndex(list, 'v1')).toBe(1);
    expect(currentIndex(list, null)).toBe(2);
  });

  it('подпись листания — «версия N из последней», у исходника — «исходник»', () => {
    const list = orderedVersions(thread([ver('origin', 0), ver('v1', 1), ver('v3', 3)]));
    expect(navText(list[1], list)).toBe('версия 1 из 3');
    expect(navText(list[0], list)).toBe('исходник');
  });
});

describe('карточка: A/B', () => {
  it('A — основа версии, B — сама версия', () => {
    const t = thread([ver('origin', 0), ver('v1', 1), ver('v2', 2, { baseVersionId: 'origin' })]);
    expect(abSides(t, t.versions[2])).toEqual([
      { versionId: 'origin', label: 'A · исх.' },
      { versionId: 'v2', label: 'B · в2' },
    ]);
  });

  it('без основы — предыдущая по номеру; основа без звука (одни стемы) — сравнивать не с чем', () => {
    const stemsOnly = ver('v1', 1, { files: [{ role: 'stem:vocals', path: 'v.mp3' }] });
    const t = thread([ver('origin', 0), stemsOnly, ver('v2', 2)]);
    expect(abSides(t, t.versions[2])).toEqual([{ versionId: 'v2', label: 'в2' }]);
    const t2 = thread([ver('origin', 0), ver('v1', 1)]);
    expect(abSides(t2, t2.versions[1]).map(s => s.versionId)).toEqual(['origin', 'v1']);
  });

  it('версия без главного файла плеера не получает', () => {
    const t = thread([ver('v1', 1, { files: [{ role: 'stem:drums', path: 'd.mp3' }] })]);
    expect(abSides(t, t.versions[0])).toEqual([]);
  });
});

describe('карточка: файлы версии', () => {
  it('стемы — отдельно, остальное списком: текст выше нот, у каждого расширение', () => {
    const v = ver('v1', 1, {
      files: [
        { role: 'main', path: 'w/anthem.mp3' }, { role: 'score', path: 'w/anthem.abc' }, { role: 'text', path: 'w/a.txt' },
        { role: 'subtitles', path: 'w/a.srt' }, { role: 'stem:vocals', path: 'w/vocals.mp3' }, { role: 'model', path: 'w/voice.pth' },
        { role: 'index', path: 'w/voice.index' },
      ],
    });
    expect(versionStems(v)).toEqual([{ id: 'stem:vocals', name: 'vocals', path: 'w/vocals.mp3' }]);
    expect(extraFiles(v).map(f => `${f.role}:${f.ext}`)).toEqual(['text:txt', 'subtitles:srt', 'score:abc', 'model:pth', 'index:index']);
  });
});

describe('карточка: лицензия и цена', () => {
  it('значок по лицензии версии; у исходника и правки без ИИ — без значка', () => {
    expect(licenseBadge(ver('v1', 1, { license: 'CC BY-NC 4.0' }))).toMatchObject({ label: 'CC BY-NC', hint: 'Только некоммерческое использование' });
    expect(licenseBadge(ver('v1', 1, { license: 'GPL-3.0' }))?.label).toBe('GPL-3.0');
    expect(licenseBadge(ver('v1', 1, { license: 'watermark' }))?.label).toBe('водяной знак');
    expect(licenseBadge(ver('v1', 1, { license: 'MIT / Apache-2.0' }))?.label).toBe('коммерчески чистая');
    expect(licenseBadge(ver('v1', 1, { license: 'не указана' }))?.label).toBe('лицензия не указана');
    expect(licenseBadge(ver('v1', 1, { license: null }))).toBeNull();
    expect(licenseBadge(ver('origin', 0, { license: 'CC BY-NC 4.0' }))).toBeNull();
  });

  it('цена: бесплатно у локальных, «≈» у оценки', () => {
    expect(priceText({ amount: null, unit: 'free', approx: false })).toBe('бесплатно');
    expect(priceText({ amount: 0.12, unit: 'usd', approx: true })).toBe('≈ $0.12');
    expect(priceText(null)).toBeNull();
  });

  it('«что сделано» у правки без ИИ — из события нити', () => {
    const t = thread([ver('origin', 0), ver('v1', 1, { jobId: 'j1' })]);
    const events = [{ at: '', kind: 'edited', jobId: 'j1', text: 'Человек свёл стемы (vocals −3 дБ, drums) (без ИИ): intro.mp3, от: версия 1' }];
    expect(doneText(t, t.versions[1], events)).toBe('Без ИИ · Свёл стемы (vocals −3 дБ, drums)');
  });

  it('итог запуска: отмена, обрыв, недобор вариантов', () => {
    expect(launchEndNote('running', 0, 2)).toBeNull();
    expect(launchEndNote('done', 2, 2)).toBeNull();
    expect(launchEndNote('done', 1, 2)).toBe('Готово вариантов: 1 из 2');
    expect(launchEndNote('failed', 0, 1)).toBe('Запуск не получился');
  });
});

describe('карточка: сведение стемов', () => {
  it('уходят только звучащие стемы с громкостью, от версии и ревизии; новая версия применяется', async () => {
    const t = thread([ver('v1', 1, { files: [{ role: 'stem:vocals', path: 'v' }, { role: 'stem:drums', path: 'd' }, { role: 'stem:bass', path: 'b' }] })]);
    __applyThreads('s1', 'p1', st(5, [t]));
    const stems = versionStems(t.versions[0]);
    const plan = mixPlan(stems, setGain(toggleMute(EMPTY_MIXER, 'stem:drums'), 'stem:vocals', -3));
    const next = st(6, [{ ...t, versions: [...t.versions, ver('v2', 2)], currentVersionId: 'v2' }]);
    const mix = vi.spyOn(audioApi, 'mix').mockResolvedValue({ threadId: 't1', versionId: 'v2', number: 2, jobId: 'j', state: next });

    expect(await mixStems('p1', 's1', t, 'v1', plan)).toBe(true);
    expect(mix).toHaveBeenCalledWith('p1', 's1', 't1', {
      stems: [{ role: 'stem:vocals', gainDb: -3 }, { role: 'stem:bass', gainDb: 0 }], baseVersionId: 'v1', revision: 5,
    });
    expect(getThreadsState('s1').threads[0].currentVersionId).toBe('v2');
  });

  it('всё заглушено — запроса нет', async () => {
    const t = thread([ver('v1', 1, { files: [{ role: 'stem:vocals', path: 'v' }] })]);
    const mix = vi.spyOn(audioApi, 'mix');
    const plan = mixPlan(versionStems(t.versions[0]), toggleMute(EMPTY_MIXER, 'stem:vocals'));
    expect(await mixStems('p1', 's1', t, 'v1', plan)).toBe(false);
    expect(mix).not.toHaveBeenCalled();
  });
});

describe('карточка: сохранение', () => {
  const t = thread([ver('origin', 0), ver('v2', 2)]);

  it('«Сохранить в проект» — следующей версией', async () => {
    const save = vi.spyOn(audioApi, 'save').mockResolvedValue({ path: 'music/intro.v2.mp3', files: ['music/intro.v2.mp3'] });
    expect(await saveVersion('p1', 's1', t, 'v2')).toEqual({ ok: true, path: 'music/intro.v2.mp3' });
    expect(save).toHaveBeenCalledWith('p1', 's1', 't1', { versionId: 'v2', mode: 'nextVersion' });
  });

  it('«Сохранить как…» на занятое имя: 409 name_taken отдаёт свободное имя, его можно взять', async () => {
    const err = Object.assign(new Error('Файл music/hit.mp3 уже есть'), {
      status: 409, body: { code: 'name_taken', error: 'Файл music/hit.mp3 уже есть', suggestion: 'music/hit.v2.mp3' },
    });
    vi.spyOn(audioApi, 'save').mockRejectedValue(err);
    const r = await saveVersion('p1', 's1', t, 'v2', { folder: 'music', fileName: 'hit' });
    expect(r).toEqual({ ok: false, error: 'Файл music/hit.mp3 уже есть', suggestion: 'music/hit.v2.mp3' });
    // Занятое имя показывает диалог, а не тост
    expect(dispatched.some(e => e.type === 'cc-local-toast')).toBe(false);
    expect(splitSuggestion('music/hit.v2.mp3')).toEqual({ folder: 'music', fileName: 'hit.v2.mp3' });
    expect(splitSuggestion('hit.v2.mp3')).toEqual({ folder: '', fileName: 'hit.v2.mp3' });
  });

  it('другой 409 подсказкой не считается', async () => {
    vi.spyOn(audioApi, 'save').mockRejectedValue(Object.assign(new Error('x'), { status: 409, body: { code: 'revision_conflict' } }));
    expect(await saveVersion('p1', 's1', t, 'v2', { folder: '', fileName: 'a' })).toMatchObject({ ok: false, suggestion: null });
  });

  it('личный чат: кнопка — «Скачать», сохранение в проект не уходит на сервер', async () => {
    expect(saveKind(true)).toBe('download');
    expect(saveKind(false)).toBe('project');
    const save = vi.spyOn(audioApi, 'save');
    expect(await saveVersion('personal', 's1', t, 'v2')).toMatchObject({ ok: false });
    expect(save).not.toHaveBeenCalled();
  });

  it('«Взять» — текущая версия нити от свежей ревизии', async () => {
    __applyThreads('s1', 'p1', st(9, [t]));
    const cur = vi.spyOn(audioApi, 'current').mockResolvedValue(st(10, [{ ...t, currentVersionId: 'origin' }]));
    expect(await takeVersion('p1', 's1', t, 'origin')).toBe(true);
    expect(cur).toHaveBeenCalledWith('p1', 's1', 't1', 'origin', 9);
  });
});

describe('стор нити: контракт выделения для поля «Кусок»', () => {
  it('одно выделение на нить; панель отмечает открытое поле, карточка просит операцию', () => {
    setSelection('s1', 't1', { start: 1, end: 2.5, versionId: 'v2' });
    expect(getSelection('s1', 't1')).toEqual({ start: 1, end: 2.5, versionId: 'v2' });
    expect(getSelection('s1', 't2')).toBeNull();
    setSelection('s1', 't1', null);
    expect(getSelection('s1', 't1')).toBeNull();

    setPieceFieldOpen('s1', 't1');
    expect(getPieceFieldOpen('s1')).toBe('t1');
    setPieceFieldOpen('s1', null);
    expect(getPieceFieldOpen('s1')).toBeNull();

    requestOperation('s1', 't1', 'trim');
    requestOperation('s1', 't1', 'repaint');
    expect(getOperationRequest('s1')).toEqual({ threadId: 't1', op: 'repaint', seq: 2 });
    // Панель «Звук» открывается на «Настройках»
    expect((dispatched[dispatched.length - 1] as CustomEvent).detail).toEqual({ key: 'sound', tab: 'settings' });
  });
});

describe('волна: кэш пиков', () => {
  it('сервер спрашивается раз на версию и роль; кэш не растёт выше потолка', async () => {
    const peaks = vi.spyOn(audioApi, 'peaks').mockImplementation(async () => ({ peaks: [0.5], seconds: 3 }));
    const req = (versionId: string) => ({ scope: 'p1', sessionId: 's1', threadId: 't1', versionId, role: null, points: 240 });
    await loadPeaks(req('v1'));
    await loadPeaks(req('v1'));
    expect(peaks).toHaveBeenCalledTimes(1);
    for (let i = 0; i < CACHE_MAX + 5; i++) await loadPeaks(req(`x${i}`));
    expect(__peaksCacheSize()).toBe(CACHE_MAX);
  });

  it('LRU: прочитанное недавно не вытесняется', () => {
    const m = new Map<string, number>();
    lruSet(m, 'a', 1, 2);
    lruSet(m, 'b', 2, 2);
    lruGet(m, 'a');
    lruSet(m, 'c', 3, 2);
    expect([...m.keys()]).toEqual(['a', 'c']);
  });
});

describe('микшер: узел ушедшего стема', () => {
  it('снимается из графа и отпускает файл', () => {
    const el = { pause: vi.fn(), removeAttribute: vi.fn(), load: vi.fn() };
    const source = { disconnect: vi.fn() };
    const gain = { disconnect: vi.fn() };
    dropNode({ el, source, gain } as unknown as Parameters<typeof dropNode>[0]);
    expect(el.pause).toHaveBeenCalled();
    expect(el.removeAttribute).toHaveBeenCalledWith('src');
    expect(source.disconnect).toHaveBeenCalled();
    expect(gain.disconnect).toHaveBeenCalled();
  });
});

describe('волна: общий максимум', () => {
  it('тихая волна растягивается, разница громкости A/B сохраняется', () => {
    expect(normalizeJoint([[0.1, 0.2], null, [0.05]])).toEqual([[0.5, 1], null, [0.25]]);
  });
});
