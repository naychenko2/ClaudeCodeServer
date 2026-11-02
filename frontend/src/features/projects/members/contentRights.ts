// Точка права записи контента в общем проекте (S2c-F4a): чистая логика «можно ли это
// действие записи» по роли. Без React — проверяется юнит-тестами. Право решает бэк;
// фронт лишь не обещает того, что бэк откажет. Закрытое не выключаем точечно по каждому
// контролу, а прячем вход целиком — решение по умолчанию живёт здесь, в одном месте.

import type { Project } from '../../../types';

// Полный состав дверей записи контента: файлы (сохранить/создать/папку/переименовать/
// загрузить/удалить/в markdown/откатить/из URL/офисный), удаление папки, git (стейдж
// ханки/вернуть версию), медиа (правка/рисование картинки в проект), заметки
// (создать/править/удалить/переместить/папку/коммент/сменить источник/задачи).
// Новый вход записи — сюда, а не в вызывающий компонент.
export type ContentDoor =
  | 'file.save' | 'file.create' | 'file.mkdir' | 'file.rename' | 'file.upload'
  | 'file.delete' | 'file.toMarkdown' | 'dir.delete' | 'file.revert' | 'file.saveFromUrl'
  | 'file.office' | 'git.stageHunk' | 'git.restoreVersion' | 'media.edit'
  | 'note.create' | 'note.edit' | 'note.delete' | 'note.move' | 'note.folder'
  | 'note.comment' | 'note.moveSource' | 'note.tasks';

// Двери, открытые редактору: запись в файлы и обычные заметки. Остальное (удаление папки,
// откат файла, запись из URL, офисные файлы, git-действия, медиа-редакторы, смена
// источника заметки, задачи) закрыто на бэке — здесь закрыто так же, чтобы фронт не
// обещал отказ. 'media.edit' — правка/рисование картинки в проект: шлюзы медиа-редакторов
// на бэке (ImageEditScopeGate и т.п.) под полным Write, участник получает 404
// (SharedProjectEditorScopeGatesTests), поэтому закрыта и редактору, и читателю.
const EDITOR_DOORS: readonly ContentDoor[] = [
  'file.save', 'file.create', 'file.mkdir', 'file.rename', 'file.upload',
  'file.delete', 'file.toMarkdown',
  'note.create', 'note.edit', 'note.delete', 'note.move', 'note.folder', 'note.comment',
];

// Можно ли записать контент через эту дверь: нет роли (обычный проект и старый бэк) и
// владелец — все двери; читатель — ни одной; редактор — только открытые ему.
export function canWriteContent(
  project: Pick<Project, 'myRole'> | null | undefined,
  door: ContentDoor,
): boolean {
  const role = project?.myRole;
  if (!role || role === 'owner') return true;
  if (role === 'reader') return false;
  return EDITOR_DOORS.includes(door);
}
