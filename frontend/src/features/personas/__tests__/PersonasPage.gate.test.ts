// W3-F1d: гейт по роли в студии персоны на странице «Персоны» (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md §3.4). После W3-B2 участник видит
// здесь проектные персоны общих проектов; фронт не обещает того, что бэк откажет:
// читатель — без чата с персоной («Поговорить»), без ⋯-меню («Сделать персоной по
// умолчанию», «Удалить персону»); редактор — чат остаётся, makeLead/delete (Configure)
// закрыты; владелец, своя персона и обычный проект (роли нет) — всё, как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец TeamCommandCenter.gate.test.ts
// и components/SessionList.test.ts: effects не идут (чаты/привязки/активность остаются
// незагруженными), но тулбар и визитка рендерятся по состоянию — наличие/отсутствие
// контролов ловится по их именам в HTML. Рендерим PersonaStudio (карточку персоны из
// PersonasPage): сама страница целиком в статичном рендере тянет хаб, рельсу панелей
// и навигацию — они вне проверяемого. Файл .test.ts: конфиг vitest включает только
// src/**/*.test.ts (соседние gate-тесты — такие же).
// Имена: «Поговорить» — кнопка чата в визитке (единственный её носитель в студии:
// тулбар её не рисует); title="Ещё" — триггер ⋯-меню, единственный носитель пунктов
// «Сделать персоной по умолчанию»/«Удалить персону»: сами пункты в SSR закрыты
// (menuOpen=false, как в F1b/F1c), но когда оба закрыты ролью, скрыт и весь триггер —
// его отсутствие и есть отсутствие makeLead/delete.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaStudio } from '../PersonasPage';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// Проектная персона проекта 'proj1' (видна по scope+projectId, без сети)
const PROJECT_PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
} as Persona;

// Своя персона участника: глобальная, без проекта → personaRole даёт null («как сейчас»)
const OWN_PERSONA: Persona = {
  id: 'own-1', ownerId: 'me1', name: 'Алиса', role: 'Ассистент', handle: 'alisa',
  scope: 'global', memoryEnabled: false,
} as Persona;

// Проект с ролью; без defaultPersonaId — «эта персона не руководитель»,
// пункт «Сделать персоной по умолчанию» там, где makeLead открыт, доступен.
function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарфон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Студия выбранной персоны: вид «Профиль» (визитка), не редактирование, десктоп
function renderStudio(persona: Persona, projects: Project[]): string {
  return renderToStaticMarkup(createElement(PersonaStudio, {
    persona, projects, talking: false,
    onDelete: () => {}, onTalk: () => {}, onOpenSession: () => {}, onOpenSpecialties: () => {},
    onBack: () => {}, isMobile: false,
  }));
}

// Сколько раз имя встречается в HTML
function countOf(html: string, name: string): number {
  return html.split(name).length - 1;
}

const CHAT = 'Поговорить';
const LEAD_MENU = 'title="Ещё"';
// Вкладка «Память» (хвост ревью 7efb03bd): карточка ручного добавления в MemoryPanel
// рисуется только с onAdd — его PersonaMemoryPanel даёт по роли (memoryWrite).
const ADD_CARD = 'Добавить вручную';
const ADD_BTN = 'Запомнить';

describe('PersonasPage (студия персоны): гейт по роли (W3-F1d)', () => {
  it('читатель: нет «Поговорить», нет ⋯-меню (makeLead/delete)', () => {
    const html = renderStudio(PROJECT_PERSONA, [projectWith('reader')]);
    expect(countOf(html, CHAT), 'чат с персоной читателю (RunTurn закрыт)').toBe(0);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead+delete — Configure) читателю').toBe(0);
  });

  it('редактор: «Поговорить» есть; ⋯-меню закрыто (makeLead/delete — Configure)', () => {
    const html = renderStudio(PROJECT_PERSONA, [projectWith('editor')]);
    expect(countOf(html, CHAT), 'чат с персоной редактору (RunTurn открыт)').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead/delete) редактору').toBe(0);
  });

  it('владелец: «Поговорить» и ⋯-меню (makeLead/delete) — всё есть', () => {
    const html = renderStudio(PROJECT_PERSONA, [projectWith('owner')]);
    expect(countOf(html, CHAT), 'чат с персоной владельцу').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead/delete) владельцу').toBe(1);
  });

  // Своя персона участника (role === null): глобальная — «как сейчас», все три действия
  it('своя (глобальная) персона: «Поговорить» и ⋯-меню — всё есть', () => {
    const html = renderStudio(OWN_PERSONA, [projectWith('reader')]);
    expect(countOf(html, CHAT), 'чат со своей персоной').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню (makeLead/delete) со своей персоной').toBe(1);
  });

  // Проектная персона без myRole (обычный проект / старый бэк) — матрица §2 читает
  // отсутствие роли как «все»: поведение не меняется
  it('проектная персона без myRole (обычный проект / старый бэк) — всё открыто, как раньше', () => {
    const html = renderStudio(PROJECT_PERSONA, [projectWith(undefined)]);
    expect(countOf(html, CHAT), 'чат с персоной без роли').toBe(1);
    expect(countOf(html, LEAD_MENU), '⋯-меню без роли').toBe(1);
  });
});

// Хвост ревью 7efb03bd (W3-F1h): студия обязана пробрасывать вычисленную роль в
// PersonaMemoryPanel — иначе тот считает роль null («как раньше, всё открыто») и читатель
// видит в памяти все кнопки записи. Проверяется вкладка «Память» (initialView='memory'):
// effects в SSR не идут, память не грузится, но карточка ручного добавления рисуется
// независимо от загрузки — её отсутствие в HTML и есть закрытый memoryWrite.
describe('PersonasPage (студия персоны): вкладка «Память» по роли (W3-F1h)', () => {
  function renderMemory(persona: Persona, projects: Project[]): string {
    return renderToStaticMarkup(createElement(PersonaStudio, {
      persona, projects, talking: false, initialView: 'memory',
      onDelete: () => {}, onTalk: () => {}, onOpenSession: () => {}, onOpenSpecialties: () => {},
      onBack: () => {}, isMobile: false,
    }));
  }

  it('читатель: нет карточки добавления и «Запомнить» (memoryWrite закрыт)', () => {
    const html = renderMemory(PROJECT_PERSONA, [projectWith('reader')]);
    expect(countOf(html, ADD_CARD), 'карточка ручного добавления читателю').toBe(0);
    expect(countOf(html, ADD_BTN), '«Запомнить» читателю').toBe(0);
  });

  it('редактор: карточка добавления на месте (memoryWrite открыт)', () => {
    const html = renderMemory(PROJECT_PERSONA, [projectWith('editor')]);
    expect(countOf(html, ADD_CARD), 'карточка ручного добавления редактору').toBe(1);
    expect(countOf(html, ADD_BTN), '«Запомнить» редактору').toBe(1);
  });

  it('владелец и без myRole: карточка добавления на месте, как раньше', () => {
    expect(countOf(renderMemory(PROJECT_PERSONA, [projectWith('owner')]), ADD_CARD), 'карточка добавления владельцу').toBe(1);
    expect(countOf(renderMemory(PROJECT_PERSONA, [projectWith(undefined)]), ADD_CARD), 'карточка добавления без роли').toBe(1);
  });
});
