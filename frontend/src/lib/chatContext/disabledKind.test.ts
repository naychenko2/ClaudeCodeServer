// Сторож признака «неактивного вида» (S2b-3a): на реальном слоте context-kind и сторе подсистем.
// Выключенная вертикаль выпадает из реестра (гейт чтения в registryCore.getSlotContributions),
// и её вид — «выключен»: ни иконки, ни действий, ни «Формата». Включённая — нет.
// Причина неактивности — два состояния с разными текстами: «выключен администратором» (ключа
// нет) и «не загрузился» (ключ есть, фронтовый MF-remote вертикали не зарегистрирован);
// различает их поле `missing` объекта — см. describe ниже.
// Файл «project-file» — не выключенный вид: кита у него нет by design.

import { beforeEach, describe, expect, it } from 'vitest';
import { registerSubsystem, SLOT_CONTEXT_KIND } from '../subsystems/registryCore';
import { __resetSubsystems, isSubsystemEnabled, setAllSubsystems } from '../subsystems';
import { disabledKindReason, disabledKindText, isKindDisabled } from './disabledKind';
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
    expect(disabledKindReason('project-file', getKindRegistry(), true)).toBeNull();
    setAllSubsystems(['imageeditor']);
    expect(isKindDisabled('project-file', getKindRegistry())).toBe(false);
    expect(disabledKindReason('project-file', getKindRegistry(), false)).toBeNull();
  });
});

// Два состояния неактивного вида (хвост b39c753a, п. 1): текст зависит от того, знает ли бэк
// провайдера вида. «Незагруженная» вертикаль моделируется отсутствием её манифеста: ключ есть
// в сторе включённых, вклада в реестре нет — ровно то, что остаётся после таймаута
// loadSubsystemRemotes (App.tsx). Вид «audio» взят настоящий (AudioContextKind.cs:30-33),
// его манифеста в этом файле нет ни при каком наборе стора.
describe('причина неактивности: выключенная вертикаль против незагруженного MF-remote', () => {
  it('ключа нет в сторе, бэковый провайдер молчит (missing=true): «Раздел выключен администратором»', () => {
    setAllSubsystems([]);
    const reg = getKindRegistry();
    expect(reg.has('audio')).toBe(false);
    const reason = disabledKindReason('audio', reg, true);
    expect(reason).toBe('admin');
    expect(reason && disabledKindText(reason)).toBe('Раздел выключен администратором');
  });

  it('ключ в сторе, вида в реестре нет, бэк описывает объект (missing=false): «Раздел не загрузился»', () => {
    setAllSubsystems(['audioeditor']);
    expect(isSubsystemEnabled('audioeditor')).toBe(true);
    const reg = getKindRegistry();
    expect(reg.has('audio')).toBe(false);
    const reason = disabledKindReason('audio', reg, false);
    expect(reason).toBe('notLoaded');
    expect(reason && disabledKindText(reason)).toBe('Раздел не загрузился');
  });

  it('один и тот же вид при разных признаках даёт разные тексты', () => {
    setAllSubsystems(['audioeditor']);
    const reg = getKindRegistry();
    const off = disabledKindReason('audio', reg, true);
    const failed = disabledKindReason('audio', reg, false);
    expect([off, failed]).toEqual(['admin', 'notLoaded']);
    expect(disabledKindText(off!)).not.toBe(disabledKindText(failed!));
  });
});
