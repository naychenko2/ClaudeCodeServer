// A3b: группа прав формы персоны — «Как у специальности» / «Своё» (разрез §2 «Форма персоны»).
// F26a: строка «скопировано <дата> · отличается в N местах» — N равно числу ВИДИМЫХ меток
// отличия на этой карточке (профиль / ключ инструмента / строка запретов), а не бэковскому
// accessDiffCount (области 0..3): число обязано сходиться со списком «≠» на той же карточке.
// SSR renderToStaticMarkup (образец PersonaForm.gate.test.ts): effects не идут, каталог
// специальностей не загружен — эффективные значения берутся из полей персоны, которые сервер
// подставил при выдаче.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { PersonaAccessGroup } from '../PersonaAccessGroup';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona, PersonaAccess } from '../../../types';
import type { AccessDiff } from '../personaInheritModel';

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

// Ожидаемая дата считается НЕ форматтером компонента (тот же формат, что lib/postTime.ts:
// «13 нояб.», год — только если он не текущий), чтобы мутация формата краснила тест.
function expectedDate(iso: string): string {
  const d = new Date(iso);
  const opts: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short' };
  if (d.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
  return d.toLocaleDateString('ru-RU', opts);
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

  it('копия есть: форма передаёт дату бэка в карточку; число бэка в подписи не участвует (F26a)', () => {
    const html = render({ inheritsAccess: false, accessCopiedAt: COPIED_ISO, accessDiffCount: 2 });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).toContain('скопировано');
    expect(html).toContain(expectedDate(COPIED_ISO));
    // В SSR каталог специальностей не загружен → у формы нет diff → на карточке нет меток.
    // «отличается» не выходит, хотя бэк говорит «2»: бэковское число в подпись больше не доезжает.
    expect(html).not.toContain('отличается в');
  });

  it('«скопировано … · отличается в N местах» не рисуется: бэк не отдаёт accessCopiedAt/accessDiffCount', () => {
    expect(render({ inheritsAccess: false })).not.toContain('отличается в');
  });
});

// ── F26a: строка «скопировано …» в самой карточке ─────────────────────────────────────
// Карточка рендерится напрямую: число в подписи обязано равняться числу меток «≠»,
// нарисованных на этой же карточке. Бэковое число карточке не передаётся вовсе —
// поле accessDiffCount остаётся только в types.ts (бэк его шлёт, подпись его не читает).

const NO_DIFF: AccessDiff = { access: false, tools: [], extraDisallowed: [], missingDisallowed: [], count: 0 };

// Сценарий прежнего теста F26a: ключ web + два лишних запрета. Фронтовый diff.count = 3
// (каждый запрет отдельно), бэк посчитал бы 2 области (набор инструментов + набор запретов),
// а карточка рисует РОВНО 2 метки: «≠» у ключа web и одну строку лишних запретов.
const DIFF_KEY_PLUS_LINE: AccessDiff = { access: false, tools: ['web'], extraDisallowed: ['Bash', 'Edit'], missingDisallowed: [], count: 3 };

function renderGroup(extra: {
  copiedAt?: string | null;
  inherited?: boolean;
  access?: PersonaAccess;
  tools?: string[];
  disallowed?: string[];
  diff?: AccessDiff;
} = {}): string {
  return renderToStaticMarkup(createElement(PersonaAccessGroup, {
    inherited: extra.inherited ?? false,
    onModeChange: () => {},
    specialtyKey: 'qa',
    specialtyTitle: 'QA-тестер',
    access: extra.access ?? 'custom',
    tools: extra.tools ?? ['tasks', 'notes'],
    disallowed: extra.disallowed ?? ['Bash', 'Edit'],
    onAccess: () => {},
    onTools: () => {},
    onDisallowed: () => {},
    diff: extra.diff ?? NO_DIFF,
    copiedAt: extra.copiedAt,
  }));
}

describe('PersonaAccessGroup: «отличается в N местах» = число видимых меток (F26a)', () => {
  it('ключ + строка лишних запретов: две метки — «в 2 местах», не по каждому запрету', () => {
    const html = renderGroup({ copiedAt: COPIED_ISO, diff: DIFF_KEY_PLUS_LINE });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).toContain('скопировано');
    expect(html).toContain(expectedDate(COPIED_ISO));
    expect(html).toContain('data-testid="access-diff-web"');
    expect(html).toContain('data-testid="access-diff-extra"');
    expect(html).toContain('отличается в 2 местах');
    // diff.count = 3 (каждый запрет отдельно): строка считается ОДНОЙ меткой, её числа в подписи нет
    expect(html).not.toContain('3 местах');
    expect(html).not.toContain(COPIED_ISO);
  });

  it('два отличающихся ключа инструментов — «в 2 местах» (бэк считает это одной областью)', () => {
    const html = renderGroup({
      copiedAt: COPIED_ISO,
      diff: { access: false, tools: ['notes', 'web'], extraDisallowed: [], missingDisallowed: [], count: 2 },
    });
    expect(html).toContain('data-testid="access-diff-notes"');
    expect(html).toContain('data-testid="access-diff-web"');
    expect(html).toContain('отличается в 2 местах');
  });

  it('метка запретов скрыта (чужой профиль, свой список пуст) — число не завышено', () => {
    // Бэк в этом сценарии — 2 области (профиль + запреты), diff.count = 2; но блок
    // «Запрещённые» не рендерится (профиль не «Свой», свой список пуст) → его строка
    // «≠ у специальности запрещено» до экрана не доезжает и в счёт не идёт.
    const html = renderGroup({
      copiedAt: COPIED_ISO,
      access: 'readOnly',
      disallowed: [],
      diff: { access: true, tools: [], extraDisallowed: [], missingDisallowed: ['Bash'], count: 2 },
    });
    expect(html).not.toContain('Запрещённые инструменты');
    expect(html).not.toContain('у специальности запрещено');
    expect(html).toContain('≠ у специальности другой профиль');
    expect(html).toContain('отличается в 1 месте');
  });

  it('одна метка — «в 1 месте»', () => {
    const html = renderGroup({
      copiedAt: COPIED_ISO,
      diff: { access: false, tools: ['web'], extraDisallowed: [], missingDisallowed: [], count: 1 },
    });
    expect(html).toContain('data-testid="access-diff-web"');
    expect(html).toContain('отличается в 1 месте');
  });

  it('копия совпадает с шаблоном (меток нет): дата есть, «отличается» — нет', () => {
    const html = renderGroup({ copiedAt: COPIED_ISO });
    expect(html).toContain('data-testid="access-copied"');
    expect(html).not.toContain('отличается в');
  });

  it('копии нет (accessCopiedAt пустой/нет): строки нет', () => {
    for (const copiedAt of [null, undefined, '']) {
      expect(renderGroup({ copiedAt, diff: DIFF_KEY_PLUS_LINE })).not.toContain('access-copied');
    }
  });

  it('дата невалидная: строки нет', () => {
    expect(renderGroup({ copiedAt: 'not-a-date', diff: DIFF_KEY_PLUS_LINE })).not.toContain('access-copied');
  });

  it('наследование: строки копии нет, даже если старые поля остались в ответе', () => {
    const html = renderGroup({ inherited: true, copiedAt: COPIED_ISO, diff: DIFF_KEY_PLUS_LINE });
    expect(html).not.toContain('access-copied');
    expect(html).toContain('data-testid="access-source"');
  });
});
