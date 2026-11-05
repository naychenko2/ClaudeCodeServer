// W3-F1c: гейт по роли в панели персон проекта (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md §3.4). Фронт не обещает того,
// что бэк откажет участнику: читатель — без чата с персоной («Поговорить»), без правки
// карточки («Редактировать»), без ⋯-меню («Сделать персоной по умолчанию», «Удалить
// персону») и без «Новой персоны»; редактор — чат и правка карточки остаются,
// makeLead/delete (Configure) закрыты, создание открыто (createPersona); владелец и обычный
// проект (myRole нет) — всё, как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец TeamCommandCenter.gate.test.ts
// и components/SessionList.test.ts: effects не идут (чаты/привязки/активность остаются
// незагруженными), но тулбар и визитка рендерятся по состоянию — наличие/отсутствие
// контролов ловится по их именам в HTML.
// Имена: «Поговорить» — кнопка чата в визитке; title="Редактировать" — кнопка тулбара;
// title="Ещё" — триггер ⋯-меню, единственный носитель пунктов «Сделать персоной по
// умолчанию»/«Удалить персону»: сами пункты в SSR закрыты (menuOpen=false, как в F1b),
// но когда оба пункта закрыты ролью, скрыт и весь триггер — его отсутствие и есть
// отсутствие makeLead/delete. title="Новая персона" — кнопка сайдбара (создание — createPersona).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { ProjectPersonaPane, ProjectPersonasPanel } from '../ProjectPersonasPanel';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// usePersonas — модульный стор: подменяем его фикстурой (остальные экспорты сохраняются;
// personaTitleLines и visibleIn работают по-настоящему). personasRef — vi.hoisted: на него
// ссылается фабрика мока.
const personasRef = vi.hoisted(() => ({ current: [] as Persona[] }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, usePersonas: () => personasRef.current };
});

// Проектная персона проекта 'proj1' (visibleIn — по scope+projectId, без сети)
const PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
} as Persona;

// Проект с ролью; без defaultPersonaId — «эта персона не руководитель», пункт
// «Сделать персоной по умолчанию» для владельца открыт (makeLead).
function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарфон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Центральная зона: визитка выбранной персоны (view='preview', не редактирование)
function renderPane(project: Project): string {
  return renderToStaticMarkup(createElement(ProjectPersonaPane, {
    project, personaId: 'per-1', creating: false,
    onOpenChat: () => {}, onSelectPersona: () => {}, onCleared: () => {},
  }));
}

// Сайдбар «Команда»: список + кнопка создания
function renderSidebar(project: Project): string {
  return renderToStaticMarkup(createElement(ProjectPersonasPanel, {
    project, selectedId: null, onSelect: () => {}, onNew: () => {},
  }));
}

// Сколько раз имя встречается в HTML
function countOf(html: string, name: string): number {
  return html.split(name).length - 1;
}

const CHAT = 'Поговорить';
const EDIT = 'title="Редактировать"';
const LEAD_MENU = 'title="Ещё"';
const NEW_PERSONA = 'title="Новая персона"';

describe('ProjectPersonasPanel: гейт по роли (W3-F1c)', () => {
  it('читатель: нет «Поговорить», нет «Редактировать», нет ⋯-меню, нет «Новой персоны»', () => {
    personasRef.current = [PERSONA];
    const html = renderPane(projectWith('reader'));
    expect(countOf(html, CHAT), 'чат с персоной читателю (RunTurn закрыт)').toBe(0);
    expect(countOf(html, EDIT), 'правка карточки читателю').toBe(0);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead+delete закрыты) читателю').toBe(0);
    expect(countOf(renderSidebar(projectWith('reader')), NEW_PERSONA), 'создание персоны (createPersona) читателю').toBe(0);
  });

  it('редактор: «Поговорить» и «Редактировать» есть, создание есть; ⋯-меню закрыто', () => {
    personasRef.current = [PERSONA];
    const html = renderPane(projectWith('editor'));
    expect(countOf(html, CHAT), 'чат с персоной редактору (RunTurn открыт)').toBe(1);
    expect(countOf(html, EDIT), 'правка карточки редактору (editCard открыт)').toBe(1);
    // makeLead и delete — оба Configure: редактору закрыты, меню пустое → скрыт весь триггер
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead/delete — Configure) редактору').toBe(0);
    expect(countOf(renderSidebar(projectWith('editor')), NEW_PERSONA), 'создание персоны редактору (И-7: OwnerId=T)').toBe(1);
  });

  it('владелец: «Поговорить», «Редактировать», ⋯-меню и «Новая персона» — всё есть', () => {
    personasRef.current = [PERSONA];
    const html = renderPane(projectWith('owner'));
    expect(countOf(html, CHAT), 'чат с персоной владельцу').toBe(1);
    expect(countOf(html, EDIT), 'правка карточки владельцу').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead/delete) владельцу').toBe(1);
    expect(countOf(renderSidebar(projectWith('owner')), NEW_PERSONA), 'создание персоны владельцу').toBe(1);
  });

  // «нет роли» = обычный проект / старый бэк — матрица §2 читает её как «все»
  it('без myRole (обычный проект / старый бэк) — всё открыто, как раньше', () => {
    personasRef.current = [PERSONA];
    const html = renderPane(projectWith(undefined));
    expect(countOf(html, CHAT), 'чат с персоной без роли').toBe(1);
    expect(countOf(html, EDIT), 'правка карточки без роли').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню без роли').toBe(1);
    expect(countOf(renderSidebar(projectWith(undefined)), NEW_PERSONA), 'создание персоны без роли').toBe(1);
  });
});
