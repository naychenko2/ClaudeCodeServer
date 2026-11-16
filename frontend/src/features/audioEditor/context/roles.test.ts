import { describe, expect, it } from 'vitest';
import { audioRefOf } from './work';
import { audioRefRoles, personalFileCandidate, personalFileRoles } from './roles';
import { disabledKindReason, isKindDisabled } from '../../../lib/chatContext/disabledKind';

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
  // Ф-2-д-д: личный вид ссылки (Ф-9) бэкенд берёт только куском склейки — роль «образец голоса»
  // личному файлу не дана, запрос с ней дал бы 400 role_not_accepted
  it('личный файл — только кусок склейки, ровно одна роль', () => {
    expect(audioRefRoles('personal-file').map(r => r.role)).toEqual(['piece']);
  });
});

describe('личный файл при основном объекте', () => {
  it('основной звук — единственная роль «Кусок склейки»', () => {
    expect(personalFileRoles('audio').map(r => r.role)).toEqual(['piece']);
  });
  it('другой основной объект и его отсутствие — ролей нет (кнопки быть не должно)', () => {
    expect(personalFileRoles('image')).toEqual([]);
    expect(personalFileRoles('image-character')).toEqual([]);
    expect(personalFileRoles('project-file')).toEqual([]);
    expect(personalFileRoles(null)).toEqual([]);
    expect(personalFileRoles(undefined)).toEqual([]);
  });
  it('кандидат — вид «personal-file» и путь от корня «Личного»', () => {
    expect(personalFileCandidate('audio/песня.wav')).toEqual({ kind: 'personal-file', ref: { path: 'audio/песня.wav' } });
  });
  // Вид «personal-file» — бэковый, фронтового кита у него нет by design (как у «project-file»):
  // без этого прикреплённый личный файл гас в строке контекста с текстом «Раздел не загрузился»
  it('личный вид не считается неактивным разделом', () => {
    const registry = new Map();
    expect(isKindDisabled('personal-file', registry)).toBe(false);
    expect(disabledKindReason('personal-file', registry, false)).toBeNull();
  });
});

describe('ссылка основного объекта', () => {
  it('версия нити — с versionId, нить без версий — без него', () => {
    expect(audioRefOf('t1', 'v2')).toEqual({ kind: 'audio', ref: { threadId: 't1', versionId: 'v2' } });
    expect(audioRefOf('t1', null)).toEqual({ kind: 'audio', ref: { threadId: 't1' } });
  });
});
