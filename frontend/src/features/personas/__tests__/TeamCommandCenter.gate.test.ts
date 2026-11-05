// W3-F1b: гейт по роли в командном центре проекта (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md §3.4). Фронт не обещает того,
// что бэк откажет участнику: читатель — без смены руководителя (makeLead), без ИИ-сборки
// команды (aiTeam) и без чатов с проектными персонами (chat); редактор — без смены
// руководителя, ИИ-сборка и чаты остаются; владелец и обычный проект (myRole нет) — всё.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец components/SessionList.test.ts:
// effects не идут (лента/память/задачи остаются незагруженными), но тулбар, герой и
// пустое состояние рендерятся по состоянию — наличие/отсутствие контролов ловится
// по их именам в HTML. Имена: «Сменить» — контрол руководителя (при defaultPersonaId),
// «Сформировать команду» — ИИ-сборка (в пустом состоянии — единственный открытый
// вход: пункт тулбар-меню в SSR закрыт, меню свёрнуто), «Созвать команду» — групповой
// чат с персонами.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { TeamCommandCenter } from '../TeamCommandCenter';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// usePersonas — модульный стор: подменяем его фикстурой команды (personaLabel и
// остальные экспорты сохраняем). teamRef — vi.hoisted: на него ссылается фабрика мока.
const teamRef = vi.hoisted(() => ({ current: [] as Persona[] }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, usePersonas: () => teamRef.current };
});

// Окружение node — sessionStorage нет; TeamCommandCenter читает cc_team_tab в
// инициализаторе состояния (дефолт 'overview' — то, что нужно тесту).
const sstore = new Map<string, string>();
(globalThis as any).sessionStorage = {
  getItem: (k: string) => sstore.get(k) ?? null,
  setItem: (k: string, v: string) => { sstore.set(k, String(v)); },
  removeItem: (k: string) => { sstore.delete(k); },
  clear: () => sstore.clear(),
  key: () => null,
  get length() { return sstore.size; },
} as Storage;

// Команда из двух проектных персон p1 (isProjectTeam: scope='project' + projectId).
const TEAM: Persona[] = [
  { id: 'lead-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer', scope: 'project', projectId: 'p1', memoryEnabled: false } as Persona,
  { id: 'mate-2', ownerId: 'o1', name: 'Марк', role: 'Разработчик', handle: 'dev', scope: 'project', projectId: 'p1', memoryEnabled: false } as Persona,
];

// Проект с ролью; defaultPersonaId='lead-1' — руководитель назначен, контрол называется
// «Сменить» (без него был бы «Назначить» — та же ветка makeLead).
function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'p1', name: 'Росмарфон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole, defaultPersonaId: 'lead-1',
  } as Project;
}

function renderCenter(project: Project): string {
  return renderToStaticMarkup(createElement(TeamCommandCenter, {
    project, onOpenPersona: () => {}, onNewPersona: () => {}, onOpenSession: () => {}, onOpenSessionById: () => {},
  }));
}

// Сколько раз имя встречается в HTML. Имена точные и заглавные — подсказка пустого
// состояния («попросите LLM сформировать команду») другой регистр не даёт ложных совпадений.
function countOf(html: string, name: string): number {
  return html.split(name).length - 1;
}

describe('TeamCommandCenter: гейт по роли (W3-F1b)', () => {
  it('читатель: нет «Сменить», нет «Созвать команду», нет ИИ-сборки (и в пустом состоянии)', () => {
    teamRef.current = TEAM;
    const html = renderCenter(projectWith('reader'));
    expect(countOf(html, 'Сменить'), 'контрол смены руководителя у читателя').toBe(0);
    expect(countOf(html, 'Созвать команду'), 'групповой чат у читателя').toBe(0);
    // Строка-информация о руководителе остаётся (она только для чтения) — это не makeLead
    expect(countOf(html, 'Руководитель проекта:')).toBe(1);
    teamRef.current = [];
    expect(countOf(renderCenter(projectWith('reader')), 'Сформировать команду'), 'ИИ-сборка у читателя').toBe(0);
  });

  it('редактор: ИИ-сборка есть, «Сменить» нет; чаты с персонами открыты', () => {
    teamRef.current = [];
    expect(countOf(renderCenter(projectWith('editor')), 'Сформировать команду'), 'ИИ-сборка редактору (B3 открыта)').toBe(1);
    teamRef.current = TEAM;
    const html = renderCenter(projectWith('editor'));
    expect(countOf(html, 'Сменить'), 'смена руководителя редактору (makeLead — Configure)').toBe(0);
    expect(countOf(html, 'Созвать команду'), 'групповой чат редактору (chat открыт)').toBe(1);
  });

  it('владелец: «Сменить», «Созвать команду» и ИИ-сборка — всё есть', () => {
    teamRef.current = TEAM;
    const html = renderCenter(projectWith('owner'));
    expect(countOf(html, 'Сменить'), 'смена руководителя владельцу').toBe(1);
    expect(countOf(html, 'Созвать команду'), 'групповой чат владельцу').toBe(1);
    teamRef.current = [];
    expect(countOf(renderCenter(projectWith('owner')), 'Сформировать команду'), 'ИИ-сборка владельцу').toBe(1);
  });

  // «нет роли» = обычный проект / старый бэк — матрица §2 читает её как «все»
  it('без myRole (обычный проект / старый бэк) — всё открыто, как раньше', () => {
    teamRef.current = TEAM;
    const html = renderCenter(projectWith(undefined));
    expect(countOf(html, 'Сменить'), 'смена руководителя без роли').toBe(1);
    expect(countOf(html, 'Созвать команду'), 'групповой чат без роли').toBe(1);
    teamRef.current = [];
    expect(countOf(renderCenter(projectWith(undefined)), 'Сформировать команду'), 'ИИ-сборка без роли').toBe(1);
  });
});
