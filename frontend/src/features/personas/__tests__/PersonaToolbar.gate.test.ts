// W4-T4: гейт кнопки «Сохранить» в тулбаре студии (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md §3.4). Кнопки сохранения в самой
// PersonaForm нет никогда — она живёт здесь, в тулбаре родителя, а форма лишь сообщает
// статус через onStatus: canSave=false для тех, кому закрыто editCard (PersonaForm.tsx:111-114,516:
// readOnlyForm = !can('editCard'); в матрице editCard — только владелец, personaRights.ts:32-40),
// для владельца — true. Тулбар гейтится кнопку этим статусом (PersonaToolbar.tsx:100-102):
// !status.canSave → disabled. Вторая линия защиты — ранний выход save() (PersonaForm.tsx:595).
// Здесь — первая: в режиме правки кнопка есть у всех, но активна только там, где можно
// сохранить. Мутация «снять !status.canSave из saveDisabled» красит кейсы читателя и
// редактора (dirty=true — держит кнопку выключенной ровно canSave).
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец PersonaForm.gate.test.ts:
// effects не идут (ResizeObserver в useContainerWidth/PillSwitch — в эффектах, width=null →
// широкая раскладка; на состояние кнопки это не влияет). Мок breakpoints не нужен:
// тулбар не зовёт useIsMobile, мобильность — проп. mode='edit', editing=true: только
// тогда тулбар рисует «Сохранить» (в превью-режиме saveArea не рисуется ни у кого);
// onEdit/onDelete/onMakeDefault заданы — проверяется ровно гейт сохранения, а не
// гейт этих контролов (он — ProjectPersonasPanel.gate.test.ts).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaToolbar } from '../PersonaToolbar';
import { canPersona, personaRole } from '../personaRights';
import type { PersonaFormStatus } from '../PersonaForm';
import { C } from '../../../lib/design';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// Проектная персона проекта 'proj1' — та же фикстура, что в PersonaForm.gate.test.ts
const PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
} as Persona;

function projectWith(myRole: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарафон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Статус, который форма сообщает родителю для этой роли: тот же источник, что у
// canSave в форме (personaRole → canPersona('editCard'); фикстура валидна по имени и
// зоне, поэтому других условий canSave здесь нет). dirty=true — человек правил карточку:
// кнопка выключена ровно тем, что выключает роль, а не отсутствием правок.
function statusForRole(myRole: ProjectRoleView): PersonaFormStatus {
  return {
    canSave: canPersona(personaRole(PERSONA, [projectWith(myRole)]), 'editCard'),
    saving: false,
    dirty: true,
  };
}

// Тулбар в режиме правки профиля персоны проекта с ролью myRole
function renderToolbar(myRole: ProjectRoleView): string {
  return renderToStaticMarkup(createElement(PersonaToolbar, {
    mode: 'edit', persona: PERSONA, accent: C.accent, // фолбэк акцента — как у родителя (ProjectPersonasPanel.tsx:144)
    zoneLabel: `Проект · ${projectWith(myRole).name}`,
    view: 'preview', onView: () => {},
    editing: true, onEdit: () => {}, onCancelEdit: () => {},
    status: statusForRole(myRole),
    onDelete: () => {}, onMakeDefault: () => {},
    onSave: () => {},
  }));
}

// Открывающий <button …> кнопки с точным текстом-содержимым (образец buttonTagWithText
// из SessionList.test.ts): SSR кладёт единственный текстовый ребёнок сразу за '>'
function buttonTagWithText(html: string, text: string): string | null {
  const end = html.indexOf(`>${text}</button>`);
  if (end < 0) return null;
  const start = html.lastIndexOf('<button', end);
  if (start < 0) return null;
  return html.slice(start, end);
}

describe('PersonaToolbar: гейт «Сохранить» по роли (W4-T4)', () => {
  it('читатель: «Сохранить» выключена', () => {
    const html = renderToolbar('reader');
    const btn = buttonTagWithText(html, 'Сохранить');
    expect(btn, 'кнопки «Сохранить» в режиме правки нет').not.toBeNull();
    expect(btn!, '«Сохранить» читателю не выключена').toContain('disabled');
  });

  it('редактор: «Сохранить» выключена (editCard — только владелец)', () => {
    const html = renderToolbar('editor');
    const btn = buttonTagWithText(html, 'Сохранить');
    expect(btn, 'кнопки «Сохранить» в режиме правки нет').not.toBeNull();
    expect(btn!, '«Сохранить» редактору не выключена').toContain('disabled');
  });

  it('владелец: «Сохранить» активна', () => {
    const html = renderToolbar('owner');
    const btn = buttonTagWithText(html, 'Сохранить');
    expect(btn, 'кнопки «Сохранить» владельцу нет').not.toBeNull();
    expect(btn!, '«Сохранить» владельцу выключена').not.toContain('disabled');
  });
});
