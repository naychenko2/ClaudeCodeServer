// Х-W4-79: детское пустое состояние центра. Окружение vitest — node, рендер статический
// (react-dom/server): меряем тексты и структуру, клик/создание урока покрыты у дока
// (ProjectRail.child.test.tsx — тот же childApi.createLesson).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ChildCenterEmpty } from './ChildCenterEmpty';
import type { ChildSubjectsDock } from '../projects/ProjectRail';
import chatPanelSrc from '../../components/ChatPanel.tsx?raw';
import pageSrc from '../../pages/WorkspacePage.tsx?raw';
import desktopSrc from '../../pages/workspace/DesktopWorkspace.tsx?raw';

const dock = (activeSubjectId: string | null): ChildSubjectsDock => ({
  subjects: [], activeSubjectId, onOpenSubject: () => {},
});

describe('ChildCenterEmpty', () => {
  it('детский текст и кнопка «Новый урок»; взрослых «С чего начнём?» / «Новый чат» нет', () => {
    const html = renderToStaticMarkup(createElement(ChildCenterEmpty, { dock: dock('math') }));
    expect(html).toContain('Выбери урок или начни новый');
    expect(html).toContain('data-testid="child-center-new-lesson"');
    expect(html).toContain('Новый урок');
    expect(html).not.toContain('С чего начнём');
    expect(html).not.toContain('Новый чат');
  });

  it('без активного предмета кнопка закрыта (создавать урок не в чём)', () => {
    const html = renderToStaticMarkup(createElement(ChildCenterEmpty, { dock: dock(null) }));
    expect(html).toMatch(/<button[^>]*data-testid="child-center-new-lesson"[^>]*disabled|<button[^>]*disabled[^>]*data-testid="child-center-new-lesson"/);
  });

  it('с активным предметом кнопка открыта', () => {
    const html = renderToStaticMarkup(createElement(ChildCenterEmpty, { dock: dock('math') }));
    expect(html).not.toMatch(/<button[^>]*data-testid="child-center-new-lesson"[^>]*disabled/);
  });
});

describe('Х-W4-79: взрослые empty state ребёнку не монтируются', () => {
  it('ChatPanel гасит ChatEmptyState (а с ним NewChatSetup) при childFeedKinds', () => {
    expect(chatPanelSrc).toMatch(/items\.length === 0 && !isHistoryLoading && online && !childFeedKinds/);
  });

  it('оба центра без сессии отдают ребёнку ChildCenterEmpty, а не «Новый чат»', () => {
    expect(pageSrc).toMatch(/NoSession = isChildScreen \? <ChildCenterEmpty/);
    expect(desktopSrc).toMatch(/p\.child \? \(\s*<div[^>]*>\s*<ChildCenterEmpty/);
  });
});
