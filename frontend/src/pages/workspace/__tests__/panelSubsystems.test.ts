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
      tasks: 'tasks',
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

// Панели без подсистемного гейта: явный allow-list с обоснованием на строке.
// Сторож полноты ниже (R-S2b-2c): каждый ключ WORKSPACE_KEYS обязан быть заявлен
// здесь или в PANEL_SUBSYSTEM — новая панель обязана сказать, ведёт ли она в
// вертикаль. Добавить ключ в WORKSPACE_KEYS без решения — сторож красный.
const PANELS_WITHOUT_GATE: Record<string, string> = {
  // Основные панели воркспейса: показывают содержимое проекта, вертикали за ними нет.
  chats: 'основная панель: вертикали нет',
  files: 'основная панель: вертикали нет',
  docs: 'основная панель: корпус документации, вертикали нет',
  team: 'основная панель: персоны проекта, ключа в реестре подсистем нет',
  terminal: 'основная панель: терминал, ключа в реестре подсистем нет',
  // Сессионные панели: видимость по артефактам сессии (SESSION_KEYS), не по подсистеме.
  plan: 'сессионная панель: видимость по артефакту',
  agents: 'сессионная панель: видимость по артефакту',
  context: 'сессионная панель: видимость по артефакту',
  // Центральная панель: существует, пока в центре открыт документ (CENTER_KEYS).
  toc: 'центральная панель: живёт, пока открыт источник',
  // Панели слота workspace-panel-def: выключенная подсистема не отдаёт ни одного
  // вклада (реестр слота), ключ в рельсе не появляется.
  characters: 'панель слота: скрыта реестром вкладов',
  voices: 'панель слота: скрыта реестром вкладов',
  // Core-фича без бэкового тумблера: гейтится своим isAvailable (local-model-stats).
  localModel: 'фича: свой isAvailable, бэкового тумблера нет',
  // Панель генерации: свой гейт (фича-флаг), не подсистема.
  chatContext: 'панель генерации: свой фич-гейт',
  // Маркер семейства плагинов: свой гейт (флаг + возможности проекта).
  pluginPanels: 'маркер семейства плагинов: свой гейт',
};

describe('Сторож полноты: каждый ключ WORKSPACE_KEYS заявил своё место', () => {
  it('каждый ключ — в карте PANEL_SUBSYSTEM или в allow-list', () => {
    for (const k of WORKSPACE_KEYS) {
      const declared = k in PANEL_SUBSYSTEM || k in PANELS_WITHOUT_GATE;
      expect(declared, `ключ ${k}: ни в PANEL_SUBSYSTEM, ни в allow-list`).toBe(true);
    }
  });

  it('панель заявлена ровно в одном списке: не в карте и в allow-list сразу', () => {
    for (const k of Object.keys(PANEL_SUBSYSTEM)) {
      expect(PANELS_WITHOUT_GATE, `ключ ${k}: и в карте, и в allow-list`).not.toHaveProperty(k);
    }
  });

  it('у каждой позиции allow-list есть обоснование', () => {
    for (const [k, why] of Object.entries(PANELS_WITHOUT_GATE)) {
      expect(why.trim().length, `ключ ${k}: пустое обоснование`).toBeGreaterThan(0);
    }
  });
});
