// F27: диалоги перехода группы прав. Компоненты — чистая обёртка над ui-китом, поэтому
// вызываем их как функции и читаем props возвращённого элемента (без DOM).
import type { ReactElement } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ReturnInheritDialog, SwitchSpecialtyInheritDialog, SwitchSpecialtyOwnDialog } from '../PersonaInheritDialogs';
import { accessDiff, describeAccessDiff, describeAccessGroup, type AccessGroup } from '../personaInheritModel';

type P = Record<string, any>;
const props = (e: unknown) => (e as ReactElement<P>).props;

describe('describeAccessDiff / describeAccessGroup', () => {
  const tpl: AccessGroup = { access: 'full', tools: ['tasks', 'notes', 'web'], disallowedTools: [] };
  const own: AccessGroup = { access: 'custom', tools: ['tasks', 'notes'], disallowedTools: ['Bash'] };

  it('перечисляет отличия фразами: профиль, выключенная возможность, запрет', () => {
    expect(describeAccessDiff(accessDiff(own, tpl), own))
      .toEqual(['профиль доступа: свой', 'выключен веб-поиск', 'запрещён Bash']);
  });
  it('снятый запрет специальности — «снят запрет»', () => {
    const t: AccessGroup = { access: 'custom', disallowedTools: ['Edit'] };
    const o: AccessGroup = { access: 'custom', disallowedTools: [] };
    expect(describeAccessDiff(accessDiff(o, t), o)).toEqual(['снят запрет Edit']);
  });
  it('права шаблона: запреты и выключенные возможности', () => {
    expect(describeAccessGroup({ access: 'custom', tools: ['tasks'], disallowedTools: ['Edit', 'Write'] }))
      .toBe('профиль «свой», выключено: заметки, веб-поиск, запрещены Edit и Write');
    expect(describeAccessGroup(null)).toBe('полные права');
  });
});

describe('диалоги перехода', () => {
  it('возврат: заголовок, перечень отличий, «Оставить своё / Вернуть»', () => {
    const onKeep = vi.fn(); const onReturn = vi.fn();
    const p = props(ReturnInheritDialog({ specialtyTitle: 'QA-тестер', diffs: ['выключен веб-поиск', 'запрещён Bash'], onKeep, onReturn }));
    expect(p.title).toBe('Вернуть права как у специальности?');
    expect(p.subtitle).toContain('«QA-тестер» в 2 местах: выключен веб-поиск, запрещён Bash');
    expect(p.confirmLabel).toBe('Вернуть');
    expect(p.cancelLabel).toBe('Оставить своё');
    p.onConfirm(); p.onCancel();
    expect(onReturn).toHaveBeenCalledTimes(1);
    expect(onKeep).toHaveBeenCalledTimes(1);
  });

  it('смена при наследовании: какими станут права и что будет с умениями', () => {
    const p = props(SwitchSpecialtyInheritDialog({ specialtyTitle: 'Код-ревьюер', rights: 'запрещены Edit и Write', onApply: vi.fn(), onCancel: vi.fn() }));
    expect(p.title).toBe('Специальность — «Код-ревьюер»');
    expect(p.subtitle).toContain('станут как у «Код-ревьюер»: запрещены Edit и Write');
    expect(p.subtitle).toContain('Умения специальности заменятся, ваши отключения и свои умения останутся.');
    expect(p.confirmLabel).toBe('Сменить специальность');
    expect(p.cancelLabel).toBe('Отмена');
  });

  it('смена при своих правах: три исхода — отмена, оставить свои, перейти на новые', () => {
    const onKeep = vi.fn(); const onTemplate = vi.fn(); const onCancel = vi.fn();
    const modal = SwitchSpecialtyOwnDialog({ specialtyTitle: 'Код-ревьюер', rights: 'x', onKeep, onTemplate, onCancel });
    expect(props(modal).subtitle).toContain('Оставить свои или перейти на новые?');
    const buttons = props(props(modal).footer).children as ReactElement<P>[];
    expect(buttons.map(b => b.props.children)).toEqual(['Отмена', 'Оставить свои', 'Перейти на новые']);
    buttons.forEach(b => b.props.onClick());
    expect([onCancel, onKeep, onTemplate].map(f => f.mock.calls.length)).toEqual([1, 1, 1]);
    props(modal).onClose();
    expect(onCancel).toHaveBeenCalledTimes(2);
  });
});
