// Сторож связки «ушёл открытый чат → сосед» в WorkspacePage. Открытая шторка чатов
// лежит в истории своей записью поверх записи чата: прямой navReplace затёр бы её
// флаг, и запись ушедшего чата осталась бы под соседом. Поэтому и выбор соседа, и
// перезапись истории обязаны идти ПОСЛЕ снятия записи шторки — внутри
// afterChatSwitcherClosed. Страница слишком велика для компонентного теста без DOM,
// поэтому связку проверяем по исходнику: тело leaveActiveChat и проводка в SessionList.

import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const src = readFileSync(join(process.cwd(), 'src', 'pages', 'WorkspacePage.tsx'), 'utf8');

// Тело стрелочной функции leaveActiveChat — до закрывающей скобки на её отступе
function leaveActiveChatBody(): string {
  const start = src.indexOf('const leaveActiveChat = ');
  expect(start, 'в WorkspacePage нет leaveActiveChat').toBeGreaterThan(-1);
  const end = src.indexOf('\n  };', start);
  expect(end).toBeGreaterThan(start);
  return src.slice(start, end);
}

describe('WorkspacePage: удаление и архив открытого чата', () => {
  it('удаление активного чата из SessionList уходит в leaveActiveChat', () => {
    expect(src).toContain('onActiveDeleted={leaveActiveChat}');
  });

  it('архив активного чата тоже уходит в leaveActiveChat', () => {
    expect(src).toMatch(/leaveActiveChat\(neighbor \?\? null\)/);
  });

  it('leaveActiveChat переписывает историю только после снятия записи шторки', () => {
    const body = leaveActiveChatBody();
    const gate = body.indexOf('afterChatSwitcherClosed(');
    expect(gate, 'leaveActiveChat не ждёт снятия записи шторки (afterChatSwitcherClosed)').toBeGreaterThan(-1);
    const before = body.slice(0, gate);
    for (const call of ['navReplace(', 'navPush(', 'handleSelectSession(', 'handleClearSession(']) {
      expect(before.includes(call), `${call} в leaveActiveChat идёт до afterChatSwitcherClosed`).toBe(false);
    }
    // Обе ветки (сосед и пустое состояние) перезаписывают запись внутри колбэка
    expect(body.slice(gate).match(/navReplace\(/g)?.length).toBe(2);
  });
});
