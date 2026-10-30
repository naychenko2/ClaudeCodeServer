// Подпись автора над чужим пузырём и право «Не доставлять» в очереди (S2a-F1).
import { describe, expect, it } from 'vitest';
import type { ProjectMembersView } from '../../../../types';
import { canCancelPending, resolveMessageAuthor } from '../messageAuthor';

const view: ProjectMembersView = {
  owner: { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' },
  members: [{ userId: 'u-maria', role: 'editor', username: 'maria', displayName: 'Мария' }],
  myRole: 'editor',
};
const shared = { myRole: 'editor' as const, formerMembers: [{ userId: 'u-old', displayName: 'Олег' }] };

describe('resolveMessageAuthor', () => {
  it('чужое сообщение участника — имя из списка', () => {
    expect(resolveMessageAuthor(shared, 'u-maria', 'u-me', view)).toEqual({ userId: 'u-maria', name: 'Мария', gone: false });
  });
  it('чужое сообщение владельца — имя владельца', () => {
    expect(resolveMessageAuthor(shared, 'u-owner', 'u-me', view)?.name).toBe('Андрей');
  });
  it('свой пузырь — без подписи', () => {
    expect(resolveMessageAuthor(shared, 'u-me', 'u-me', view)).toBeNull();
  });
  it('личный проект и сообщение без senderUserId — без подписи', () => {
    expect(resolveMessageAuthor({ myRole: undefined }, 'u-maria', 'u-me', view)).toBeNull();
    expect(resolveMessageAuthor(shared, undefined, 'u-me', view)).toBeNull();
  });
  it('бывший участник — серый аватар и пометка', () => {
    expect(resolveMessageAuthor(shared, 'u-old', 'u-me', view)).toEqual({ userId: 'u-old', name: 'Олег', gone: true });
  });
  it('список ещё не загружен — запасное имя, не «ушёл»', () => {
    expect(resolveMessageAuthor(shared, 'u-maria', 'u-me', null)).toEqual({ userId: 'u-maria', name: 'Участник', gone: false });
  });
});

describe('canCancelPending', () => {
  it('владелец и личный проект отменяют любое', () => {
    expect(canCancelPending({ myRole: 'owner' }, 'u-maria', 'u-me')).toBe(true);
    expect(canCancelPending({ myRole: undefined }, undefined, 'u-me')).toBe(true);
  });
  it('участник — только своё', () => {
    expect(canCancelPending({ myRole: 'editor' }, 'u-me', 'u-me')).toBe(true);
    expect(canCancelPending({ myRole: 'editor' }, 'u-maria', 'u-me')).toBe(false);
  });
  it('участник и неизвестный отправитель — нельзя', () => {
    expect(canCancelPending({ myRole: 'reader' }, undefined, 'u-me')).toBe(false);
  });
});
