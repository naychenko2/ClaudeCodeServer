import { describe, expect, it } from 'vitest';
import { audioRefOf } from './work';
import { audioRefRoles } from './roles';

describe('роли референсов основного звука', () => {
  it('голос из библиотеки — единственная роль (вопроса нет)', () => {
    expect(audioRefRoles('audio-voice').map(r => r.role)).toEqual(['voice']);
  });
  it('звук ленты и файл проекта — образец голоса или кусок склейки', () => {
    for (const kind of ['audio', 'project-file']) expect(audioRefRoles(kind).map(r => r.role)).toEqual(['reference', 'piece']);
  });
  it('картинка и персонаж звук не берёт', () => {
    expect(audioRefRoles('image')).toEqual([]);
    expect(audioRefRoles('image-character')).toEqual([]);
  });
});

describe('ссылка основного объекта', () => {
  it('версия нити — с versionId, нить без версий — без него', () => {
    expect(audioRefOf('t1', 'v2')).toEqual({ kind: 'audio', ref: { threadId: 't1', versionId: 'v2' } });
    expect(audioRefOf('t1', null)).toEqual({ kind: 'audio', ref: { threadId: 't1' } });
  });
});
