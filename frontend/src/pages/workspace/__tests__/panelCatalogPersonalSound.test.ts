// Регистрация панели «Звук из Личного» (Ф-2) в каталоге панелей.
//
// Дефект: ключ `personal-sound` не был зарегистрирован ни в PANEL_KEYS, ни в
// PANEL_META, ни в PANEL_HOME, ни в CHAT_RIGHT_KEYS — PanelZone отсекал панель
// по keyAllowed, и кнопки не было ни в рельсе личного чата, ни в ящике рельсы.
//
// Инварианты:
//  • панель доступна в ЛИЧНОМ чате (CHAT_RIGHT_KEYS);
//  • в ПРОЕКТНОМ чате её нет (ключа нет в WORKSPACE_KEYS) — проект и личный
//    чат разведены набором, а не флагом;
//  • ключ в каталоге и ключ манифеста совпадают ПОБАЙТОВО (манифест не трогаем).

import { describe, it, expect } from 'vitest';
import {
  CHAT_RIGHT_KEYS, PANEL_HOME, PANEL_KEYS, PANEL_META, WORKSPACE_KEYS,
  isPanelKey, keyAllowed, panelMetaResolved,
} from '../panelCatalog';
import { PERSONAL_SOUND_KEY } from '../../../features/audioEditor/panel/PersonalSoundPanel';
import { manifest } from '../../../features/audioEditor/manifest';

const KEY = 'personal-sound';

describe('каталог панелей: «Звук из Личного» зарегистрирован', () => {
  it('ключ есть в реестре панелей', () => {
    expect(PANEL_KEYS as readonly string[]).toContain(KEY);
    expect(isPanelKey(KEY)).toBe(true);
  });

  it('у панели есть мета: заголовок «Звук из Личного»', () => {
    expect(PANEL_META[KEY].title).toBe('Звук из Личного');
    expect(panelMetaResolved(KEY).title).toBe('Звук из Личного');
  });

  it('домашняя зона — правая рельса', () => {
    expect(PANEL_HOME[KEY]).toBe('right');
  });

  it('панель доступна в личном чате (CHAT_RIGHT_KEYS)', () => {
    expect(CHAT_RIGHT_KEYS as readonly string[]).toContain(KEY);
    expect(keyAllowed(KEY, CHAT_RIGHT_KEYS)).toBe(true);
  });

  it('в проектном чате панели нет (WORKSPACE_KEYS)', () => {
    expect(WORKSPACE_KEYS as readonly string[]).not.toContain(KEY);
    expect(keyAllowed(KEY, WORKSPACE_KEYS)).toBe(false);
  });

  it('ключ каталога и ключ манифеста совпадают побайтово', () => {
    expect(PERSONAL_SOUND_KEY).toBe(KEY);
    expect(PERSONAL_SOUND_KEY.length).toBe(KEY.length);
  });
});

// Связка манифеста и рельсы — ровно та, что в ChatsPage: вклад слота
// workspace-panel-def попадает в правую зону личного чата, если его ключ
// разрешён набором CHAT_RIGHT_KEYS и он сам доступен без проекта.
describe('манифест звука: вклад панели «Звук из Личного»', () => {
  const defs = manifest.slots['workspace-panel-def'] as Array<{
    name: string;
    action?: { isAvailable?: (projectId: string | null) => boolean; title?: string };
  }>;
  const def = defs.find(d => d.name === PERSONAL_SOUND_KEY);

  it('вклад отдаётся под ключом панели', () => {
    expect(def, 'вклад «Звук из Личного» в слоте workspace-panel-def').toBeTruthy();
    expect(def!.name).toBe(KEY);
    expect(def!.action?.title).toBe(PANEL_META[KEY].title);
  });

  it('доступен только личному чату (projectId === null)', () => {
    expect(def!.action?.isAvailable?.(null)).toBe(true);
    expect(def!.action?.isAvailable?.('p1')).toBe(false);
  });

  it('в личном чате вклад разрешён, в проекте — нет', () => {
    expect(keyAllowed(KEY, CHAT_RIGHT_KEYS)).toBe(true);
    expect(keyAllowed(KEY, WORKSPACE_KEYS)).toBe(false);
  });
});
