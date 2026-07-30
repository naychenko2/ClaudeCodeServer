// Карточка вызова инструмента: статус прерванного фонового субагента (bgAborted).
// Регрессия: карточка обычного (не персоны) фонового агента, остановленного пользователем,
// показывала «готово», как будто задача успешно завершена, — bgAborted нигде не читался.
// Рендерим статикой через react-dom/server — как соседний TeamEscalationView.test.
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import { ToolUseView } from '../ToolUseView';
import { PersonaConsultCard } from '../PersonaTaskView';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Квитанция фонового запуска (isAsyncLaunchAck): tool_result приходит мгновенно,
// завершение агента отслеживается по bgDone/bgAborted, а не по этому тексту
const ASYNC_ACK =
  'Async agent launched successfully.\nagentId: a011da168d23b9e32\noutput_file: /tmp/out.txt';

function bgAgent(over: Partial<ToolItem>): ToolItem {
  return {
    kind: 'tool_use',
    id: 't1',
    name: 'Task',
    input: { description: 'Прочитать 100 файлов', prompt: 'Прочитай построчно…' },
    result: ASYNC_ACK,
    bgDone: true,
    ...over,
  };
}

const renderTool = (item: ToolItem) =>
  renderToStaticMarkup(createElement(ToolUseView, { item, online: true }));

describe('ToolUseView — прерванный фоновый субагент', () => {
  it('bgDone + bgAborted: шапка показывает «прервано», а не «готово»', () => {
    const html = renderTool(bgAgent({ bgAborted: true }));
    expect(html).toContain('прервано');
    expect(html).not.toContain('готово');
  });

  it('bgDone без прерывания: честное «готово», «прервано» нет', () => {
    const html = renderTool(bgAgent({}));
    expect(html).toContain('готово');
    expect(html).not.toContain('прервано');
  });

  it('isError сильнее bgAborted: статус «ошибка»', () => {
    const html = renderTool(bgAgent({ bgAborted: true, isError: true }));
    expect(html).toContain('ошибка');
    expect(html).not.toContain('прервано');
  });
});

describe('PersonaConsultCard — шапка при прерванном агенте', () => {
  const base = {
    question: 'Разберись в коде',
    running: false,
    isError: false,
    answer: '',
    emptyAnswerNote: 'Агент прерван — ответа не будет',
  };

  it('aborted: статус «прервано» в шапке', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, aborted: true }));
    expect(html).toContain('прервано');
    expect(html).toContain('Агент прерван — ответа не будет');
  });

  it('без aborted: шапка без статуса, дефолтная пометка пустого ответа', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, emptyAnswerNote: undefined }));
    expect(html).not.toContain('прервано');
    expect(html).toContain('Ответ передан без текста');
  });
});
