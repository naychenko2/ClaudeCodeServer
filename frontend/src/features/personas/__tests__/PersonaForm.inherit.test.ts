// A3b: группа прав формы персоны — «Как у специальности» / «Своё» (разрез §2 «Форма персоны»).
// F26a: строка «скопировано <дата> · отличается в N местах» — число из бэка (accessDiffCount),
// не из фронтового accessDiff.
// SSR renderToStaticMarkup (образец PersonaForm.gate.test.ts): effects не идут, каталог
// специальностей не загружен — эффективные значения берутся из полей персоны, которые сервер
// подставил при выдаче.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { PersonaAccessGroup } from '../PersonaAccessGroup';
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

// Дата «копии прав» для тестов F26a
const COPIED_ISO = '2026-11-13T10:00:00Z';

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

  it('копия есть: форма передаёт поля бэка в карточку — строка с датой и «2 местах» (F26a)', () => {
    const html = render({ inheritsAccess: false, accessCopiedAt: COPIED_ISO, accessDiffCount: 2 });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).toContain('отличается в 2 местах');
  });

  it('«скопировано … · отличается в N местах» не рисуется: бэк не отдаёт accessCopiedAt/accessDiffCount', () => {
    expect(render({ inheritsAccess: false })).not.toContain('отличается в');
  });
});

// ── F26a: строка «скопировано …» в самой карточке ─────────────────────────────────────
// Карточка рендерится напрямую: здесь нужен и фронтовый diff (пометки «≠»), и бэковский
// diffCount — их числа РАЗНЫЕ по построению (фронт считает по местам, бэк — областями,
// PersonaManager.AccessDiff): в сценарии ниже фронт насчитал 3 (ключ web + два запрета),
// бэк — 2 (набор инструментов + набор запретов). Подпись обязана нести бэковское число.

const DIFF3 = { access: false, tools: ['web'], extraDisallowed: ['Bash', 'Edit'], missingDisallowed: [], count: 3 };

// Ожидаемая дата считается НЕ форматтером компонента (тот же формат, что lib/postTime.ts:
// «13 нояб.», год — только если он не текущий), чтобы мутация формата краснила тест.
function expectedDate(iso: string): string {
  const d = new Date(iso);
  const opts: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short' };
  if (d.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
  return d.toLocaleDateString('ru-RU', opts);
}

function renderGroup(extra: { copiedAt?: string | null; diffCount?: number | null; inherited?: boolean } = {}): string {
  return renderToStaticMarkup(createElement(PersonaAccessGroup, {
    inherited: extra.inherited ?? false,
    onModeChange: () => {},
    specialtyKey: 'qa',
    specialtyTitle: 'QA-тестер',
    access: 'custom',
    tools: ['tasks', 'notes'],
    disallowed: ['Bash', 'Edit'],
    onAccess: () => {},
    onTools: () => {},
    onDisallowed: () => {},
    diff: DIFF3,
    copiedAt: extra.copiedAt,
    diffCount: extra.diffCount,
  }));
}

describe('PersonaAccessGroup: «скопировано <дата> · отличается в N местах» (F26a)', () => {
  it('копия есть: строка с датой (не ISO) и числом из бэка, не из фронтового diff', () => {
    const html = renderGroup({ copiedAt: COPIED_ISO, diffCount: 2 });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).toContain('скопировано');
    expect(html).toContain(expectedDate(COPIED_ISO));
    expect(html).toContain('отличается в 2 местах');
    // 3 — фронтовое число (diff.count): в подписи его быть не должно
    expect(html).not.toContain('3 местах');
    expect(html).not.toContain(COPIED_ISO);
  });

  it('одно отличие — «в 1 месте»', () => {
    expect(renderGroup({ copiedAt: COPIED_ISO, diffCount: 1 })).toContain('отличается в 1 месте');
  });

  it('копия совпадает с шаблоном (0): дата есть, «отличается» — нет', () => {
    const html = renderGroup({ copiedAt: COPIED_ISO, diffCount: 0 });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).not.toContain('отличается в');
  });

  it('копии нет (accessCopiedAt пустой/нет): строки нет', () => {
    for (const copiedAt of [null, undefined, '']) {
      expect(renderGroup({ copiedAt, diffCount: 2 })).not.toContain('access-copied');
    }
  });

  it('дата невалидная: строки нет', () => {
    expect(renderGroup({ copiedAt: 'not-a-date', diffCount: 2 })).not.toContain('access-copied');
  });

  it('наследование: строки копии нет, даже если старые поля остались в ответе', () => {
    const html = renderGroup({ inherited: true, copiedAt: COPIED_ISO, diffCount: 2 });
    expect(html).not.toContain('access-copied');
    expect(html).toContain('data-testid="access-source"');
  });
});
