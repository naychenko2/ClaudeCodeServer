// «Капсула идущих ходов» (S2a-F2): пустой список — ничего не рисуется; чужой
// приватный чат — строка без ссылки и без названия чата; обычный ход — аватар,
// имя и название чата со ссылкой. Рендер статикой (react-dom/server), как в
// PendingMessageView.test: у компонента нет хуков и состояния.
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { RunningTurnsPill, type RunningTurn } from '../RunningTurnsPill';

const turn = (over: Partial<RunningTurn> = {}): RunningTurn => ({
  chatId: 'c1', chatTitle: 'Фото витрины', userId: 'u-maria', displayName: 'Мария',
  isPrivateOfOther: false, ...over,
});

const render = (turns: RunningTurn[], projectId?: string) =>
  renderToStaticMarkup(createElement(RunningTurnsPill, { turns, projectId }));

describe('капсула идущих ходов', () => {
  it('0 ходов — капсулы нет', () => {
    expect(render([])).toBe('');
  });

  it('чужой приватный чат: строка без ссылки и без названия чата', () => {
    const html = render([turn({ isPrivateOfOther: true })]);
    expect(html).toContain('Мария работает в приватном чате');
    // Приватное остаётся приватным: названия чата в строке нет…
    expect(html).not.toContain('Фото витрины');
    // …и перехода тоже: ни якоря, ни href
    expect(html).not.toContain('<a ');
    expect(html).not.toContain('href=');
  });

  it('обычный ход: имя и название чата со ссылкой на чат', () => {
    const html = render([turn()]);
    expect(html).toContain('Мария');
    expect(html).toContain('Фото витрины');
    expect(html).toContain('href="#/chats/c1"');
  });

  it('с контекстом проекта ссылка проект-скоупа', () => {
    const html = render([turn()], 'p-1');
    expect(html).toContain('href="#/project/p-1/chat/c1"');
  });

  it('приватный ход не гасит обычные строки', () => {
    const html = render([
      turn({ chatId: 'c1', isPrivateOfOther: true }),
      turn({ chatId: 'c2', chatTitle: 'Меню на зиму', userId: 'u-andrey', displayName: 'Андрей' }),
    ]);
    expect(html).toContain('Мария работает в приватном чате');
    expect(html).toContain('Меню на зиму');
    expect(html).toContain('href="#/chats/c2"');
    // приватная строка — без своей ссылки: единственный якорь относится к обычному ходу
    expect(html.match(/<a /g)?.length).toBe(1);
  });
});
