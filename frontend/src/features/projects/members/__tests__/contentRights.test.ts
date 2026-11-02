// Точка права записи контента (S2c-F4a, +S2c-F4k 'media.edit'): таблица 22 дверь ×
// 4 вида роли (нет роли, owner, editor, reader) = 88 проверок. Логика чистая, без
// React и без стенда. Ожидание здесь — независимая формулировка правила: открыть в
// модуле закрытую дверь или пустить читателя — тест краснеет (мутация).
import { describe, expect, it } from 'vitest';
import type { Project } from '../../../../types';
import { canWriteContent, type ContentDoor } from '../contentRights';

// Состав дверей — тот же, что ContentDoor; сокращение состава краснеет на длине.
const DOORS: ContentDoor[] = [
  'file.save', 'file.create', 'file.mkdir', 'file.rename', 'file.upload',
  'file.delete', 'file.toMarkdown', 'dir.delete', 'file.revert', 'file.saveFromUrl',
  'file.office', 'git.stageHunk', 'git.restoreVersion', 'media.edit',
  'note.create', 'note.edit', 'note.delete', 'note.move', 'note.folder',
  'note.comment', 'note.moveSource', 'note.tasks',
];

// Закрыто редактору (закрыто на бэке): зеркало того, чего в модуле быть не должно.
// 'media.edit' — шлюзы медиа-редакторов на бэке под полным Write (ImageEditScopeGate):
// участник через них пишет 404, закрыта и редактору.
const EDITOR_CLOSED: ContentDoor[] = [
  'dir.delete', 'file.revert', 'file.saveFromUrl', 'file.office',
  'git.stageHunk', 'git.restoreVersion', 'media.edit', 'note.moveSource', 'note.tasks',
];

const ROLE_KINDS = ['none', 'owner', 'editor', 'reader'] as const;

// Ожидание по правилу задачи — независимо от реализации модуля.
function expectedFor(role: (typeof ROLE_KINDS)[number], door: ContentDoor): boolean {
  if (role === 'none' || role === 'owner') return true;
  if (role === 'reader') return false;
  return !EDITOR_CLOSED.includes(door);
}

describe('canWriteContent (S2c-F4a)', () => {
  it('состав полный: 22 дверь × 4 роли = 88 проверок', () => {
    expect(DOORS.length).toBe(22);
    expect(EDITOR_CLOSED.length).toBe(9);
  });

  it('таблица: 22 дверь × 4 вида роли', () => {
    for (const role of ROLE_KINDS) {
      for (const door of DOORS) {
        const project: Pick<Project, 'myRole'> = role === 'none' ? {} : { myRole: role };
        expect(canWriteContent(project, door), `${role} · ${door}`).toBe(expectedFor(role, door));
      }
    }
  });

  it('проекта нет вовсе (null/undefined) — как без роли: все двери открыты', () => {
    for (const door of DOORS) {
      expect(canWriteContent(null, door)).toBe(true);
      expect(canWriteContent(undefined, door)).toBe(true);
    }
  });
});
