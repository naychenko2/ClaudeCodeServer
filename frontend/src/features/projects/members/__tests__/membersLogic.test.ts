// Логика вкладки «Участники» (S1-F3): сводка заголовка и тексты отказов формы приглашения.
import { describe, expect, it } from 'vitest';
import { MEMBER_REFUSAL } from '../../../../types';
import type { ProjectMember } from '../../../../types';
import { inviteErrorText, membersSummary, peopleCount } from '../membersLogic';

// Отказ сервера: тело ошибки несёт код, статус есть (lib/offline)
const refusal = (code: string, status = 409) =>
  Object.assign(new Error('текст сервера'), { status, body: { error: 'текст сервера', code } });

describe('сводка в заголовке', () => {
  it('без участников — «только вы», нейтрально', () => {
    expect(membersSummary(0)).toEqual({ text: 'только вы', tone: 'neutral' });
  });
  it('склонение числа людей', () => {
    expect([1, 2, 4, 5, 11, 12, 21, 22].map(peopleCount)).toEqual([
      '1 человек', '2 человека', '4 человека', '5 человек', '11 человек', '12 человек', '21 человек', '22 человека',
    ]);
    expect(membersSummary(3)).toEqual({ text: '3 человека', tone: 'ok' });
  });
});

describe('ошибки формы приглашения', () => {
  const none: ProjectMember[] = [];
  it('user_not_found называет введённое имя', () => {
    expect(inviteErrorText(refusal(MEMBER_REFUSAL.UserNotFound, 404), ' maria ', none)).toContain('«maria»');
  });
  it('already_member подставляет роль из списка (регистр имени не важен)', () => {
    const list: ProjectMember[] = [{ userId: 'u1', username: 'Maria', role: 'reader' }];
    expect(inviteErrorText(refusal(MEMBER_REFUSAL.AlreadyMember), 'maria', list)).toContain('читатель');
  });
  it('self — «Это вы»', () => {
    expect(inviteErrorText(refusal(MEMBER_REFUSAL.Self, 400), 'me', none)).toContain('Это вы');
  });
  it('flag_disabled — просьба включить «Общие проекты» в «Экспериментах»', () => {
    const t = inviteErrorText(refusal(MEMBER_REFUSAL.FlagDisabled), 'maria', none);
    expect(t).toContain('Попросите maria');
    expect(t).toContain('«Экспериментах»');
  });
  it('local_project и shared_projects_disabled различаются', () => {
    const a = inviteErrorText(refusal(MEMBER_REFUSAL.LocalProject), 'x', none);
    const b = inviteErrorText(refusal(MEMBER_REFUSAL.SwitchDisabled, 503), 'x', none);
    expect(a).toContain('на вашем устройстве');
    expect(b).toContain('выключены на сервере');
  });
  it('сеть: ошибка без статуса — «Нет связи», ничего не потеряно', () => {
    const t = inviteErrorText(new TypeError('Failed to fetch'), 'x', none);
    expect(t).toContain('Нет связи');
    expect(t).toContain('Ничего не потеряно');
  });
});
