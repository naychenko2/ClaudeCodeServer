// Цвета и глифы графа — производные от токенов дизайн-системы (C.*). Никакого
// сырого hex: меняется тема — меняются и цвета узлов/рёбер. Соответствие токенов
// семантике связей и типов — из макета Майи (docs/design/mockups/code-graph-panel.md).
import { C } from '../../lib/design';
import type { CodeGraphNodeKind, CodeGraphRelation, CodeGraphConfidence } from '../../types';

// Цвет ребра по типу связи. References — отдельный от Calls оттенок (индиго),
// Implements — «позитивная» связь (зелёный). Accent (оранжевый) НЕ трогаем.
export const EDGE_COLOR: Record<CodeGraphRelation, string> = {
  Calls: C.info,
  Implements: C.success,
  References: C.plan,
};

// Фон чипа/бейджа связи (м soft-подложка под цвет ребра)
export const EDGE_BG: Record<CodeGraphRelation, string> = {
  Calls: C.infoBg,
  Implements: C.successBg,
  References: C.planLight,
};

// Цвет узла по типу. Class — нейтральный (большинство узлов), остальные — свои.
export const KIND_COLOR: Record<CodeGraphNodeKind, string> = {
  Class: C.textSecondary,
  Interface: C.info,
  Struct: C.success,
  Enum: C.plan,
};

// Контур кольца узла-класса в тёмной теме должен читаться на белом фоне холста —
// textSecondary там светлый, поэтому для кольца Class берём заголовочный тон
export const KIND_RING: Record<CodeGraphNodeKind, string> = {
  Class: C.textHeading,
  Interface: C.info,
  Struct: C.success,
  Enum: C.plan,
};

// Глиф типа в центре кружка (моноширинная буква)
export const KIND_GLYPH: Record<CodeGraphNodeKind, string> = {
  Class: 'C',
  Interface: 'I',
  Struct: 'S',
  Enum: 'E',
};

// Русские подписи связей для легенды/паспорта
export const RELATION_LABEL: Record<CodeGraphRelation, string> = {
  Calls: 'Вызывает',
  Implements: 'Реализует',
  References: 'Упоминает',
};

// Пунктир = Inferred (связь выведена эвристикой, не из анализа кода)
export function isDashed(confidence: CodeGraphConfidence): boolean {
  return confidence === 'Inferred';
}

// === Геометрия узла на холсте ===
// Одни значения на оба холста («Фокус» и «Обзор») и в тех же величинах, что у графа
// заметок (features/notes/graph): обводка ~2, кольцо выделения в 3px от кружка. Два
// графа продукта не обязаны совпадать раскладкой — но толщины и зазоры, разъехавшиеся
// по вкусу автора (2.2 здесь, 1.2 там), читаются как разные приложения.
export const NODE_STROKE = 2;        // обводка кружка узла
export const NODE_STROKE_MAIN = 3.5; // центр «Фокуса» — единственный, кто толще
export const RING_HOVER_GAP = 3;     // кольцо под курсором
export const RING_GOD_GAP = 6;       // пунктирное кольцо god-узла
export const HIT_PAD = 10;           // прозрачный hit-target шире кружка
export const HIT_MIN = 20;           // …но не мельче пальца
export const LABEL_HALO = 3;         // подложка подписи цветом холста (читаемость поверх линий)
