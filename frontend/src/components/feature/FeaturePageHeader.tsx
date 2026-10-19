import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ArrowLeft, GitBranch, MoreHorizontal } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { IconButton } from '../ui/IconButton';
import { Button } from '../ui/Button';
import { Menu, MenuItem } from '../ui/Menu';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from '../chat/feature/FeatureGlyphs';

// Тон тега статуса (макет §5.1): run — идёт, wait/merge — ждёт/к мержу,
// stop — стоит, loop — доработка, merged — смержена, closed — без мержа.
export type FeatureStatusTone = 'run' | 'wait' | 'merge' | 'stop' | 'loop' | 'merged' | 'closed';

export interface FeatureStatusTag {
  label: string;
  tone: FeatureStatusTone;
}

// Единый focus-visible ring (клавиатура) — инжектим один раз (паттерн IconButton, §0).
const FOCUS_CLASS = 'cc-feature-page-crumb';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-crumb-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-crumb-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Пульс скелета загрузки (1,4 с); при prefers-reduced-motion — без анимации (макет §5.7).
const SKELETON_CLASS = 'cc-feature-page-skeleton';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-skeleton-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-skeleton-style';
  el.textContent =
    `@keyframes cc-feature-page-sk-pulse{0%,100%{opacity:1}50%{opacity:0.35}}` +
    `.${SKELETON_CLASS}{animation:cc-feature-page-sk-pulse 1.4s ease-in-out infinite;}` +
    `@media (prefers-reduced-motion: reduce){.${SKELETON_CLASS}{animation:none;opacity:0.55}}`;
  document.head.appendChild(el);
}

// Тон тега → стиль (макет §5.1): фон + цвет; у «стоит» — inset-кольцо danger (Р-10).
const TAG_STYLE: Record<FeatureStatusTone, CSSProperties> = {
  run:    { background: C.bgSelected, color: C.textHeading },
  wait:   { background: C.infoBg, color: C.textPrimary },
  merge:  { background: C.infoBg, color: C.textPrimary },
  stop:   { background: C.dangerBg, color: C.dangerText, boxShadow: `inset 0 0 0 1px ${C.danger}` },
  loop:   { background: C.warningBg, color: C.warningText },
  merged: { background: C.successBg, color: C.successText },
  closed: { background: C.bgSelected, color: C.textSecondary },
};

// Точка-глиф (info) — у «ждёт вас»/«к мержу»: тон держит фон, текст — textPrimary (Р-10).
function TagDot() {
  return <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }} />;
}

// Тег статуса в шапке: 11/600, поля 1 7, R 6, gap 4 (макет §5.1).
function StatusTag({ tag }: { tag: FeatureStatusTag }) {
  return (
    <span
      data-testid="feature-page-status"
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans,
        padding: '1px 7px', borderRadius: R.sm,
        ...TAG_STYLE[tag.tone],
      }}
    >
      {(tag.tone === 'wait' || tag.tone === 'merge') && <TagDot />}
      {tag.label}
    </span>
  );
}

export interface FeaturePageHeaderProps {
  // Название фичи (из индекса, фолбэк — эпик страницы).
  title: string;
  // Глиф состояния (FeatureGlyph): wait/stopped/run/loop/closed/done.
  glyph: GlyphKind;
  // Тег статуса (null — нет: загрузка или нет данных).
  statusTag: FeatureStatusTag | null;
  // Ветка worktree эпика (mono + иконка ветки); null — нет.
  branch: string | null;
  // «идёт 1 дн 6 ч» / «шла 3 дн 6 ч»; null — нет.
  ageLabel: string | null;
  // Число задач фичи (эпик + дочерние); null — нет.
  taskCount: number | null;
  // Главное действие (та же функция, что в Ф1.3: featureQuickAction); null — нет.
  mainAction: string | null;
  // Крошка «← …».
  backLabel: string;
  onBack: () => void;
  // Пункт меню ⋯: «Скопировать ветку». («Открыть эпик в «Задачах»» убран: только
  // тост-обещание; переход в «Задачи» будет в Ф4.1.)
  onCopyBranch: () => void;
  // Клик по главному действию.
  onMainAction: () => void;
  isMobile: boolean;
  // Загрузка: вместо названия — полоса-скелет (20×50%), крошка рабочая (макет §5.7).
  loading: boolean;
}

