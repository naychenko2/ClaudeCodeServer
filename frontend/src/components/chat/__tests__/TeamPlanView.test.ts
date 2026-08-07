// Карточка плана «Командной реализации»: блок «Замысел» и ссылка на файл полного плана
// (решение владельца 2026-08-02, docs/architecture/team-implement-mode.md).
// Рендерим статикой через react-dom/server — как соседние TeamEscalationView.test/ToolUseView.test.
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem, TeamPlan } from '../../../types';
import { TeamPlanView } from '../TeamPlanView';
import { ChatOpenFileContext } from '../contexts';

type PlanItem = Extract<ChatItem, { kind: 'team_plan' }>;

function plan(over: Partial<TeamPlan> = {}): TeamPlan {
  return {
    id: 'plan1', request: 'экспорт трат', summary: 'Экспорт трат в XLSX',
    createdAt: '2026-08-02T10:00:00Z', waveCount: 1, executorCount: 1,
    subtasks: [{
      id: 'st1', title: 'Эндпоинт экспорта', goal: '', executorPersonaId: 'p1',
      executorRationale: 'Бэкенд — его зона', files: ['Controllers/SpendController.cs'],
      wave: 1, doneCriteria: '',
    }],
    version: 1, assumptions: [], changes: [],
    ...over,
  };
}

function card(over: Partial<TeamPlan> = {}, extra: Partial<PlanItem> = {}): PlanItem {
  return { kind: 'team_plan', planId: 'plan1', plan: plan(over), resolved: false, approved: null, ...extra };
}

const render = (item: PlanItem, onOpenFile: ((path: string) => void) | null = () => {}) =>
  renderToStaticMarkup(
    createElement(ChatOpenFileContext.Provider, { value: onOpenFile },
      createElement(TeamPlanView, { item, online: true })));

describe('TeamPlanView — блок «Замысел» и ссылка на полный план', () => {
  it('без intent и planFilePath — ни блока, ни ссылки нет', () => {
    const html = render(card());
    expect(html).not.toContain('Замысел');
    expect(html).not.toContain('Полный план');
  });

  it('intent непустой — блок «Замысел» с текстом рендерится над списком под-задач', () => {
    const html = render(card({ intent: 'Идём через SafeJoin, авторизацию не трогаем.' }));
    expect(html).toContain('Замысел');
    expect(html).toContain('Идём через SafeJoin, авторизацию не трогаем.');
  });

  it('planFilePath задан — строка-ссылка показывает хвост пути (полный путь — в title)', () => {
    const html = render(card({ planFilePath: 'docs/plans/team/abc123/plan-v1.md' }));
    expect(html).toContain('Полный план');
    expect(html).toContain('>plan-v1.md<');
    expect(html).toContain('title="Открыть docs/plans/team/abc123/plan-v1.md"');
  });

  it('planFilePath: null — строки нет, карточка не падает', () => {
    const html = render(card({ planFilePath: null }));
    expect(html).not.toContain('Полный план');
  });

  it('без обработчика открытия файла (onOpenFile отсутствует) — ссылка не рендерится', () => {
    const html = render(card({ planFilePath: 'docs/plans/team/abc123/plan-v1.md' }), null);
    expect(html).not.toContain('Полный план');
  });

  it('свёрнутая (resolved) карточка запущенного плана — ссылка всё равно работает', () => {
    const html = render(card(
      { planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: true },
    ));
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  // Прод-баг (Вера, 2026-08-03): «Замысел» не показывался в развёрнутой карточке ПОСЛЕ
  // старта волны (resolved && approved) — прошлый тест этой ветки проверял только ссылку
  // на файл, без intent, и дефект прошёл мимо. Обе вещи должны рендериться вместе.
  it('запущенный план (resolved && approved) с intent — «Замысел» рендерится рядом со ссылкой на файл', () => {
    const html = render(card(
      { intent: 'Идём через SafeJoin, авторизацию не трогаем.', planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: true },
    ));
    expect(html).toContain('Замысел');
    expect(html).toContain('Идём через SafeJoin, авторизацию не трогаем.');
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  it('отменённый план — ссылка тоже рендерится', () => {
    const html = render(card(
      { planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: false },
    ));
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  // Правка плана человеком («Изменить план») гасит старую карточку не как отменённую,
  // а как заменённую версией vN — иначе выглядит будто план отменили, хотя правка принята
  it('карточка с supersededBy показывает «заменена версией vN», а не «план отменён»', () => {
    const html = render(card({}, { resolved: true, approved: false, supersededBy: 2 }));
    expect(html).toContain('заменена');
    expect(html).toContain('v2');
    expect(html).not.toContain('План отменён');
  });

  it('resolved/approved=false без supersededBy — по-прежнему «план отменён»', () => {
    const html = render(card({}, { resolved: true, approved: false }));
    expect(html).toContain('План отменён');
  });
});
