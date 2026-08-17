import { describe, expect, it } from 'vitest';
import { parseExcalidrawScene } from '../ExcalidrawViewer';

// Валидация .excalidraw-входа: компонент на её основе решает — рендерить сцену,
// пустой лист или empty-state «не похоже на файл Excalidraw».
describe('parseExcalidrawScene', () => {
  it('корректная сцена → объект с elements', () => {
    const json = JSON.stringify({ type: 'excalidraw', version: 2, elements: [{ id: 'a', type: 'rectangle' }], appState: {}, files: {} });
    expect(parseExcalidrawScene(json)).toEqual({ elements: [{ id: 'a', type: 'rectangle' }] });
  });

  it('пустой файл → пустая сцена (чистый лист)', () => {
    expect(parseExcalidrawScene('')).toEqual({ elements: [] });
    expect(parseExcalidrawScene('   \n  ')).toEqual({ elements: [] });
  });

  it('битый JSON → null (empty-state)', () => {
    expect(parseExcalidrawScene('{ "elements": [')).toBeNull();
  });

  it('не объект (массив/строка/число) → null', () => {
    expect(parseExcalidrawScene('[1,2,3]')).toBeNull();
    expect(parseExcalidrawScene('"text"')).toBeNull();
    expect(parseExcalidrawScene('42')).toBeNull();
  });

  it('объект без массива elements → null', () => {
    expect(parseExcalidrawScene('{"type":"excalidraw"}')).toBeNull();
    expect(parseExcalidrawScene('{"elements":"nope"}')).toBeNull();
  });

  it('null/undefined → пустая сцена, не падение', () => {
    expect(parseExcalidrawScene(null as unknown as string)).toEqual({ elements: [] });
    expect(parseExcalidrawScene(undefined as unknown as string)).toEqual({ elements: [] });
  });

  it('elements: null → null (empty-state)', () => {
    expect(parseExcalidrawScene('{"elements":null}')).toBeNull();
  });

  it('большая сцена (10k элементов) → парсится без обрыва', () => {
    const elements = Array.from({ length: 10000 }, (_, i) => ({ id: String(i), type: 'rectangle', x: i, y: i }));
    const json = JSON.stringify({ type: 'excalidraw', version: 2, elements, appState: {}, files: {} });
    const scene = parseExcalidrawScene(json);
    expect(scene).not.toBeNull();
    expect(scene?.elements).toHaveLength(10000);
  });
});
