// Цвета и глифы графа — производные от токенов дизайн-системы (C.*). Никакого
// сырого hex: меняется тема — меняются и цвета узлов/рёбер. Соответствие токенов
// семантике связей и типов — из макета Майи (docs/mockups/code-graph-panel.md).
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
