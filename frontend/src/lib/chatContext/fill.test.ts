import { beforeEach, describe, expect, it } from 'vitest';
import { refOf, rolesFor } from './fill';
import { registerSubsystem } from '../subsystems/registryCore';
import type { ChatContextDto, ChatContextPrimary, ChatContextRef, ContextKindApi, ContextKindCtx } from './types';

const ctx: ContextKindCtx = { projectId: 'p', sessionId: 's', isMobile: false };
const primary = (kind = 'image'): ChatContextPrimary =>
  ({ id: 'p1', kind, ref: { threadId: 't1' }, by: 'human', addedAt: '', label: 'hero.png', version: null, thumb: null, missing: false, role: null });
const ref = (id: string, kind: string, r: Record<string, unknown>, role: string | null = 'style'): ChatContextRef =>
  ({ id, kind, ref: r, by: 'human', addedAt: '', label: id, version: null, thumb: null, missing: false, role, usedBy: [] });
const state = (p: ChatContextPrimary | null, refs: ChatContextRef[] = []): ChatContextDto => ({ revision: 1, primary: p, refs });

const imageApi = {
  kinds: ['image'],
  refRoles: (_c, _p, kind) => (kind === 'project-file' ? [{ role: 'style', label: 'Стиль' }, { role: 'object', label: 'Объект' }] : []),
} as unknown as ContextKindApi;

const install = (api: ContextKindApi) => registerSubsystem({
  key: 'fill-test', title: 't', order: 1, noPill: true, core: true, slots: { 'context-kind': [{ name: 'image', action: api as never }] },
});
beforeEach(() => install(imageApi));

describe('refOf', () => {
  it('находит референс по виду и ссылке, роль не в счёте', () => {
    const s = state(primary(), [ref('r1', 'project-file', { path: 'a.png' }, 'object')]);
    expect(refOf(s, { kind: 'project-file', ref: { path: 'a.png' } })?.id).toBe('r1');
    expect(refOf(s, { kind: 'project-file', ref: { path: 'b.png' } })).toBeNull();
    expect(refOf(s, { kind: 'image', ref: { path: 'a.png' } })).toBeNull();
  });
});

describe('rolesFor', () => {
  it('роли даёт вид основного объекта', () => {
    expect(rolesFor(ctx, state(primary()), 'project-file').map(r => r.role)).toEqual(['style', 'object']);
  });
  it('нет основного объекта — ролей нет', () => {
    expect(rolesFor(ctx, state(null), 'project-file')).toEqual([]);
  });
  it('вид основного не знает кандидата или не объявил refRoles — ролей нет', () => {
    expect(rolesFor(ctx, state(primary()), 'image-character')).toEqual([]);
    install({ kinds: ['image'] } as unknown as ContextKindApi);
    expect(rolesFor(ctx, state(primary()), 'project-file')).toEqual([]);
  });
});
