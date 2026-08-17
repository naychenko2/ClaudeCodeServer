import { describe, expect, it } from 'vitest';
import { DIAGRAM_KINDS, DIAGRAM_META, diagramFileName, retargetDiagramExt } from '../diagramTemplates';

// Шаблоны создания диаграмм: содержимое обязано быть валидным для своих
// просмотрщиков — иначе «создал диаграмму» закончится ошибкой рендера.
describe('diagramTemplates', () => {
  it('excalidraw-шаблон — валидный JSON с пустой сценой', () => {
    const parsed = JSON.parse(DIAGRAM_META.excalidraw.template) as { type: string; elements: unknown[] };
    expect(parsed.type).toBe('excalidraw');
    expect(Array.isArray(parsed.elements)).toBe(true);
    expect(parsed.elements).toHaveLength(0);
  });

  it('drawio-шаблон — валидный XML с mxGraphModel и root', () => {
    const t = DIAGRAM_META.drawio.template;
    // Структурная проверка без DOM (в node-окружении vitest нет DOMParser):
    // парные теги и обязательные узлы модели draw.io
    for (const tag of ['<mxfile', '<diagram', '<mxGraphModel', '<root>', '<mxCell id="0"/>', '<mxCell id="1" parent="0"/>']) {
      expect(t).toContain(tag);
    }
    expect(t).toContain('</mxGraphModel></diagram></mxfile>');
  });

  it('mermaid-шаблон — начинается с типа диаграммы и непуст', () => {
    const t = DIAGRAM_META.mermaid.template;
    expect(t.startsWith('flowchart')).toBe(true);
    expect(t).toContain('-->');
  });

  it('у каждого типа заданы ext/label/hint/template', () => {
    for (const kind of DIAGRAM_KINDS) {
      const m = DIAGRAM_META[kind];
      expect(m.ext).toBeTruthy();
      expect(m.label).toBeTruthy();
      expect(m.hint).toBeTruthy();
      expect(m.template.trim().length).toBeGreaterThan(0);
    }
  });
});

describe('diagramFileName / retargetDiagramExt', () => {
  it('дефолтное имя — diagram.<ext>', () => {
    expect(diagramFileName('excalidraw')).toBe('diagram.excalidraw');
    expect(diagramFileName('drawio')).toBe('diagram.drawio');
    expect(diagramFileName('mermaid')).toBe('diagram.mmd');
  });

  it('смена типа обновляет известное расширение', () => {
    expect(retargetDiagramExt('diagram.excalidraw', 'drawio')).toBe('diagram.drawio');
    expect(retargetDiagramExt('моя схема.mmd', 'excalidraw')).toBe('моя схема.excalidraw');
  });

  it('правленое расширение не трогаем', () => {
    // .v2 перед расширением — часть имени, хвост .drawio всё равно известного типа
    expect(retargetDiagramExt('schema.v2.drawio', 'mermaid')).toBe('schema.v2.mmd');
    // своё расширение/без расширения — не лезем
    expect(retargetDiagramExt('архив.tar.gz', 'drawio')).toBe('архив.tar.gz');
    expect(retargetDiagramExt('без-расширения', 'drawio')).toBe('без-расширения');
  });

  it('регистр расширения не важен при замене', () => {
    expect(retargetDiagramExt('DIAGRAM.DRAWIO', 'excalidraw')).toBe('DIAGRAM.excalidraw');
    // .dio — альтернативный синоним drawio, не входящий в наши мета-ключи:
    // для функции это «своё расширение», не трогаем
    expect(retargetDiagramExt('DIAGRAM.DIO', 'excalidraw')).toBe('DIAGRAM.DIO');
  });
});
