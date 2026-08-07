// Навигация по графу — «Назад» плюс цепочка крошек. Одна на два места: шапку
// документа в центре и карту проекта в панели рельсы. Общий компонент, а не копия:
// цепочка читает и правит ОДИН стор, и разъехавшиеся правила сворачивания середины
// («…») выглядели бы как два разных графа.
//
// Крошки одинаково понимают оба вида шагов: группа ведёт в «Обзор» с соответствующим
// раскрытием, тип — в «Фокус» на нём (см. lib/codeGraph.ts).
import { useMemo, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { BackButton } from '../../components/ui';
import { ICON_SIZE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions } from '../../lib/codeGraph';

interface CrumbDisplay {
  key: string;
  label: string;
  step: number;   // индекс в navPath, -1 — корень «Обзора»
}

// Сколько последних шагов показываем целиком: цепочка не растёт неограниченно,
// середина сворачивается в «…». В узкой колонке панели хвост короче.
function maxTailFor(compact: boolean, isMobile: boolean): number {
  if (compact) return 2;
  return isMobile ? 2 : 4;
}

export function CodeGraphNavBar({ compact, isMobile, trailing, style }: {
  // Панельный режим: короче хвост крошек и плотнее отступы
  compact?: boolean;
  isMobile?: boolean;
  // Приписка справа (в документе — FQN узла в фокусе)
  trailing?: React.ReactNode;
  style?: React.CSSProperties;
}) {
  const s = useCodeGraph();
  const a = useCodeGraphActions();

  const crumbs = useMemo<CrumbDisplay[]>(() => {
    const root: CrumbDisplay = { key: 'root', label: 'Обзор', step: -1 };
    if (!s.navPath.length) return [root];
    const byId = s.data ? new Map(s.data.nodes.map(n => [n.id, n])) : null;
    const steps: CrumbDisplay[] = s.navPath.map((step, i) => step.kind === 'node'
      ? { key: `n:${step.id}:${i}`, label: byId?.get(step.id)?.label ?? step.id, step: i }
      : { key: `g:${step.group}:${i}`, label: step.group.split('.').pop() ?? step.group, step: i });
    const maxTail = maxTailFor(!!compact, !!isMobile);
    if (steps.length <= maxTail) return [root, ...steps];
    const tail = steps.slice(-maxTail);
    const ellipsis: CrumbDisplay = { key: 'ellipsis', label: '…', step: steps.length - maxTail - 1 };
    return [root, ellipsis, ...tail];
  }, [s.navPath, s.data, compact, isMobile]);

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: compact ? SP.xs : SP.sm,
      padding: compact ? `${SP.xs}px ${SP.sm}px` : `${SP.sm}px ${SP.md}px`,
      borderBottom: `1px solid ${C.borderLight}`, background: C.bgPanel, flexShrink: 0,
      overflowX: 'auto', whiteSpace: 'nowrap', scrollbarWidth: 'none',
      ...style,
    }}>
      <BackButton onClick={() => a.back()} title="Назад" iconSize={ICON_SIZE.xs}
        style={{ opacity: s.navPath.length ? 1 : 0.4, pointerEvents: s.navPath.length ? 'auto' : 'none' }}>
        <span style={{ fontSize: FS.xs, color: C.textSecondary }}>Назад</span>
      </BackButton>
      {crumbs.map((c, i) => (
        <Crumb key={c.key} first={i === 0} last={i === crumbs.length - 1}
          onJump={() => a.toStep(c.step)}>{c.label}</Crumb>
      ))}
      {trailing}
    </div>
  );
}

// Ступень цепочки. Кликабельная ведёт назад по пути, последняя — текущее место
// (без клика). Форма — та же, что у крошек «Файлов» и разбора расходов: разделитель
// «›», активная жирнее, наведение подсвечивает переход.
function Crumb({ children, first, last, onJump }: {
  children: React.ReactNode;
  first: boolean;
  last: boolean;
  onJump: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
      {!first && <span style={{ color: C.textMuted, fontSize: FS.xs }}>›</span>}
      <span
        onClick={last ? undefined : onJump}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        title={last ? undefined : `Вернуться к ${children}`}
        style={{
          fontFamily: FONT.mono, fontSize: FS.xs,
          color: last || hover ? C.textHeading : C.textSecondary,
          fontWeight: last ? 600 : 400,
          cursor: last ? 'default' : 'pointer',
          background: !last && hover ? C.bgSelected : 'transparent',
          padding: `${SP.xxs}px ${SP.xs}px`, borderRadius: R.sm,
        }}>{children}</span>
    </span>
  );
}
