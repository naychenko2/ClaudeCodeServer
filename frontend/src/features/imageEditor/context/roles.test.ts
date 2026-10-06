import { describe, expect, it } from 'vitest';
import { imageRefOf } from './work';
import { imageRefRoles } from './roles';
import { roleLabel } from '../../../lib/chatContext/roleLabels';

describe('роли референсов основной картинки', () => {
  it('картинка и файл проекта — образцом: стиль, объект, лицо', () => {
    for (const kind of ['image', 'project-file']) {
      expect(imageRefRoles(kind).map(r => r.role)).toEqual(['style', 'object', 'face']);
    }
  });
  it('пункт → роль → бейдж: лицо для картинки и файла, персонаж только у «Персонажей»', () => {
    const rows = (k: string) => imageRefRoles(k).map(r => [r.label, r.role, roleLabel(r.role)]);
    for (const kind of ['image', 'project-file']) {
      expect(rows(kind)).toContainEqual(['Как лицо', 'face', 'лицо']);
      expect(imageRefRoles(kind).some(r => r.role === 'character')).toBe(false);
    }
    expect(rows('image-character')).toEqual([['Как персонажа', 'character', 'персонаж']]);
  });
  it('персонаж — единственная роль (вопроса нет), остальные виды картинка не берёт', () => {
    expect(imageRefRoles('image-character').map(r => r.role)).toEqual(['character']);
    expect(imageRefRoles('audio')).toEqual([]);
    expect(imageRefRoles('audio-voice')).toEqual([]);
  });
});

describe('ссылка основного объекта', () => {
  it('версия нити — с versionId, нить без версий — без него', () => {
    expect(imageRefOf('t1', 'v2')).toEqual({ kind: 'image', ref: { threadId: 't1', versionId: 'v2' } });
    expect(imageRefOf('t1', null)).toEqual({ kind: 'image', ref: { threadId: 't1' } });
  });
});
