// W3-F1g: гейт по роли во вкладке правил автоматики (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md: автоматика — Configure,
// правила меняет только владелец). Читатель и редактор видят список правил, но
// входов записи нет: ни «Добавить правило», ни ИИ-подбора/генерации, ни меню
// действий карточки (в нём только правка/тест/вкл-выкл/удаление); над списком —
// строка «Правила автоматики меняет только владелец проекта». Владелец и обычный
// проект (myRole нет) — всё как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец PersonaForm.gate.test.ts
// и components/SessionList.test.ts. В панели нет внешних сторов (только useState/useRef) —
// подмены не нужны. Начальный рендер — карточки свёрнуты, меню закрыты: тест проверяет
// входы, видимые в начальном рендере; выключенные поля раскрытого тела проверены
// кодом (<fieldset disabled> + disabled у полей шапки), а не этим тестом — раскрыть
// карточку кликом в SSR нельзя.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaAutomationPanel } from '../PersonaAutomationPanel';
import { C } from '../../../lib/design';
import type { Persona, PersonaAutomationRule, Project, ProjectRoleView } from '../../../types';

// Правило «таймер, ежедневно в 09:00 · сообщить» — маркерный набор для карточки
const RULE: PersonaAutomationRule = {
  id: 'r-1', enabled: true, name: 'Следить за релизами',
  trigger: { type: 'timer', args: { schedule: { type: 'daily', time: '09:00' } } },
  action: { weight: 'gate', instruction: 'Сообщай о новых коммитах', rememberInHistory: false, expiresAfterMinutes: 1440 },
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
};

// Проектная персона проекта 'proj1' с одним правилом автоматики
const PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
  automationRules: [RULE],
} as Persona;

function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарафон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Вкладка «Проактивность» этой персоны в проекте с ролью myRole
function renderPanel(myRole?: ProjectRoleView, rules: PersonaAutomationRule[] = [RULE]): string {
  return renderToStaticMarkup(createElement(PersonaAutomationPanel, {
    persona: { ...PERSONA, automationRules: rules },
    projects: [projectWith(myRole)],
    accent: C.accent,
  }));
}

function countOf(html: string, s: string): number { return html.split(s).length - 1; }

const HINT_TEXT = 'Правила автоматики меняет только владелец проекта';

describe('PersonaAutomationPanel: гейт по роли (W3-F1g)', () => {
  // Кнопки под списком и «⋯» карточки — единственные входы записи в начальном рендере;
  // «Проверить»/«Удалить» живут в закрытом меню и в footer раскрытой карточки — в
  // начальном рендере их нет ни у кого, отсутствие у читателя/редактора — часть гейта
  it('читатель: входов записи нет, строка-пояснение есть', () => {
    const html = renderPanel('reader');
    expect(countOf(html, 'Добавить правило'), '«Добавить правило» читателю').toBe(0);
    expect(countOf(html, 'Подобрать автоматически'), 'ИИ-подбор читателю').toBe(0);
    expect(countOf(html, 'Создать по промпту'), 'ИИ-генерация читателю').toBe(0);
    expect(countOf(html, 'aria-label="Действия"'), 'меню действий карточки читателю').toBe(0);
    expect(countOf(html, 'Проверить'), '«Тест» читателю').toBe(0);
    expect(countOf(html, 'Удалить'), 'удаление читателю').toBe(0);
    expect(html, 'строки-пояснения над списком нет').toContain(HINT_TEXT);
  });

  it('редактор: входов записи нет, строка-пояснение есть', () => {
    const html = renderPanel('editor');
    expect(countOf(html, 'Добавить правило'), '«Добавить правило» редактору').toBe(0);
    expect(countOf(html, 'Подобрать автоматически'), 'ИИ-подбор редактору').toBe(0);
    expect(countOf(html, 'Создать по промпту'), 'ИИ-генерация редактору').toBe(0);
    expect(countOf(html, 'aria-label="Действия"'), 'меню действий карточки редактору').toBe(0);
    expect(countOf(html, 'Проверить'), '«Тест» редактору').toBe(0);
    expect(countOf(html, 'Удалить'), 'удаление редактору').toBe(0);
    expect(html, 'строки-пояснения над списком нет').toContain(HINT_TEXT);
  });

  // Правил нет — пустое состояние: у не-владельца оно тоже без кнопок записи
  it('читатель без правил: пустое состояние без кнопок, строка есть', () => {
    const html = renderPanel('reader', []);
    expect(countOf(html, 'Создать правило'), '«Создать правило» читателю').toBe(0);
    expect(countOf(html, 'Подобрать автоматически'), 'ИИ-подбор читателю').toBe(0);
    expect(countOf(html, 'Создать по промпту'), 'ИИ-генерация читателю').toBe(0);
    expect(html, 'пустого состояния без кнопок нет').toContain('Правил автоматики нет');
    expect(html, 'строки-пояснения нет').toContain(HINT_TEXT);
  });

  // Доказательство, что тест вообще видит эти входы: у владельца они есть —
  // отсутствие у читателя/редактора значит именно гейт, а не «так не рисуется»
  it('владелец: кнопки и меню действий есть, пояснения нет', () => {
    const html = renderPanel('owner');
    expect(countOf(html, 'Добавить правило'), '«Добавить правило» владельцу').toBe(1);
    expect(countOf(html, 'Подобрать автоматически'), 'ИИ-подбор владельцу').toBe(1);
    expect(countOf(html, 'Создать по промпту'), 'ИИ-генерация владельцу').toBe(1);
    expect(countOf(html, 'aria-label="Действия"'), 'меню действий карточки владельцу').toBe(1);
    expect(html, 'пояснения в панели владельца быть не должно').not.toContain(HINT_TEXT);
  });

  // «нет роли» = обычный проект / старый бэк — матрица §2 читает её как «все»
  it('без myRole (обычный проект / старый бэк) — как владелец', () => {
    const html = renderPanel(undefined);
    expect(countOf(html, 'Добавить правило'), '«Добавить правило» без роли').toBe(1);
    expect(countOf(html, 'aria-label="Действия"'), 'меню действий карточки без роли').toBe(1);
    expect(html, 'пояснения без роли быть не должно').not.toContain(HINT_TEXT);
  });
});
