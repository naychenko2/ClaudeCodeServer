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

describe('PersonaForm: группа «Права» (F26)', () => {
  it('inheritsAccess=true: карточка с источником и ссылкой на роль, пометки «своё» нет', () => {
    const html = render({ inheritsAccess: true, access: 'readOnly' });
    expect(html).toContain('Группы, которые приходят от специальности');
    expect(html).toContain('Как у специальности');
    expect(html).toContain('data-testid="access-source"');
    expect(html).toContain('обновится вместе с ней');
    expect(html).toContain('href="#/specialties/frontend"');
    expect(html).toContain('Править нельзя, пока права наследуются');
    expect(html).not.toContain('>своё<');
  });

  it('inheritsAccess=false или поля нет (старый бэк): бейдж «своё», чекбоксы, без строки источника', () => {
    for (const p of [{ inheritsAccess: false }, {}]) {
      const html = render({ ...p, access: 'full' });
      expect(html).toContain('>своё<');
      expect(html).toContain('Веб-поиск');
      expect(html).not.toContain('data-testid="access-source"');
      expect(html).not.toContain('Править нельзя');
    }
  });

  it('«скопировано … · отличается в N местах» не рисуется: бэк не отдаёт accessCopiedAt/accessDiffCount', () => {
    expect(render({ inheritsAccess: false })).not.toContain('отличается в');
  });
});
