// Ступени рельса маркеров комментариев считаются по ширине САМОГО документа: в сплите
// с чатом окно широкое, а тексту остаётся треть экрана. Проверяем границы ступеней,
// отсутствие замера и тач-минимум — геометрия в jsdom не воспроизводится, а это чистая
// функция и вся раскладка держится на ней.
import { describe, it, expect, vi } from 'vitest';

// DocComments тянет react-markdown, api и персон — для чистой функции они не нужны
vi.mock('react-markdown', () => ({ default: () => null, defaultUrlTransform: (u: string) => u }));
vi.mock('remark-gfm', () => ({ default: () => undefined }));
vi.mock('react-syntax-highlighter', () => ({ Prism: () => null }));
vi.mock('react-syntax-highlighter/dist/esm/styles/prism', () => ({ oneDark: {} }));
vi.mock('../../../components/MermaidDiagram', () => ({ MermaidDiagram: () => null }));
vi.mock('../../../lib/api', () => ({ api: {} }));

import { railFor } from '../DocComments';

describe('railFor', () => {
  it('до замера идёт по isMobile', () => {
    expect(railFor(null).size).toBe(19);
    expect(railFor(null, true).size).toBe(16);
  });

  it('нулевая ширина — это скрытый предок, а не узкий документ', () => {
    // Панель во вкладке/свёрнутом контейнере меряется как 0 — мелкий рельс в первом
    // кадре после показа был бы враньём
    expect(railFor(0).size).toBe(19);
    expect(railFor(0, true).size).toBe(16);
  });

  it('три ступени по ширине документа', () => {
    expect(railFor(360).size).toBe(14);    // мобила, узкий сплит
    expect(railFor(419).size).toBe(14);
    expect(railFor(420).size).toBe(16);    // обычный сплит с чатом
    expect(railFor(639).size).toBe(16);
    expect(railFor(640).size).toBe(19);    // полный экран
    expect(railFor(1200).size).toBe(19);
  });

  it('на тач-экране не опускается ниже 16 — пальцем нужна цель покрупнее', () => {
    expect(railFor(360, true).size).toBe(16);
    expect(railFor(1200, true).size).toBe(19);
  });

  it('зазор и иконка идут за размером флажка', () => {
    expect(railFor(1200)).toEqual({ size: 19, gap: 8, icon: 10 });
    expect(railFor(500)).toEqual({ size: 16, gap: 6, icon: 9 });
    expect(railFor(360)).toEqual({ size: 14, gap: 4, icon: 8 });
  });
});
