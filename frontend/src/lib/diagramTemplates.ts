// Стартовые шаблоны для создания диаграмм из меню «+» в панели файлов.
// Содержимое валидно для соответствующих просмотрщиков (ExcalidrawViewer,
// DrawioViewer, MermaidDiagram): пустой файл mermaid/excalidraw без заголовка
// открывался бы хуже, а .mmd без типа диаграммы вообще не рендерится.

export type DiagramKind = 'excalidraw' | 'drawio' | 'mermaid';

export interface DiagramMeta {
  /** Расширение файла (без точки) */
  ext: string;
  /** Название в диалоге */
  label: string;
  /** Короткая подсказка-описание */
  hint: string;
  /** Стартовое содержимое файла */
  template: string;
}

export const DIAGRAM_META: Record<DiagramKind, DiagramMeta> = {
  excalidraw: {
    ext: 'excalidraw',
    label: 'Excalidraw',
    hint: 'Наброски от руки',
    // Полный заголовок сцены: файл узнают excalidraw.com и Obsidian-плагин;
    // наш просмотрщик валидирует пустой файл тоже, но полный — честнее
    template: '{"type":"excalidraw","version":2,"source":"ccs","elements":[],"appState":{},"files":{}}',
  },
  drawio: {
    ext: 'drawio',
    label: 'draw.io',
    hint: 'Формальные схемы',
    // Минимальная модель с одним пустым слоем — DrawioViewer грузит её через init
    template: '<mxfile host="ccs"><diagram id="d1" name="Страница 1"><mxGraphModel dx="800" dy="600" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="850" pageHeight="1100" math="0" shadow="0"><root><mxCell id="0"/><mxCell id="1" parent="0"/></root></mxGraphModel></diagram></mxfile>',
  },
  mermaid: {
    ext: 'mmd',
    label: 'Mermaid',
    hint: 'Код → схема',
    // Без типа диаграммы mermaid не рендерится — даём стартовый flowchart
    template: 'flowchart TD\n    Start([Начало]) --> Finish([Конец])',
  },
};

export const DIAGRAM_KINDS: DiagramKind[] = ['excalidraw', 'drawio', 'mermaid'];

/** Дефолтное имя файла для типа (без папки) */
export function diagramFileName(kind: DiagramKind): string {
  return `diagram.${DIAGRAM_META[kind].ext}`;
}

/**
 * Заменить расширение в имени на расширение выбранного типа — но только если
 * текущий хвост совпадает с расширением одного из типов (т.е. имя ещё не правили
 * руками под своё). Пользовательское расширение («schema.v2.drawio») не трогаем.
 */
export function retargetDiagramExt(name: string, kind: DiagramKind): string {
  // Сравнение в нижнем регистре (DIAGRAM.DIO — тоже drawio), а режем по факту:
  // суффикс известного расширения меняем на суффикс выбранного типа
  const lower = name.toLowerCase();
  const hit = DIAGRAM_KINDS
    .map(k => `.${DIAGRAM_META[k].ext}`)
    .find(ext => lower.endsWith(ext));
  if (!hit) return name; // правленое/своё расширение — не лезем
  return name.slice(0, name.length - hit.length) + `.${DIAGRAM_META[kind].ext}`;
}
