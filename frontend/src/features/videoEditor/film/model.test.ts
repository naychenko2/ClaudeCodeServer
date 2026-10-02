import { describe, expect, it } from 'vitest';
import { film, FILM_PATH, scene } from '../mocks';
import { filmChip, filmDuration, staleFilm } from '../strip/summary';
import {
  buildView, clampTrim, cutLabel, newFilmPath, sceneOfItem, snapshotOf, soundPreset, spentText, staleReasons, trimLabel,
} from './model';

describe('фильм: подписи и длительность', () => {
  it('склейки под стыком', () => {
    expect(cutLabel({ type: 'butt', sec: 0 })).toBe('| встык');
    expect(cutLabel({ type: 'dissolve', sec: 1 })).toBe('≈ наплыв 1 с');
    expect(cutLabel({ type: 'fade', sec: 0.5 })).toBe('■ затемнение 0,5 с');
  });
  it('наплыв укорачивает фильм честно', () => {
    // 8 + 8 + 7 − 1 (наплыв)
    expect(filmDuration(film().document)).toBe(22);
    expect(filmChip('утро', film()).text).toBe('утро · 3 сцены · 0:22');
  });
  it('подрезка шагом 0,5 с, «✂ 7 с из 10 с»', () => {
    expect(trimLabel(film().document.items[2])).toBe('✂ 7 с из 10 с');
    expect(trimLabel(film().document.items[0])).toBe('✂ 8 с');
    expect(clampTrim([0.3, 9.9], 8)).toEqual([0.5, 8]);
    expect(clampTrim([4, 4], 8)).toEqual([4, 4.5]);
  });
  it('потрачено — только отображение', () => {
    expect(spentText(film().spent)).toBe('$6.40 · GPU 5 мин');
  });
});

describe('сборка и «обновлена»', () => {
  it('без сцен — пусто; идёт — прогресс; собран — результат', () => {
    expect(buildView(film({ document: { ...film().document, items: [], cuts: [] } })).kind).toBe('empty');
    expect(buildView(film({ build: { state: 'running', progress: 0.4 } }))).toEqual({ kind: 'running', progress: 0.4 });
    expect(buildView(film({ build: { state: 'waiting', progress: 0 } })).kind).toBe('waiting');
    const built = film({ document: { ...film().document, builds: [{ file: 'video/утро/film.mp4', sourceHash: 'ab', at: '2026-10-02T16:00:00Z' }] } });
    expect(buildView(built)).toEqual({ kind: 'done', file: 'video/утро/film.mp4', stale: [] });
    expect(staleFilm(built)).toBe(false);
  });
  it('новый файл сцены — точка «обновлена» и причина пересборки', () => {
    const f = film({
      document: { ...film().document, builds: [{ file: 'video/утро/film.mp4', sourceHash: 'ab', at: '2026-10-02T16:00:00Z' }] },
      marks: [{ index: 2, claude: false, updated: true, stale: false }],
    });
    expect(staleReasons(f)).toEqual(['сцена 3 обновлена']);
    expect(staleFilm(f)).toBe(true);
  });
});

describe('стыки с соседями', () => {
  it('заготовка «Звука»: длина фильма, песня, инструментал, привязка к .film', () => {
    expect(soundPreset('утро', film())).toMatchObject({ mode: 'music', op: 'song', duration: 22, instrumental: true, bindTo: FILM_PATH, from: 'под фильм «утро»' });
  });
  it('снимок сцены для строки фильма — только файлы-кадры', () => {
    const s = scene('s1', { settings: { ...scene('s1').settings, frameB: { kind: 'image', threadId: 't', versionId: 'v' } } });
    expect(snapshotOf(s)).toEqual({ text: 'Камера медленно приближается к окну', frameA: 'video/утро/кадры/кадр-2.png', provider: 'fal', model: 'veo-3.1', durationSec: 8 });
    expect(sceneOfItem([scene('s2', { savedFiles: [{ versionId: 'ver-1', path: 'video/утро/scene-02.mp4' }] })], film().document.items[1])?.sceneId).toBe('s2');
  });
  it('новый фильм — папка и файл по имени', () => {
    expect(newFilmPath('утро в горах')).toBe('video/утро-в-горах/утро-в-горах.film');
    expect(newFilmPath('  ')).toBeNull();
  });
});
