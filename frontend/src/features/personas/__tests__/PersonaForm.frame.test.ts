// F29: каркас формы персоны по макету persona-inheritance.html (A3b).
// P3 — для участника одна плашка Notice вместо hint у каждого поля зоны и денег;
// P4/P5 — подсказки «Специальности» по состоянию (со specialностью — ссылка на неё,
// без — «Наследовать не от кого») и вариант «Без специальности»; P27 — подпись-источник
// моделей при «Без специальности»; P24 — футер «Отменить / Сохранить»: «Сохранить»
// вызывает ту же save(), что тулбар через ref (тот же PUT), «Отменить» — тот же
// обработчик, что кнопка тулбара (parent передаёт один и тот же).
// SSR-часть — renderToStaticMarkup (образец PersonaForm.inherit.test.ts); клик футера —
// фейк-DOM (образец PersonaForm.save.test.ts).

import { act, createElement, createRef } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm, type PersonaFormHandle } from '../PersonaForm';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona, Project } from '../../../types';

vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});

// bumpPersonas — заглушка: иначе после сохранения идёт реальный reloadPersonas
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, bumpPersonas: vi.fn() };
});

const { api } = await import('../../../lib/api');
const { installFakeDom, mount, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } =
  await import('../../../components/chat/__tests__/fakeDom');

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend',
  access: 'full', tools: ['tasks', 'notes', 'web'], disallowedTools: null,
} as Persona;

// Участник проекта-редактор: editZoneModel закрыт (ownerOnlyFields=true), карточка открыта
const MEMBER_PROJECTS = [{ id: 'p1', name: 'AI Home', myRole: 'editor' }] as unknown as Project[];
const MEMBER = { ...BASE, scope: 'project', projectId: 'p1' } as Persona;

function render(p: Partial<Persona>): string {
  return renderToStaticMarkup(createElement(PersonaForm, {
    persona: { ...BASE, ...p } as Persona, projects: [], onSaved: () => {}, onDelete: () => {},
  }));
}

beforeEach(() => { setAllFlags({}); });

describe('PersonaForm: Notice участника вместо полевых hint (F29, P3)', () => {
  it('участник: одна плашка «Права, модели и специальность меняет владелец проекта», прежних hint нет', () => {
    const html = renderToStaticMarkup(createElement(PersonaForm, {
      persona: MEMBER, projects: MEMBER_PROJECTS, onSaved: () => {}, onDelete: () => {},
    }));
    // ровно одна плашка на весь рендер
    expect((html.match(/Права, модели и специальность меняет владелец проекта/g) ?? []).length).toBe(1);
    expect(html).not.toContain('Меняет только владелец проекта');
  });

  it('владелец (обычная персона): плашки участника нет', () => {
    expect(render({})).not.toContain('Права, модели и специальность меняет владелец проекта');
  });
});

describe('PersonaForm: поле «Специальность» (F29, P4/P5)', () => {
  it('со специальностью: подсказка «От неё приходят…» и ссылка «Открыть специальность … →»', () => {
    const html = render({});
    expect(html).toContain('От неё приходят права, модели и типовые умения');
    expect(html).toContain('Открыть специальность');
    expect(html).toContain('href="#/specialties/frontend"');
    expect(html).toContain('(правит админ)');
  });

  it('«Без специальности»: опция селекта и подсказка «Наследовать не от кого…», ссылки нет', () => {
    const html = render({ specialty: 'none', inheritsAccess: true });
    expect(html).toContain('>Без специальности<');
    expect(html).not.toContain('>Не задана<');
    expect(html).toContain('Наследовать не от кого: права — полный доступ, модели — «Модели по умолчанию», типовых умений нет');
    expect(html).not.toContain('href="#/specialties/none"');
  });

  it('«Без специальности»: подписи-источники прав и моделей', () => {
    const html = render({ specialty: 'none', inheritsAccess: true });
    expect(html).toContain('↳ по умолчанию: полный доступ без ограничений');
    expect(html).toContain('data-testid="models-source"');
    expect(html).toContain('↳ «Модели по умолчанию»');
  });

  it('со специальностью: подписи-источника «Модели по умолчанию» нет', () => {
    expect(render({ inheritsAccess: true })).not.toContain('data-testid="models-source"');
  });
});

