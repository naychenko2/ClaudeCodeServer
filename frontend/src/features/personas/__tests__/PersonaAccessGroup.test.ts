// F26: карточка группы «Права» — состояния через SSR (renderToStaticMarkup).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaAccessGroup, type PersonaAccessGroupProps } from '../PersonaAccessGroup';
import { accessDiff } from '../personaInheritModel';

const noop = () => {};
const base: PersonaAccessGroupProps = {
  inherited: false, onModeChange: noop, specialtyKey: 'qa', specialtyTitle: 'QA-тестер',
  access: 'custom', tools: ['tasks', 'notes'], disallowed: ['Bash'],
  onAccess: noop, onTools: noop, onDisallowed: noop, diff: null,
};
const render = (p: Partial<PersonaAccessGroupProps>) =>
  renderToStaticMarkup(createElement(PersonaAccessGroup, { ...base, ...p }));
const disabledCount = (html: string) => (html.match(/disabled=""/g) ?? []).length;

describe('PersonaAccessGroup', () => {
  it('«Своё»: три чекбокса, веб-поиск выключен у персоны и включён у специальности → «≠» только у него', () => {
    const diff = accessDiff(
      { access: 'custom', tools: ['tasks', 'notes'], disallowedTools: ['Bash'] },
      { access: 'custom', tools: ['tasks', 'notes', 'web'], disallowedTools: [] });
    const html = render({ diff });
    expect((html.match(/role="checkbox"/g) ?? []).length).toBe(3);
    expect(html).toContain('data-testid="access-diff-web"');
    expect(html).not.toContain('data-testid="access-diff-tasks"');
    expect(html).toContain('data-testid="access-diff-extra"');
    expect(html).toContain('+ Инструмент');
    expect(html).not.toContain('обновится вместе с ней');
  });

  it('«Как у специальности»: контролы отключены, видна строка источника, «+ Инструмент» и «≠» нет', () => {
    const html = render({ inherited: true });
    expect(html).toContain('обновится вместе с ней');
    expect(html).not.toContain('+ Инструмент');
    expect(html).not.toContain('data-testid="access-diff-');
    expect(html).toContain('disabled=""');
    expect(html).not.toContain('>своё<');
  });

  it('«Своё» редактируется: без readOnly ни один контрол не отключён', () => {
    expect(disabledCount(render({}))).toBe(0);
  });

  it('участник проекта (readOnly): переключатель, профиль и чекбоксы отключены, чипы без ×, подсказка владельца', () => {
    const html = render({ readOnly: true, readOnlyHint: 'Права меняет владелец проекта' });
    expect(disabledCount(html)).toBeGreaterThanOrEqual(8);
    expect(html).not.toContain('Убрать запрет');
    expect(html).not.toContain('+ Инструмент');
    expect(html).toContain('Права меняет владелец проекта');
  });
});
