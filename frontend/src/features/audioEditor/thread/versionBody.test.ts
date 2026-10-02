import { describe, expect, it } from 'vitest';
import type { AudioThreadVersion } from '../api';
import { shownExtraFiles } from './VersionBody';

const ver = (files: AudioThreadVersion['files']): AudioThreadVersion => ({
  id: 'v1', number: 1, jobId: 'job-v1', variant: 1, baseVersionId: null, files, license: null, createdAt: '',
});
const midi = { role: 'midi', path: 'w/anthem.mid' };
const score = { role: 'score', path: 'w/anthem.abc' };
const roles = (v: AudioThreadVersion, on: boolean) => shownExtraFiles(v, on).map(f => f.role);

describe('shownExtraFiles: строка .mid при просмотрщике нот', () => {
  it('без флага .mid остаётся в списке', () => {
    expect(roles(ver([midi, score]), false)).toEqual(['score', 'midi']);
  });
  it('с флагом у версии без главного звука .mid скрыт, остальные строки на месте', () => {
    expect(roles(ver([midi, score]), true)).toEqual(['score']);
  });
  it('с флагом у версии с главным звуком .mid остаётся', () => {
    expect(roles(ver([{ role: 'main', path: 'w/anthem.mp3' }, midi, score]), true)).toEqual(['score', 'midi']);
  });
});
