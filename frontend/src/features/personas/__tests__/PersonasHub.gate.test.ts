// W3-F1d (доработка дефекта fe0f42e8): гейт по роли на ВИТРИНЕ хаба «Персоны». Карточка
// AssistantCard рисовала «Поговорить» без проверки роли: в режиме «Все» читатель общего
// проекта видел её на проектной персоне и при нажатии получал 403 от бэка. Роль — только
// через personaRole + canPersona('chat') (personaRights, матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11); закрытая кнопка прячется целиком —
// нет onTalk в пропсах карточки, нет и ряда под неё (тот же приём, что F1c/F1d).
//
// Окружение node (конфиг vitest), SSR renderToStaticMarkup — образец PersonasPage.gate.test.ts:
// effects не идут (лента активности остаётся незагруженной), но витрина рендерится по props,
// а наличие/отсутствие контрола ловится по его имени в HTML. Имя «Поговорить» — единственный
// носитель кнопки в хабе (подзаголовок секции — «поговорить» со строчной, не считается).
// Список проектов хаб берёт из общего кэша useAllProjects — подменяем мок модуля (образец
// features/tasks/__tests__/executorMembers.test.ts): роль ставится на самом проекте, как её
// отдаёт бэк (Project.myRole). useMe: стор не прогревался (loaded=false) → карточка-приглашение
// «знакомство» не рисуется и не мешает подсчёту.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// Список проектов для хаба (useAllProjects) — подменяемый: роль читается из myRole проекта
const m = vi.hoisted(() => ({ projects: [] as Project[] }));
vi.mock('../../projects/useAllProjects', () => ({ useAllProjects: () => m.projects }));

import { PersonasHub } from '../PersonasHub';

// Проектная персона проекта 'proj1' (видна по scope+projectId, без сети)
// createdAt обязателен: хаб подмешивает в ленту активности «создание персоны» из самого
// списка (personasActivity.ts:79) и сортирует ленту по нему — без него рендер падает.
const PROJECT_PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
  createdAt: '2026-01-01T00:00:00Z',
} as Persona;

// Своя персона участника: глобальная, без проекта → personaRole даёт null («как сейчас»)
const OWN_PERSONA: Persona = {
  id: 'own-1', ownerId: 'me1', name: 'Алиса', role: 'Ассистент', handle: 'alisa',
  scope: 'global', memoryEnabled: false,
  createdAt: '2026-01-02T00:00:00Z',
} as Persona;

// Проект с ролью участника (контракт соседей: роль только в Project.myRole)
function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарфон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Витрина хаба с этими персонами и этим списком проектов (мок отдаёт m.projects)
function renderHub(personas: Persona[], projects: Project[]): string {
  m.projects = projects;
  return renderToStaticMarkup(createElement(PersonasHub, {
    personas, talking: false,
    onTalk: () => {}, onOpenSession: () => {}, onNew: () => {}, onOpenPersonaView: () => {},
  }));
}

// Сколько раз имя встречается в HTML
function countOf(html: string, name: string): number {
  return html.split(name).length - 1;
}

const CHAT = 'Поговорить';

describe('PersonasHub (витрина «Твои помощники»): гейт «Поговорить» по роли (W3-F1d)', () => {
  it('читатель: на карточке проектной персоны нет «Поговорить»', () => {
    const html = renderHub([PROJECT_PERSONA], [projectWith('reader')]);
    expect(countOf(html, CHAT), 'чат с персоной читателю (RunTurn закрыт)').toBe(0);
  });

  it('редактор: «Поговорить» есть (RunTurn открыт)', () => {
    const html = renderHub([PROJECT_PERSONA], [projectWith('editor')]);
    expect(countOf(html, CHAT), 'чат с персоной редактору').toBe(1);
  });

  it('владелец: «Поговорить» есть', () => {
    const html = renderHub([PROJECT_PERSONA], [projectWith('owner')]);
    expect(countOf(html, CHAT), 'чат с персоной владельцу').toBe(1);
  });

  // Своя (глобальная) персона участника: projectId нет → роль null → «как сейчас».
  // Список при этом читательский — гейт не должен задеть личную карточку.
  it('своя (глобальная) персона: «Поговорить» есть даже при читательской роли в списке', () => {
    const html = renderHub([OWN_PERSONA], [projectWith('reader')]);
    expect(countOf(html, CHAT), 'чат со своей персоной').toBe(1);
  });

  // Проектная персона без myRole (обычный проект / старый бэк) — матрица §2 читает
  // отсутствие роли как «все»: поведение витрины не меняется
  it('проектная персона без myRole (обычный проект / старый бэк) — «Поговорить» есть', () => {
    const html = renderHub([PROJECT_PERSONA], [projectWith(undefined)]);
    expect(countOf(html, CHAT), 'чат с персоной без роли').toBe(1);
  });

  // Режим «Все» (дефект fe0f42e8): на одной витрине читательская карточка без кнопки,
  // своя — с кнопкой
  it('смешанный список: читательская проектная персона без кнопки, своя — с кнопкой', () => {
    const html = renderHub([PROJECT_PERSONA, OWN_PERSONA], [projectWith('reader')]);
    expect(countOf(html, CHAT), 'одна кнопка — только на своей персоне').toBe(1);
  });
});
