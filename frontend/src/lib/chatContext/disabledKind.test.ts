// Сторож признака «выключенного вида» (S2b-3a): на реальном слоте context-kind и сторе подсистем.
// Выключенная вертикаль выпадает из реестра (гейт чтения в registryCore.getSlotContributions),
// и её вид — «выключен»: ни иконки, ни действий, ни «Формата». Включённая — нет.
// Файл «project-file» — не выключенный вид: кита у него нет by design.

import { beforeEach, describe, expect, it } from 'vitest';
import { registerSubsystem, SLOT_CONTEXT_KIND } from '../subsystems/registryCore';
import { __resetSubsystems, setAllSubsystems } from '../subsystems';
import { isKindDisabled } from './disabledKind';
import { getKindApi, getKindRegistry } from './registry';
import type { ContextKindApi } from './types';

const kindApi = (...kinds: string[]): ContextKindApi => ({
  kinds, icon: () => null, actions: () => [], preview: () => null,
  // «Формат» есть у картинки и видео — здесь важна сама функция, не её модель
  format: () => null,
});

// Тестовые вертикали — те же ключи и виды, что у настоящих (imageEditor/manifest.tsx:28,63;
// videoEditor/manifest.tsx:22,29). Без core — гейт включённости действует на них, как на реальных
registerSubsystem({
  key: 'imageeditor', title: 'Картинки', order: 95,
  slots: { [SLOT_CONTEXT_KIND]: [{ name: 'image', action: kindApi('image', 'image-character') as never }] },
});
registerSubsystem({
  key: 'videoeditor', title: 'Видео', order: 97,
  slots: { [SLOT_CONTEXT_KIND]: [{ name: 'video', action: kindApi('video-scene', 'video-film') as never }] },
});

beforeEach(() => { __resetSubsystems(); });

describe('признак выключенного вида контекста', () => {
  it('чистая проверка: вида нет в реестре — выключен; есть — нет', () => {
    const empty = new Map<string, ContextKindApi>();
    expect(isKindDisabled('image', empty)).toBe(true);
    const withImage = new Map<string, ContextKindApi>([['image', kindApi('image')]]);
    expect(isKindDisabled('image', withImage)).toBe(false);
  });

  it('стор без imageeditor: вида image нет в getKindRegistry(), isKindDisabled = true', () => {
    setAllSubsystems([]);
    const reg = getKindRegistry();
    expect(reg.has('image')).toBe(false);
    expect(reg.has('image-character')).toBe(false);
    expect(isKindDisabled('image', reg)).toBe(true);
    expect(isKindDisabled('image-character', reg)).toBe(true);
    expect(isKindDisabled('video-scene', reg)).toBe(true);
    expect(isKindDisabled('video-film', reg)).toBe(true);
  });

  it('стор с imageeditor и videoeditor: виды в реестре, isKindDisabled = false', () => {
    setAllSubsystems(['imageeditor', 'videoeditor']);
    const reg = getKindRegistry();
    expect(reg.has('image')).toBe(true);
    expect(isKindDisabled('image', reg)).toBe(false);
    expect(isKindDisabled('image-character', reg)).toBe(false);
    expect(isKindDisabled('video-scene', reg)).toBe(false);
    expect(isKindDisabled('video-film', reg)).toBe(false);
  });

  it('«Формат» выключенной вертикали: без неё undefined, с любой включённой — функция', () => {
    setAllSubsystems([]);
    expect(getKindApi('image')?.format).toBeUndefined();
    expect(getKindApi('video-film')?.format).toBeUndefined();
    setAllSubsystems(['imageeditor']);
    expect(typeof getKindApi('image')?.format).toBe('function');
    expect(getKindApi('video-film')?.format).toBeUndefined();
    setAllSubsystems(['videoeditor']);
    expect(typeof getKindApi('video-scene')?.format).toBe('function');
    expect(typeof getKindApi('video-film')?.format).toBe('function');
  });

  it('файл «project-file» — не выключенный вид: кита у него нет by design', () => {
    setAllSubsystems([]);
    expect(isKindDisabled('project-file', getKindRegistry())).toBe(false);
    setAllSubsystems(['imageeditor']);
    expect(isKindDisabled('project-file', getKindRegistry())).toBe(false);
  });
});
