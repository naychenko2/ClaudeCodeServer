// Служебные маркеры протоколов не должны доезжать до ленты: <promise>ГОТОВО</promise>
// (цикл «до готово») и протокол «Командной реализации» — <team:work>, <escalate:*>.
// Внутри кода маркер остаётся: там его цитируют, и детекторы бэкенда его так же
// игнорируют (SessionManager.ParseWorkMarker / ParseEscalationMarker).
import { describe, it, expect, vi } from 'vitest';

// MarkdownContent тянет react-markdown и api-модуль — для чистой функции не нужны
vi.mock('react-markdown', () => ({ default: () => null, defaultUrlTransform: (u: string) => u }));
vi.mock('remark-gfm', () => ({ default: () => undefined }));
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../../lib/api', () => ({ api: {} }));

import { stripServiceMarkers } from '../MarkdownContent';

describe('stripServiceMarkers', () => {
  it('режет маркер работы координатора вместе с постановкой', () => {
    const text = 'Понял, беру в работу.\n<team:work>Создать backend/Counter.cs</team>';
    expect(stripServiceMarkers(text)).toBe('Понял, беру в работу.');
  });

  it('режет маркер эскалации и маркер завершения цикла', () => {
    expect(stripServiceMarkers('Итог.\n<escalate:decision>Какой формат?</escalate>')).toBe('Итог.');
    expect(stripServiceMarkers('Всё сделано.\n<promise>ГОТОВО</promise>')).toBe('Всё сделано.');
  });

  it('режет незакрытый маркер в хвосте — ход ещё стримится', () => {
    expect(stripServiceMarkers('Беру.\n<team:work>Создать Coun')).toBe('Беру.');
  });

  it('не трогает маркер внутри кода — там его цитируют, объясняя протокол', () => {
    const inline = 'Ставлю маркер `<team:work>постановка</team>` в конце ответа';
    expect(stripServiceMarkers(inline)).toBe(inline);
    const block = 'Протокол:\n```\n<team:work>пример</team>\n```';
    expect(stripServiceMarkers(block)).toBe(block);
  });

  it('обычный текст не меняет', () => {
    expect(stripServiceMarkers('Разложил на три под-задачи, жду подтверждения.'))
      .toBe('Разложил на три под-задачи, жду подтверждения.');
  });
});
