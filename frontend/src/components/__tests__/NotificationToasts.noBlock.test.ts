// Тост-слой не перехватывает клики интерфейса (дефект: закреплённое уведомление о новой
// версии claude CLI лежало поверх кнопок шапки раздела — нажатие до них не доходило,
// браузерные сценарии падали по таймауту).
//
// Окружение node (без jsdom), как в SaveConflictDialog.test.ts: SSR-рендер настоящего
// ToastStack; аватар подменён тонкой обёрткой (он тянет стор персон). Проверка — по
// pointer-events каждого слоя: сам слой и карточка прозрачны для курсора (клик проходит
// сквозь них к интерфейсу под тостом), кликабельны только контролы карточки — ✕,
// «Открыть» и кнопка действия.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { ToastStack, type ToastItem } from '../NotificationToasts';

vi.mock('../../features/notifications/NotificationAvatar', () => ({
  NotificationAvatar: ({ kind }: { kind: string }) => createElement('i', { 'data-avatar': kind }),
  hasPersona: () => false,
  notifPersonaLabel: () => '',
}));

const render = (items: ToastItem[]) => renderToStaticMarkup(createElement(ToastStack, {
  items, sheetRaised: false, onOpen: () => {}, onDismiss: () => {}, onAction: () => {},
}));

// То самое уведомление сторожа claude CLI: закреплённое, с адресом раздела
const cliUpdate: ToastItem = {
  id: 1,
  kind: 'claude_cli_update',
  title: 'Доступна новая версия claude CLI',
  body: 'На сервере 2.1.0, последняя 2.2.0: выполните claude update на сервере.',
  url: '/models',
  notificationId: 'n-1',
  sticky: true,
};

// style названного элемента. Порядок атрибутов в разметке — порядок JSX: testid/title
// идут перед style, поэтому стиль берётся из того же тега.
const styleOf = (markup: string, key: string): string => {
  const re = key === 'close'
    ? /<button[^>]*title="Закрыть"[^>]*style="([^"]*)"/
    : new RegExp(`<[^>]*data-testid="${key}"[^>]*style="([^"]*)"`);
  const m = re.exec(markup);
  expect(m, `в разметке нет элемента ${key}`).not.toBeNull();
  return m![1];
};

describe('тост-слой не блокирует интерфейс', () => {
  const markup = render([cliUpdate]);

  it('слой не принимает клики: нажатие в его поле проходит к интерфейсу под ним', () => {
    expect(styleOf(markup, 'toast-layer')).toContain('pointer-events:none');
  });

  it('карточка не принимает клики и не обещает курсор «указатель»', () => {
    const card = styleOf(markup, 'toast');
    expect(card).toContain('pointer-events:none');
    expect(card).not.toContain('cursor:pointer');
  });

  it('крестик карточки кликабелен: им закреплённое снимают и помечают прочитанным', () => {
    expect(styleOf(markup, 'close')).toContain('pointer-events:auto');
  });

  it('уведомление с адресом остаётся открываемым — явной кнопкой, не всей карточкой', () => {
    expect(markup).toContain('data-testid="toast-open"');
    expect(styleOf(markup, 'toast-open')).toContain('pointer-events:auto');
  });

  it('кнопка действия кликабельна так же, как крестик', () => {
    const withAction = render([{
      id: 2, kind: 'success', title: 'Чат архивирован', body: 'Архивирован чат «Разбор».',
      action: { label: 'Отменить', onClick: () => {} },
    }]);
    const m = /<button[^>]*style="([^"]*)"/.exec(withAction);
    expect(m, 'в разметке нет кнопки действия').not.toBeNull();
    expect(m![1]).toContain('pointer-events:auto');
  });

  it('без адреса кнопки «Открыть» нет: нечего и открывать', () => {
    const plain = render([{ id: 3, kind: 'info', title: 'Разобрать в базу', body: 'Пакет правок запрошен.' }]);
    expect(plain).not.toContain('data-testid="toast-open"');
  });

  it('пустой список — слоя нет вовсе (он не закрывает экран пустым полем)', () => {
    expect(render([])).toBe('');
  });
});
