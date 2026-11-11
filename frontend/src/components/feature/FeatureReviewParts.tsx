// Каркас вкладки «Разбор» (волна 4, FS-FR0; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §2 «Якоря»/«Заголовок секции», §5): общие кирпичи для шести секций — секция с якорем,
// строка «ключ — значение», ряд якорей. Тела секций делают FS-FR1–FS-FR6, сборку вкладки и
// прокрутку к якорю — FS-FR. Здесь нет данных и состояния: только раскладка.

import { type CSSProperties, type ReactNode } from 'react';
import { C, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';

// === Секции ===

// Ключи и подписи шести секций (макет §5). Единый источник порядка: якорь и секция
// ссылаются одним id — контракт «id секции совпадает с ключом из REVIEW_SECTIONS».
export type ReviewSectionId = 'time' | 'volume' | 'returns' | 'who' | 'spend' | 'history';

export interface ReviewAnchorItem { id: ReviewSectionId; label: string }

export const REVIEW_SECTIONS: readonly ReviewAnchorItem[] = [
  { id: 'time', label: 'Время и срок' },
  { id: 'volume', label: 'Объём' },
  { id: 'returns', label: 'Возвраты и потери' },
  { id: 'who', label: 'Кто работал' },
  { id: 'spend', label: 'Расход' },
  { id: 'history', label: 'Хронология' },
];

// Заголовок секции — тот же стиль, что GroupHeader групп «Материалов»
// (FeaturePlansTab.tsx:112): FS.xs, 700, uppercase, letterSpacing 0.04em, C.textMuted.
// Счётчика нет: в «Разборе» у групп нет счётчиков (в отличие от «Материалов»).
const SECTION_TITLE: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
  letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted,
};

// Секция: якорь `rz-<id>` для прокрутки (прокрутку делает вызывающий, FS-FR).
// Зазор между секциями — SP.xl (макет §5: «сверху итог, ниже детали» — секции разделены).
export function ReviewSection({ id, title, children }: { id: ReviewSectionId; title: string; children: ReactNode }) {
  return (
    <section id={'rz-' + id} data-testid={'review-sec-' + id} style={{ marginBottom: SP.xl }}>
      <div style={{ padding: `2px ${SP.sm}px` }}>
        <span style={SECTION_TITLE}>{title}</span>
      </div>
      {children}
    </section>
  );
}

// === «Ключ — значение» ===

// Строки величин «Разбора» (макет §5): подпись слева, значение справа.
// Строка с value == null не рендерится: «не считалось»/«нет» вызывающий говорит словом
// в value, null — сказать нечего, строки нет. Пустой список — компонент ничего не рендерит.
export interface ReviewKvRow { label: string; value: ReactNode | null }

const KV_LABEL: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary };
const KV_VALUE: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, textAlign: 'right' };

export function ReviewKv({ rows }: { rows: ReviewKvRow[] }) {
  const visible = rows.filter((r) => r.value != null);
  if (visible.length === 0) return null;
  return (
    <div data-testid="review-kv">
      {visible.map((row, i) => (
        <div
          key={i}
          data-testid="review-kv-row"
          style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.md, padding: `${SP.xs}px 0` }}
        >
          <span style={KV_LABEL}>{row.label}</span>
          <span style={KV_VALUE}>{row.value}</span>
        </div>
      ))}
    </div>
  );
}

// === Ряд якорей ===

// Полоса якорей прокручивается горизонтально без видимой полосы прокрутки —
// тот же класс-инжект, что у FeaturePageTabs (макет .anchors { overflow-x:auto; scrollbar-width:none }).
const NO_SB_CLASS = 'cc-feature-review-anchors';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-review-anchors-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-review-anchors-style';
  el.textContent =
    `.${NO_SB_CLASS}::-webkit-scrollbar{display:none;}` +
    `.${NO_SB_CLASS}{scrollbar-width:none;}`;
  document.head.appendChild(el);
}

export interface ReviewAnchorsProps {
  items: readonly ReviewAnchorItem[];
  // Клик по якорю — один вызов с id секции; прокрутку делает вызывающий (FS-FR).
  onJump: (id: ReviewSectionId) => void;
  // На 360 якорь не меньше 40 px (макет .w360 .btn.sm.touch; паттерн высоты «в месте
  // вызова» — как FeatureBackChip.tsx:39). Без сигнала мобилы высота остаётся кита sm (32).
  isMobile?: boolean;
}

// Якорь — Button sm ghost pill (макет §2 «Якоря „Разбора“»). Состояния выбора НЕТ:
// это переход к месту, а не переключатель — ни aria-pressed, ни aria-selected.
// flexShrink 0 + overflowX auto: на 360 ряд скроллится сам, страница не переполняется.
export function ReviewAnchors({ items, onJump, isMobile = false }: ReviewAnchorsProps) {
  return (
    <div
      data-testid="review-anchors"
      className={NO_SB_CLASS}
      style={{ display: 'flex', gap: SP.xs, overflowX: 'auto', padding: `0 0 ${SP.xxs}px` }}
    >
      {items.map((it) => (
        <Button
          key={it.id}
          size="sm"
          variant="ghost"
          pill
          dataTestId={'review-anchor-' + it.id}
          style={{ flexShrink: 0, ...(isMobile ? { minHeight: 40 } : {}) }}
          onClick={() => onJump(it.id)}
        >
          {it.label}
        </Button>
      ))}
    </div>
  );
}