// Шапка страницы фичи (волна 2 «Обзор фич», шаг Ф2.4; макет §5.1–5.2).
//   верхний ряд: крошка (слева) + ⋯ (справа, marginLeft auto);
//   строка заголовка: глиф 14 + h2 (PT Serif 20/17, 700, textHeading, lineHeight 1,25);
//   мета-строка: тег статуса + ветка (mono) + «идёт/шла …» + «M задач»;
//   ряд действий: главное действие (ui/Button sm primary).
// Ловушки <button> (раздел 0 таблицы токенов) заданы явно: рамка/фон/шрифт/поля/выравнивание.
export function FeaturePageHeader({
  title, glyph, statusTag, branch, ageLabel, taskCount, mainAction,
  backLabel, onBack, onCopyBranch, onMainAction, isMobile, loading,
}: FeaturePageHeaderProps) {
  // Крошка: наведение bgSelected/textPrimary (m3, §0).
  const [hover, setHover] = useState(false);
  // Меню ⋯: rect триггера (null — закрыто).
  const [menuAnchor, setMenuAnchor] = useState<DOMRect | null>(null);

  const showMeta = !loading && (statusTag != null || branch != null || ageLabel != null || taskCount != null);

  return (
    <div
      data-testid="feature-page-header"
      style={{ flexShrink: 0, padding: '12px 16px 10px', borderBottom: `1px solid ${C.borderLight}` }}
    >
      {/* Верхний ряд: крошка (слева) + ⋯ (справа). minHeight 28, gap 6, marginBottom 6. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 6, minHeight: 28 }}>
        <button
          type="button"
          data-testid="feature-page-back"
          className={FOCUS_CLASS}
          onClick={onBack}
          onMouseEnter={() => setHover(true)}
          onMouseLeave={() => setHover(false)}
          style={{
            // Ловушки <button> (§0): рамка/фон/шрифт/поля/выравнивание явно.
            display: 'inline-flex', alignItems: 'center', gap: 5,
            border: 'none', cursor: 'pointer',
            color: hover ? C.textPrimary : C.textSecondary,
            background: hover ? C.bgSelected : 'transparent',
            fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 400,
            padding: '4px 8px 4px 4px', borderRadius: R.md,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            maxWidth: 260, boxSizing: 'border-box',
            minHeight: isMobile ? 40 : undefined,
          }}
        >
          <ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          {backLabel}
        </button>
        {/* ⋯: IconButton sm (360: lg), marginLeft auto (справа). */}
        <IconButton
          size={isMobile ? 'lg' : 'sm'}
          title="Ещё действия"
          ariaLabel="Ещё действия"
          expanded={menuAnchor != null}
          onClick={e => setMenuAnchor(a => (a ? null : (e.currentTarget as HTMLElement).getBoundingClientRect()))}
          style={{ marginLeft: 'auto' }}
        >
          <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>

      {/* Строка заголовка: глиф 14 + h2. Загрузка — полоса-скелет 20×50% вместо названия. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 9, minWidth: 0 }}>
        {loading ? (
          <div
            data-testid="feature-page-title-skeleton"
            className={SKELETON_CLASS}
            style={{ height: 20, width: '50%', background: C.bgSelected, borderRadius: R.sm, flexShrink: 0 }}
          />
        ) : (
          <>
            <span style={{ display: 'flex', flexShrink: 0 }}>
              <FeatureGlyph kind={glyph} size={14} />
            </span>
            <h2
              data-testid="feature-page-title"
              style={{
                margin: 0, fontFamily: FONT.serif,
                fontSize: isMobile ? 17 : 20, fontWeight: 700, color: C.textHeading,
                lineHeight: 1.25, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}
            >
              {title}
            </h2>
          </>
        )}
      </div>

      {/* Мета-строка: тег статуса + ветка + «идёт/шла …» + «M задач». gap 4 12, wrap, marginTop 6. */}
      {showMeta && (
        <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '4px 12px', marginTop: 6, fontSize: FS.sm, color: C.textSecondary }}>
          {statusTag && <StatusTag tag={statusTag} />}
          {branch && (
            <span
              data-testid="feature-page-branch"
              style={{ fontFamily: FONT.mono, fontSize: FS.xs, display: 'inline-flex', alignItems: 'center', gap: 4, color: C.textSecondary }}
            >
              <GitBranch size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
              {branch}
            </span>
          )}
          {ageLabel && (
            <span data-testid="feature-page-age" style={{ fontSize: FS.sm, color: C.textSecondary }}>{ageLabel}</span>
          )}
          {taskCount != null && (
            <span data-testid="feature-page-taskcount" style={{ fontSize: FS.sm, color: C.textSecondary }}>{taskCount} задач</span>
          )}
        </div>
      )}

      {/* Ряд действий: главное действие (та же функция, что в Ф1.3). gap 8, wrap, marginTop 10. */}
      {mainAction && (
        <div data-testid="feature-page-main-action" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8, marginTop: 10 }}>
          <Button
            size="sm"
            variant="primary"
            onClick={onMainAction}
            style={isMobile ? { minHeight: 40 } : undefined}
          >
            {mainAction}
          </Button>
        </div>
      )}

      {/* Меню ⋯: «Скопировать ветку» (без «Закрыть без мержа» и «Вернуть в работу» —
          за скобками волны, макет §5.2; «Открыть эпик в «Задачах»» убран — только
          тост-обещание, переход будет в Ф4.1). */}
      {menuAnchor && (
        <Menu
          onClose={() => setMenuAnchor(null)}
          anchor={menuAnchor}
          minWidth={220}
          maxHeight={isMobile ? 200 : 160}
          gap={4}
        >
          <MenuItem
            label="Скопировать ветку"
            onClick={() => { setMenuAnchor(null); onCopyBranch(); }}
            wrapper={isMobile ? { style: { minHeight: 44 } } : undefined}
          />
        </Menu>
      )}
    </div>
  );
}
