import { describe, expect, it } from 'vitest';
import type { AudioThreadVersion } from '../api';
import { shownExtraFiles } from './VersionBody';

const ver = (files: AudioThreadVersion['files']): AudioThreadVersion => ({
  id: 'v1', number: 1, jobId: 'job-v1', variant: 1, baseVersionId: null, files, license: null, createdAt: '',
});
const midi = { role: 'midi', path: 'w/anthem.mid' };
const score = { role: 'score', path: 'w/anthem.abc' };
const roles = (v: AudioThreadVersion) => shownExtraFiles(v).map(f => f.role);

describe('shownExtraFiles: строка .mid при просмотрщике нот', () => {
  it('у версии без главного звука .mid скрыт, остальные строки на месте', () => {
    expect(roles(ver([midi, score]))).toEqual(['score']);
  });
  it('у версии с главным звуком .mid остаётся', () => {
    expect(roles(ver([{ role: 'main', path: 'w/anthem.mp3' }, midi, score]))).toEqual(['score', 'midi']);
  });
});
