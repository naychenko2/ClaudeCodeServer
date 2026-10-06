// Тело котировки цены (ADR-017 §4): одно на панель, поле ввода, запуск и чип действия. Раньше его собирали
// в трёх местах и копии расходились: у быстрых операций при стрелках на холсте цена уходила с
// hasAnnotations: true, а запуск — с false. Теперь «что уйдёт вместе с запуском» решает одна функция.

import type { EditMode, ImageEditOp, ImageEditQuoteRequest } from '../api';
import { hasAnnotationMark, type Mark } from '../marks';

// Пометки уходят запуску только у правки: «Изменить» и «Изменить отмеченное»; быстрым операциям
// (фон, увеличение, дорисовка) и «Нарисовать» они не нужны
export const marksSent = (op: ImageEditOp, hasImage: boolean, marks: readonly Mark[]): boolean =>
  hasImage && (op === 'edit' || op === 'inpaint') && marks.length > 0;

export interface QuoteBodyInput {
  provider: string;
  model: string;
  mode: EditMode;
  op: ImageEditOp;
  count: number;
  hasImage: boolean;
  marks: readonly Mark[];
  // Маска уходит инпейнту, когда закрашено кистью
  withMask: boolean;
  removal: boolean;
  references: number;
  hasCharacter: boolean;
  size: { w: number; h: number } | null;
  // Чат и ревизия контекста: с ними сервер берёт образцы, персонажа и размер из стора
  context?: { sessionId: string; contextRevision: number } | null;
}

export function buildQuoteBody(i: QuoteBodyInput): ImageEditQuoteRequest {
  return {
    provider: i.provider, model: i.model, mode: i.mode, op: i.op, count: i.count,
    hasMask: i.withMask,
    hasAnnotations: marksSent(i.op, i.hasImage, i.marks) && hasAnnotationMark([...i.marks]),
    removal: i.removal,
    references: i.references, hasCharacter: i.hasCharacter,
    width: i.size?.w ?? null, height: i.size?.h ?? null,
    ...(i.context ? { sessionId: i.context.sessionId, contextRevision: i.context.contextRevision } : null),
  };
}
