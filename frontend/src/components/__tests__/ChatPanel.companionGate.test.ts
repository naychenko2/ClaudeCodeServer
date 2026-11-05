// W3-F1i: смена собеседника в чужом чате общего проекта выключена (правило W3-B4b,
// матрица §3.2 разреза docs/research/shared-projects-personas-cut-2026-11.md).
// Участник меняет собеседника только в своём чате (автор — session.createdByUserId);
// решение — только canChangeCompanion (personaRights, W3-F1a). Владелец, обычный проект
// (myRole нет) и чат вне проекта — как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец PersonaForm.gate.test.ts:
// effects не идут, закрытый селектор не раскрывается. ChatPanel целиком в SSR не рендерится
// (SignalR/сторы), поэтому рендерим сам селектор собеседника с пропом `locked`, который
// ChatPanel вычисляет. Чтобы тест краснел на правке ГЕЙТА в ChatPanel (c2), а не только
// селектора, выражение гейта берётся из исходника ChatPanel — образец guard'а по исходнику
// в chat/__tests__/RingPillBadge.test.ts.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { CompanionSelector, COMPANION_LOCKED_HINT } from '../CompanionSelector';
import { canChangeCompanion } from '../../features/personas/personaRights';
import type { Persona, Project, ProjectRoleView, Session } from '../../types';

const ME_ID = 'u-me';
const OWNER_ID = 'u-owner';

// Проектная персона 'proj1' (scope project — проходит фильтр isZonedPersona):
// список непустой, иначе селектор не рисуется вовсе и гейт нечего проверяло бы
const PERSONA = {
  id: 'per-1', ownerId: OWNER_ID, name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1',
} as Persona;

const CHAT_PANEL_SRC = readFileSync(fileURLToPath(new URL('../ChatPanel.tsx', import.meta.url)), 'utf8');

// Строка гейта из ChatPanel: `const companionLocked = !canChangeCompanion(project, session, …)`.
// Вычисляем её тем же выражением (eval из исходника): правка гейта в ChatPanel краснит
// этот тест (c2), а не только guard'ы ниже. Нет строки/незнакомые идентификаторы в ней —
// тест красный с понятным сообщением: контракт «решение — только canChangeCompanion,
// me — из useMe» сломан.
function companionLockedFromChatPanel(project: Project | null, session: Session, me: { userId: string | null }): boolean {
  const m = CHAT_PANEL_SRC.match(/const companionLocked = (.+);/);
  expect(m, 'ChatPanel: строка «const companionLocked = …» не найдена').not.toBeNull();
  return new Function('canChangeCompanion', 'project', 'session', 'me', `return (${m![1]});`)(
    canChangeCompanion, project, session, me,
  ) as boolean;
}

function projectWith(myRole?: ProjectRoleView): Project | null {
  // myRole нет = обычный проект / чат вне проекта: ChatPanel тогда получает project null
  if (myRole === undefined) return null;
  return { id: 'proj1', name: 'Росмарафон', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', myRole } as Project;
}

// Рендер селектора ровно с тем `locked`, который вычислит ChatPanel для этих данных
function renderCompanion(myRole: ProjectRoleView | undefined, authorId: string): string {
  const project = projectWith(myRole);
  const session = { id: 's1', createdByUserId: authorId } as Session;
  const locked = companionLockedFromChatPanel(project, session, { userId: ME_ID });
  return renderToStaticMarkup(createElement(CompanionSelector, {
    personas: [PERSONA], agents: [], selectedPersona: null, selectedAgentName: null,
    onSelect: () => {}, sharedProject: !!project, locked,
  }));
}

// Открывающий <button …> кнопки, несущей текст подсказки (образец buttonTagWithText
// из PersonaForm.gate.test.ts): состояние контрола читается по атрибутам тега
function buttonTagWithHint(html: string): string | null {
  const at = html.indexOf(`>${COMPANION_LOCKED_HINT}<`);
  if (at < 0) return null;
  const start = html.lastIndexOf('<button', at);
  if (start < 0) return null;
  const end = html.indexOf('>', start);
  return end < 0 ? null : html.slice(start, end + 1);
}

describe('ChatPanel: смена собеседника по роли (W3-F1i)', () => {
  it('редактор в чате владельца: селектор выключен, подсказка видима', () => {
    const html = renderCompanion('editor', OWNER_ID);
    const tag = buttonTagWithHint(html);
    expect(tag, 'подсказки «Собеседника меняет…» в разметке нет').not.toBeNull();
    expect(tag!, 'селектор редактору в чужом чате не выключен').toContain('disabled');
  });

  it('редактор в своём чате: без disabled, подсказки нет', () => {
    const html = renderCompanion('editor', ME_ID);
    expect(html, 'подсказки быть не должно').not.toContain(COMPANION_LOCKED_HINT);
    expect(html, 'свой чат редактору выключен').not.toContain('disabled');
  });

  it('читатель и в своём чате: выключен, подсказка видима', () => {
    const html = renderCompanion('reader', ME_ID);
    const tag = buttonTagWithHint(html);
    expect(tag, 'подсказки читателю нет').not.toBeNull();
    expect(tag!, 'селектор читателю не выключен').toContain('disabled');
  });

  it('владелец: как раньше — без disabled и подсказки', () => {
    const html = renderCompanion('owner', OWNER_ID);
    expect(html, 'подсказки владельцу быть не должно').not.toContain(COMPANION_LOCKED_HINT);
    expect(html, 'селектор владельцу выключен').not.toContain('disabled');
  });

  // «нет роли» = обычный проект / старый бэк / чат вне проекта — матрица читает её как «все»
  it('обычный проект и чат вне проекта (project null): как раньше', () => {
    const html = renderCompanion(undefined, ME_ID);
    expect(html, 'подсказки в обычном проекте быть не должно').not.toContain(COMPANION_LOCKED_HINT);
    expect(html, 'селектор в обычном проекте выключен').not.toContain('disabled');
  });
});

// Интеграция в ChatPanel (тот же класс guard'а, что RingPillBadge.test.ts): запрос при
// блокировке не уходит, блок доходит до селектора
describe('ChatPanel: гейт дошит до запроса и до селектора', () => {
  it('handleCompanionChange начинается с раннего выхода по companionLocked', () => {
    const at = CHAT_PANEL_SRC.indexOf('const handleCompanionChange = useCallback(');
    expect(at, 'handleCompanionChange не найден').toBeGreaterThanOrEqual(0);
    expect(
      CHAT_PANEL_SRC.slice(at, at + 600),
      'раннего выхода «if (companionLocked) return;» в начале handleCompanionChange нет',
    ).toContain('if (companionLocked) return;');
  });

  it('блок передаётся в Composer (companionLocked={companionLocked})', () => {
    expect(CHAT_PANEL_SRC, 'селектор о блоке не знает').toContain('companionLocked={companionLocked}');
  });
});
