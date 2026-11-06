import { describe, it, expect } from 'vitest';
import { PANEL_SUBSYSTEM, WORKSPACE_KEYS, workspaceKeysFor } from '../panelCatalog';
import { SUBSYSTEMS } from '../../../lib/subsystems';

// Предикат строится по списку активных ключей — той же форме, что и стор
// (setAllSubsystems принимает массив активных ключей).
const isEnabledOf = (active: readonly string[]) => (key: string) => active.includes(key);
const ALL_KEYS: readonly string[] = Object.values(SUBSYSTEMS);

describe('PANEL_SUBSYSTEM: панели рельсы привязаны к ключам подсистем', () => {
  it('каждое значение — ключ из реестра SUBSYSTEMS', () => {
    for (const [panel, sub] of Object.entries(PANEL_SUBSYSTEM)) {
      expect(ALL_KEYS, `панель ${panel} → ${sub}`).toContain(sub);
    }
  });

  it('соответствие пар — как согласовано в S2b-2c (никаких лишних и пропущенных)', () => {
    expect(PANEL_SUBSYSTEM).toEqual({
      changes: 'git',
      dossiers: 'dossiers',
      knowledge: 'knowledge',
      notes: 'notes',
      graph: 'code-graph',
      arch: 'architecture',
      skills: 'skills',
      preview: 'project-services',
      video: 'video',
    });
  });
});

describe('workspaceKeysFor: набор рельсы без панелей выключенных подсистем', () => {
  it('все включены — набор равен WORKSPACE_KEYS, порядок тот же', () => {
    expect(workspaceKeysFor(isEnabledOf(ALL_KEYS))).toEqual([...WORKSPACE_KEYS]);
  });

  it('каждая пара: ключ выключен — панели в наборе нет; включён — она на своём месте', () => {
    for (const [panel, sub] of Object.entries(PANEL_SUBSYSTEM)) {
      const off = workspaceKeysFor(isEnabledOf(ALL_KEYS.filter(k => k !== sub)));
      expect(off, `панель ${panel} при выключенной ${sub}`).not.toContain(panel);
      const on = workspaceKeysFor(isEnabledOf([sub]));
      expect(on, `панель ${panel} при включённой ${sub}`).toContain(panel);
    }
  });

  it('одна выключенная подсистема убирает ровно свою панель', () => {
    const off = workspaceKeysFor(isEnabledOf(ALL_KEYS.filter(k => k !== SUBSYSTEMS.git)));
    expect(off).toEqual(WORKSPACE_KEYS.filter(k => k !== 'changes'));
  });

  it('все выключены — ключи без пары остаются: набор = WORKSPACE_KEYS минус панели-подсистемы', () => {
    const mapped: readonly string[] = Object.keys(PANEL_SUBSYSTEM);
    expect(workspaceKeysFor(isEnabledOf([]))).toEqual(WORKSPACE_KEYS.filter(k => !mapped.includes(k)));
  });

  it('маркер pluginPanels (семейство plugin:*) вне подсистемного гейта', () => {
    // Панели плагинов гейтятся флагом и возможностями проекта (свой путь), а не
    // подсистемой: маркер семейства обязан оставаться в наборе при любом составе.
    expect(workspaceKeysFor(isEnabledOf([]))).toContain('pluginPanels');
  });
});
