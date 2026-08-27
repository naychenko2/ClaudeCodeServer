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
    emptyAnswerNote: 'Выдача прервана — ответа нет',
  };

  it('aborted: статус «прервано» в шапке и пометка про обрыв выдачи', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, aborted: true }));
    expect(html).toContain('прервано');
    expect(html).toContain('Выдача прервана — ответа нет');
    // P8: категоричного «ответа не будет» карточка не утверждает
    expect(html).not.toContain('ответа не будет');
  });

  it('без aborted: шапка без статуса, дефолтная пометка пустого ответа', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, emptyAnswerNote: undefined }));
    expect(html).not.toContain('прервано');
    expect(html).toContain('Ответ передан без текста');
  });
});

// Карточку переиспользуют консультации персон (PersonaTaskView) и агенты Workflow
// (WorkflowBlockView) — новых пропсов они не передают и обязаны рендериться как раньше.
// Всё новое поведение — строго под условием переданного пропса.
describe('PersonaConsultCard — старые вызовы без новых пропсов', () => {
  const base = {
    question: 'Разберись в коде',
    running: false,
    isError: false,
    answer: 'Готово',
  };
  const render = (props: Record<string, unknown>) =>
    renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, ...props }));

  it('без персоны — заголовок «Агент», без чипа роли и без шеврона сворачивания', () => {
    const html = render({ badge: null });
    expect(html).toContain('Агент');
    expect(html).toContain('var(--c-bg-white)');   // поверхность прежняя, не quiet
    expect(html).not.toContain('role="button"');   // шапка не кликабельна без onCollapse
  });

  it('isError без statusLine — прежняя danger-коробка с фолбэком', () => {
    const html = render({ isError: true, answer: '' });
    expect(html).toContain('var(--c-danger-bg)');
    expect(html).toContain('Не удалось получить ответ персоны');
  });

  it('running без statusLine — спиннер с подписью и italic-ожидание в теле', () => {
    const html = render({ running: true });
    expect(html).toContain('Консультируется…');
    expect(html).toContain('изучает материалы и готовит ответ');
  });
});

describe('PersonaConsultCard — ход координатора (statusLine)', () => {
  const coord = {
    question: '',
    statusLine: 'Разбирает доклады волны 2',
    running: false,
    isError: true,
    answer: 'Волна 2: три задачи закрыты, одна вернулась на доработку',
    metrics: { tokens: 4200, toolUses: 4, durationMs: 12000 },
    badge: 'координатор',
    fallbackTitle: 'Координатор',
  };
  const html = () => renderToStaticMarkup(createElement(PersonaConsultCard, coord));

  it('сорвавшийся ход: тело — сводка, а не danger-коробка', () => {
    const h = html();
    expect(h).not.toContain('var(--c-danger-bg)');
    expect(h).not.toContain('Не удалось получить ответ персоны');
    expect(h).toContain('Волна 2: три задачи закрыты');
    expect(h).toContain('ошибка');   // признак сбоя несёт шапка
  });

  it('сорвавшийся ход: футер метрик на месте', () => {
    const h = html();
    expect(h).toContain('токенов');
    expect(h).toContain('4 действия');
    expect(h).toContain('12с');
  });

  it('без персоны карточка называет роль: заголовок и чип', () => {
    const h = html();
    expect(h).toContain('Координатор');
    expect(h).toContain('координатор');
  });

  it('quiet + onCollapse: приглушённая поверхность и кликабельная шапка', () => {
    const h = renderToStaticMarkup(createElement(PersonaConsultCard, { ...coord, quiet: true, onCollapse: () => {} }));
    expect(h).toContain('var(--c-bg-card)');
    expect(h).not.toContain('var(--c-bg-white)');
    expect(h).toContain('role="button"');
  });
});