describe('PersonaForm: футер «Отменить / Сохранить» (F29, P24)', () => {
  async function mountForm(onCancelEdit: () => void) {
    installFakeDom();
    const g = globalThis as { window?: Record<string, unknown>; localStorage?: unknown; sessionStorage?: unknown };
    if (g.window) {
      g.window.history = { state: null, pushState: () => {}, replaceState: () => {} };
      g.window.addEventListener ??= () => {};
      g.window.removeEventListener ??= () => {};
    }
    g.localStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    g.sessionStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    vi.spyOn(api.personas, 'avatarCaps').mockResolvedValue({ generate: false } as never);
    vi.spyOn(api.personas, 'memory').mockResolvedValue([]);
    vi.spyOn(api.personas, 'focus').mockResolvedValue(null as never);
    const ref = createRef<PersonaFormHandle>();
    const { container } = mount(createElement(PersonaForm, {
      ref, persona: BASE, projects: [], onSaved: () => {}, onCancelEdit,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    return { container, ref };
  }

  function footerButton(container: ReturnType<typeof mount>['container'], label: string) {
    const footer = findNodeByAttr(container, 'data-testid', 'persona-form-footer');
    expect(footer, 'футера формы нет').toBeTruthy();
    const btn = [...footer!.children].find(n => n.nodeName === 'BUTTON' && textNodes(n).join('') === label);
    expect(btn, `кнопки «${label}» в футере нет`).toBeTruthy();
    return btn!;
  }

  it('футер есть в правке с родительским cancel; у формы создания — нет', async () => {
    const { container } = await mountForm(() => {});
    expect(findNodeByAttr(container, 'data-testid', 'persona-form-footer')).not.toBeNull();
    const htmlCreate = renderToStaticMarkup(createElement(PersonaForm, { persona: null, projects: [], onSaved: () => {} }));
    expect(htmlCreate).not.toContain('data-testid="persona-form-footer"');
  });

  it('«Сохранить» футера — та же save(), что у тулбара: ровно один PUT тем же телом', async () => {
    const update = vi.spyOn(api.personas, 'update').mockResolvedValue(BASE);
    const { container } = await mountForm(() => {});
    const btn = footerButton(container, 'Сохранить');
    await act(async () => { (reactProps(btn) as { onClick: () => void }).onClick(); });
    expect(update).toHaveBeenCalledTimes(1);
    const body = update.mock.calls[0][1] as Record<string, unknown>;
    expect(body.name).toBe('Светлана');
    expect(body.specialty).toBe('frontend');
  });

  it('мобила (useIsMobile=true на весь файл): кнопки футера ≥40 px и переносятся', () => {
    const html = renderToStaticMarkup(createElement(PersonaForm, {
      persona: BASE, projects: [], onSaved: () => {}, onCancelEdit: () => {},
    }));
    const at = html.indexOf('data-testid="persona-form-footer"');
    expect(at, 'футера нет').toBeGreaterThan(-1);
    const foot = html.slice(at, html.indexOf('</div>', at));
    // перенос кнопок на узком экране
    expect(foot).toContain('flex-wrap:wrap');
    const btns = foot.match(/<button[^>]*>/g) ?? [];
    expect(btns.length).toBe(2);
    // на мобиле обе кнопки размера md (minHeight 40, ui/Button.tsx)
    for (const b of btns) expect(b, 'кнопка футера ниже 40 px').toContain('min-height:40px');
  });

  it('«Отменить» футера вызывает тот же обработчик, что кнопка тулбара', async () => {
    const onCancelEdit = vi.fn();
    const { container } = await mountForm(onCancelEdit);
    const btn = footerButton(container, 'Отменить');
    await act(() => { (reactProps(btn) as { onClick: () => void }).onClick(); });
    expect(onCancelEdit).toHaveBeenCalledTimes(1);
  });

  afterAll(uninstallFakeDom);
});
