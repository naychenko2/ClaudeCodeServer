import { describe, expect, it } from 'vitest';
import type { Persona, Project, ProjectRoleView, Session } from '../../../types';
import { type PersonaAction, canChangeCompanion, canPersona, personaRole } from '../personaRights';

// Ожидаемая матрица §2 разреза, как данные: нет роли/владелец — всё; читатель — ничего;
// редактор — только чат (решение Андрея 12.11).
// Таблица — 4 роли × 14 действий = 56 проверок; новое действие в PersonaAction без строки
// здесь не скомпилируется.
const ACTIONS: PersonaAction[] = [
  'chat', 'editCard', 'createPersona', 'editZoneModel', 'delete', 'makeLead', 'automation',
  'memoryWrite', 'memoryNote', 'bindingWrite', 'bindingMcpPlugin', 'skills', 'aiTeam',
  'bindingForeignTarget',
];

const EXPECTED: Record<PersonaAction, Record<'none' | 'owner' | 'editor' | 'reader', boolean>> = {
  chat: { none: true, owner: true, editor: true, reader: false },
  editCard: { none: true, owner: true, editor: false, reader: false },
  // Создание проектной персоны — WriteContent (§2): читателю бэк откажет.
  createPersona: { none: true, owner: true, editor: false, reader: false },
  editZoneModel: { none: true, owner: true, editor: false, reader: false },
  delete: { none: true, owner: true, editor: false, reader: false },
  makeLead: { none: true, owner: true, editor: false, reader: false },
  automation: { none: true, owner: true, editor: false, reader: false },
  memoryWrite: { none: true, owner: true, editor: false, reader: false },
  memoryNote: { none: true, owner: true, editor: false, reader: false },
  bindingWrite: { none: true, owner: true, editor: false, reader: false },
  bindingMcpPlugin: { none: true, owner: true, editor: false, reader: false },
  skills: { none: true, owner: true, editor: false, reader: false },
  aiTeam: { none: true, owner: true, editor: false, reader: false },
  // Правка привязки к цели вне этого проекта — за владельцем (И-4). Различения своей и
  // чужой цели в панели больше нет: после решения 12.11 (W4-FA1) не осталось роли с
  // bindingWrite без этого права. Ключ держит паритет с бэком, а не гейт панели.
  bindingForeignTarget: { none: true, owner: true, editor: false, reader: false },
};

const ROLE_KEYS = ['none', 'owner', 'editor', 'reader'] as const;
const ROLE_VALUE: Record<(typeof ROLE_KEYS)[number], ProjectRoleView | null> = {
  none: null, owner: 'owner', editor: 'editor', reader: 'reader',
};

describe('canPersona — матрица §2: 4 роли × 14 действий', () => {
  for (const roleKey of ROLE_KEYS) {
    for (const action of ACTIONS) {
      it(`${roleKey} × ${action} → ${EXPECTED[action][roleKey]}`, () => {
        expect(canPersona(ROLE_VALUE[roleKey], action)).toBe(EXPECTED[action][roleKey]);
      });
    }
  }
});

// Проекты — только id и myRole: роль берётся из myRole, ownerId здесь не читается никогда.
const PROJECTS: Pick<Project, 'id' | 'myRole'>[] = [
  { id: 'p-editor', myRole: 'editor' },
  { id: 'p-reader', myRole: 'reader' },
  { id: 'p-owner', myRole: 'owner' },
  { id: 'p-plain' }, // обычный проект / старый бэк: myRole нет
];

describe('personaRole', () => {
  it('роль — из myRole проекта персоны', () => {
    expect(personaRole({ projectId: 'p-editor' }, PROJECTS)).toBe('editor');
    expect(personaRole({ projectId: 'p-reader' }, PROJECTS)).toBe('reader');
    expect(personaRole({ projectId: 'p-owner' }, PROJECTS)).toBe('owner');
  });

  it('нет myRole, нет проекта, нет projectId — null (= как владелец, старый бэк)', () => {
    expect(personaRole({ projectId: 'p-plain' }, PROJECTS)).toBeNull();
    expect(personaRole({ projectId: 'нет-такого' }, PROJECTS)).toBeNull();
    expect(personaRole({ projectId: 'p-editor' }, [])).toBeNull();
    const globalPersona: Pick<Persona, 'projectId'> = {};
    expect(personaRole(globalPersona, PROJECTS)).toBeNull();
  });
});

describe('canChangeCompanion — смена собеседника в чате', () => {
  const own: Pick<Session, 'createdByUserId'> = { createdByUserId: 'me' };
  const someoneElses: Pick<Session, 'createdByUserId'> = { createdByUserId: 'other' };

  it('нет роли (обычный проект и старый бэк) — разрешено', () => {
    expect(canChangeCompanion({}, someoneElses, 'me')).toBe(true);
    expect(canChangeCompanion(undefined, undefined, 'me')).toBe(true);
    expect(canChangeCompanion(null, null, 'me')).toBe(true);
  });

  it('владелец — в любом чате', () => {
    expect(canChangeCompanion({ myRole: 'owner' }, someoneElses, 'me')).toBe(true);
    expect(canChangeCompanion({ myRole: 'owner' }, own, 'me')).toBe(true);
  });

  it('читатель — ни в каком, даже в своём', () => {
    expect(canChangeCompanion({ myRole: 'reader' }, own, 'me')).toBe(false);
  });

  it('редактор — только в чате, который создал сам', () => {
    expect(canChangeCompanion({ myRole: 'editor' }, own, 'me')).toBe(true);
    expect(canChangeCompanion({ myRole: 'editor' }, someoneElses, 'me')).toBe(false);
    // автор неизвестен (старая запись) — чужой чат
    expect(canChangeCompanion({ myRole: 'editor' }, { createdByUserId: null }, 'me')).toBe(false);
    expect(canChangeCompanion({ myRole: 'editor' }, undefined, 'me')).toBe(false);
  });
});
