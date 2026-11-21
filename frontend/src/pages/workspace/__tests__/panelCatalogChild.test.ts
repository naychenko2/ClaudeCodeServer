// Детская часть набора рельсы (Ф1b, ADR-041 ред. 8, решение 76; разрез §14.1):
// childWorkspaceKeys — пересечение ключей экрана с childSurface.panels, default-deny.
// Копия замороженного снимка Б18a (ChildModeAllowList.Surface.Panels, Core) — здесь
// как контракт имён: смена на бэке обязана краснеть и здесь (снимок бэка сторожит Б18b).
// К9л: сама копия сверяется сторожем в конце файла — он читает перечень прямо из
// бэкового файла (прежний срез копии был без 'chats' — находка сдачи К9к).
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { WORKSPACE_KEYS, childWorkspaceKeys, isPanelKey } from '../panelCatalog';
import type { ChildSurface } from '../../../features/account/types';

// Ровно Panels из ChildModeAllowList.Surface (backend/ClaudeHomeServer.Core/Services/ChildMode/ChildModeAllowList.cs:23)
const CHILD_PANELS = ['files', 'knowledge', 'notes', 'chatContext', 'tasks', 'chats'];
// Тот же набор в порядке рельсы (порядок WORKSPACE_KEYS: chats → files → tasks →
// knowledge → notes → chatContext): пересечение сохраняет порядок экрана, а не снимка — поэтому
// ожидаемые списки ниже в этом порядке, а состав сверяется с CHILD_PANELS отдельно.
const RAIL_CHILD = ['chats', 'files', 'tasks', 'knowledge', 'notes', 'chatContext'];
const surface: ChildSurface = { panels: CHILD_PANELS, composerFeatures: [], feedKinds: [] };

describe('снимок Б18a и реестр: контракт имён', () => {
  it('каждый ключ снимка — живой ключ реестра и доступен в воркспейсе', () => {
    for (const k of CHILD_PANELS) {
      expect(isPanelKey(k), `ключ ${k} из снимка есть в реестре`).toBe(true);
      expect(WORKSPACE_KEYS as readonly string[], `ключ ${k} доступен в воркспейсе`).toContain(k);
    }
  });
});

describe('childWorkspaceKeys: пересечение с childSurface.panels', () => {
  it('полный набор экрана + снимок ребёнка — ровно [chats, files, tasks, knowledge, notes, chatContext]', () => {
    const out = childWorkspaceKeys([...WORKSPACE_KEYS], surface);
    expect(out).toEqual(RAIL_CHILD);
    expect([...out].sort()).toEqual([...CHILD_PANELS].sort()); // состав — ровно снимок, без лишнего и без потерь
  });

  it('default-deny: пустой снимок — пустой набор, не взрослый', () => {
    expect(childWorkspaceKeys([...WORKSPACE_KEYS], { panels: [], composerFeatures: [], feedKinds: [] })).toEqual([]);
  });

  it('ключа нет в наборе экрана — панели нет, даже если он в снимке', () => {
    // ключ реестра, которого нет в WORKSPACE_KEYS (раздел хаба) — в пересечении не появляется
    expect(childWorkspaceKeys(['files', 'knowledge'], { ...surface, panels: [...CHILD_PANELS, 'notesList'] })).toEqual(['files', 'knowledge']);
    // и вовсе незнакомый ключ — тоже
    expect(childWorkspaceKeys([...WORKSPACE_KEYS], { ...surface, panels: ['files', 'notAKey'] })).toEqual(['files']);
  });

  it('порядок — входящих ключей (порядок рельсы), а не снимка', () => {
    const shuffled: ChildSurface = { ...surface, panels: ['tasks', 'chatContext', 'knowledge', 'files', 'notes', 'chats'] };
    const out = childWorkspaceKeys([...WORKSPACE_KEYS], shuffled);
    expect(out).toEqual(RAIL_CHILD); // порядок экрана, не переставленного снимка
    const at = (k: string) => out.indexOf(k);
    expect(at('files')).toBeLessThan(at('tasks'));
  });

  it('ключ плагина (plugin:*) не проходит: в снимок его не заводят, пересечение не пропускает', () => {
    const withPlugin: ChildSurface = { ...surface, panels: [...CHILD_PANELS, 'plugin:acme/x:p1'] };
    expect(childWorkspaceKeys([...WORKSPACE_KEYS], withPlugin)).toEqual(RAIL_CHILD);
    expect(childWorkspaceKeys(['plugin:acme/x:p1', 'files'], surface)).toEqual(['files']);
  });

  it('фильтр не меняет входной массив и не дублирует ключ', () => {
    const keys = [...WORKSPACE_KEYS];
    const out = childWorkspaceKeys(keys, surface);
    expect(keys).toEqual([...WORKSPACE_KEYS]); // входной массив тронут не был
    expect(new Set(out).size).toBe(out.length);
  });
});

// ── К9л: копия снимка vs бэк ────────────────────────────────────────────────────────
// CHILD_PANELS выше по файлу — не только фикстура пересечения, но и копия снимка бэка:
// без сверки копия разъезжается с бэком молча (так и вышло с 'chats' — К9к). Сторож
// читает перечень прямо из бэкового файла: изменение бэка без ответа фронта красит
// этот тест (тот же приём, что сторож в WorkspacePage.child.test.tsx, К9к).
const ALLOW_LIST_PATH = resolve(dirname(fileURLToPath(import.meta.url)),
  '../../../../../backend/ClaudeHomeServer.Core/Services/ChildMode/ChildModeAllowList.cs');

function backendPanels(): string[] {
  const src = readFileSync(ALLOW_LIST_PATH, 'utf8');
  // Panels: ["files", …] — строка снимка ChildModeAllowList.Surface (.:23)
  const m = src.match(/Panels:\s*\[([^\]]*)\]/);
  expect(m).not.toBeNull();
  return [...m![1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
}

describe('копия детской поверхности vs бэк (К9л)', () => {
  it('CHILD_PANELS — ровно ChildModeAllowList.Surface.Panels', () => {
    // сравнение по множеству, не по порядку: снимок для фронта — множество
    // разрешённого, порядок рельсы собирает сам фронт (panelCatalog)
    expect([...CHILD_PANELS].sort(), 'копия снимка разъехалась с бэком').toEqual(backendPanels().sort());
  });
});
