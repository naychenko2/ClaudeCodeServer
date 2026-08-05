// Карта проекта в панели рельсы — уменьшенная копия холста центра, а не отдельное
// представление: тот же стор, те же правила, тот же вид (обзор либо фокус). Отличаются
// только размеры холста и плотность — в полосе 340×230 больше не помещается.
//
// Копия, а не «своя логика», намеренно: пока у карты были собственные правила
// раскрытия, она после кликов расходилась с центром — показывала другое место графа.
// Любое расхождение здесь читается как баг синхронизации, поэтому решения о том,
// ЧТО показывать, принимает только стор (lib/codeGraph.ts).
//
// Разворачивает документ ТОЛЬКО кнопка в углу: иначе первый же клик по узлу
// выбрасывал бы документ поверх чата.
import { useMemo } from 'react';
import { Maximize2, Minimize2 } from 'lucide-react';
import { C, SP } from '../../lib/design';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions } from '../../lib/codeGraph';
import { buildFocusModel, graphDegree } from './graphFocus';
import { buildOverviewScene, layoutOverview, defaultExpandedGroups, type OverviewItem } from './graphOverview';
import { CodeGraphFocusCanvas, CodeGraphOverviewCanvas } from './CodeGraphCanvas';
import { CodeGraphNavBar } from './CodeGraphNav';

// Плотность карты: на 340px ширины больше восьми кружков не расходятся — подписи
// групп начинают наезжать друг на друга. Остальные группы уходят в заглушку «+N мелких»
const PANEL_MAX_ITEMS = 8;
// Раскрытая до типов группа: в полосе панели показываем только верхушку по связности
const PANEL_TYPES_LIMIT = 8;

export function CodeGraphMiniMap({ graphOpen, onExpand, onCollapse }: {
  // Открыт ли документ в центре: кнопка в углу карты переключается развернуть/свернуть,
  // оставаясь на одном месте — где открыл, там же и закрываешь
  graphOpen?: boolean;
  onExpand: () => void;
  onCollapse?: () => void;
}) {
  const s = useCodeGraph();
  const a = useCodeGraphActions();

  const degree = useMemo(() => (s.data ? graphDegree(s.data) : undefined), [s.data]);

  // Раскрытие — ровно то же, что считает документ: автоматический корень плюс
  // раскрытое пользователем. Своего правила у карты нет (см. шапку файла)
  const expanded = useMemo(() => {
    if (!s.data) return new Set<string>();
    const base = defaultExpandedGroups(s.data.nodes);
    for (const g of s.overviewExpanded) base.add(g);
    return base;
  }, [s.data, s.overviewExpanded]);

  // «Фокус» — окрестность выбранного типа. Показываем его и здесь: иначе клик по узлу
  // уводил бы центр в фокус, а карта оставалась в обзоре, и поверхности расходились
  const focus = useMemo(() => {
    if (!s.data || s.viewMode !== 'focus' || !s.selectedId) return null;
    return buildFocusModel(s.data, s.selectedId, {
      filters: s.filters,
      hideTests: s.hideTestNodes,
      depth2: s.focusDepth2,
      panel: true,
      degree,
    });
  }, [s.data, s.viewMode, s.selectedId, s.filters, s.hideTestNodes, s.focusDepth2, degree]);

  const scene = useMemo(() => {
    if (!s.data || s.viewMode !== 'overview') return null;
    return buildOverviewScene(s.data, {
      expanded,
      typesGroup: s.overviewTypesGroup,
      hideTests: s.hideTestNodes,
      filters: s.filters,
      maxItems: PANEL_MAX_ITEMS,
      typesLimit: PANEL_TYPES_LIMIT,
    });
  }, [s.data, s.viewMode, expanded, s.overviewTypesGroup, s.hideTestNodes, s.filters]);

  const layout = useMemo(() => (scene ? layoutOverview(scene, { size: 'panel' }) : null), [scene]);

  // Ключ анимации — как в документе: меняется на каждый шаг раскрытия
  const animKey = useMemo(
    () => s.navPath.filter(step => step.kind === 'group').map(g => `${g.group}:${g.drilled}`).join('>'),
    [s.navPath],
  );

  // Та же навигация, что у холста в центре: группа с подгруппами раскрывается на
  // уровень, листовая — сразу до типов, тип — выбирается (паспорт ниже в панели)
  const onItemClick = (it: OverviewItem) => {
    if (it.kind === 'node') { a.select(it.node!.id); return; }
    if (it.kind !== 'group') return;
    if (it.hasChildren && !expanded.has(it.group!)) a.expandGroup(it.group!);
    else a.drillOverviewTypes(it.group!);
  };
  const onItemDblClick = (it: OverviewItem) => {
    if (it.kind === 'node') { a.select(it.node!.id); return; }
    if (it.kind === 'group') a.drillOverviewTypes(it.group!);
  };

  if (!s.data) return null;

  const collapse = graphOpen && onCollapse;

  return (
    <div style={{ flexShrink: 0, display: 'flex', flexDirection: 'column' }}>
      {/* Навигация — та же, что в шапке документа: «Назад» и цепочка крошек */}
      <CodeGraphNavBar compact />

      <div style={{
        position: 'relative', borderBottom: `1px solid ${C.borderLight}`,
        background: C.bgMain, height: MINI_H, padding: `${SP.xs}px 0`,
      }}>
        {focus && (
          <CodeGraphFocusCanvas
            focus={focus}
            onRefocus={a.refocus}
            onClear={() => a.select(null)}
            onExpandTail={side => a.setFocusTail(side)}
          />
        )}
        {scene && layout && (
          <CodeGraphOverviewCanvas
            scene={scene} layout={layout} animKey={animKey}
            selectedId={s.selectedId}
            onItemClick={onItemClick} onItemDblClick={onItemDblClick}
          />
        )}
        {/* Вход в документ центра и выход из него — одна кнопка в одном углу.
            Сверху, а не снизу: нижний ряд слоёв («Прочее») почти всегда занят узлами */}
        <Button variant="ghostFilled" size="xs"
          onClick={collapse ? onCollapse : onExpand}
          title={collapse ? 'Свернуть граф к чату' : 'Открыть граф в центральной области'}
          style={{ position: 'absolute', right: SP.sm, top: SP.sm }}
          leftIcon={collapse
            ? <Minimize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            : <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
          {collapse ? 'Свернуть' : 'Развернуть'}
        </Button>
      </div>
    </div>
  );
}

// Высота полосы карты. Не доля высоты панели: карта — спутник инспектора, а не его
// конкурент, и на высокой панели должна отдавать место паспорту типа
const MINI_H = 230;
