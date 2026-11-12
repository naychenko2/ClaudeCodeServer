// A3b: группа прав формы персоны — «Как у специальности» / «Своё» (разрез §2 «Форма персоны»).
// SSR renderToStaticMarkup (образец PersonaForm.gate.test.ts): effects не идут, каталог
// специальностей не загружен — эффективные значения берутся из полей персоны, которые сервер
// подставил при выдаче.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona } from '../../../types';

vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend',
} as Persona;

function render(p: Partial<Persona>): string {
  return renderToStaticMarkup(createElement(PersonaForm, {
    persona: { ...BASE, ...p } as Persona, projects: [], onSaved: () => {}, onDelete: () => {},
  }));
}

beforeEach(() => { setAllFlags({}); });

describe('PersonaForm: наследование прав (A3b)', () => {
  it('inheritsAccess=true: переключатель, приглушённая сводка и ссылка на роль; поля «Профиль доступа» нет', () => {
    const html = render({ inheritsAccess: true, access: 'readOnly' });
    expect(html).toContain('Как у специальности');
    expect(html).toContain('data-testid="access-inherited"');
    expect(html).toContain('Только чтение');
    expect(html).toContain('href="#/specialties/frontend"');
    expect(html).not.toContain('Профиль доступа');
  });

  it('inheritsAccess=false или поля нет (старый бэк): «Своё» — прежний контрол профиля', () => {
    for (const p of [{ inheritsAccess: false }, {}]) {
      const html = render({ ...p, access: 'full' });
      expect(html).toContain('Профиль доступа');
      expect(html).not.toContain('data-testid="access-inherited"');
    }
  });
});
