// Документ «Граф зависимостей» в центральной зоне workspace — ведёт себя как
// открытый файл: та же модель «документ поверх чата», что у FileViewer (корневой
// flex-контейнер + Toolbar-шапка), крестик закрытия возвращает центр к чату.
// Оборачивается в centerIsland в DesktopWorkspace — как прочие документы центра.
// Состояния: построен (SVG-холст) / empty (404) / loading / building (сборка идёт,
// авто-polling стора) / error. isStale — мягкий warning-бейдж в шапке + «Перестроить»
// как главное действие. На мобиле документ на весь экран, паспорт — в нижней шторке по FAB.
//
// Навигация — единая цепочка крошек, не тумблер режимов: документ открывается сразу
// в «Обзоре» (граф групп неймспейсов по слоям), а «Фокус» (окрестность одного типа) —
// не отдельная вкладка, а место, куда приводит клик по типу. Крошки одинаково понимают
// оба вида шагов (группа → обзор с раскрытием, тип → фокус) — см. lib/codeGraph.ts.
import { useMemo, useState, useEffect } from 'react';
import { Network, RefreshCw, X, SlidersHorizontal, AlertTriangle, Loader, Unlink } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { Button, WaitingIndicator, BackButton, EmptyState } from '../../components/ui';
import { Toolbar, ToolbarIconButton } from '../../components/Toolbar';
import { Modal } from '../../components/ui/Modal';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions } from '../../lib/codeGraph';
import { graphDegree } from './graphFocus';
import { buildFocusModel } from './graphFocus';
import { buildOverviewScene, layoutOverview, defaultExpandedGroups, type OverviewItem } from './graphOverview';
import { CodeGraphFocusCanvas, CodeGraphOverviewCanvas } from './CodeGraphCanvas';
import { CodeGraphPanel } from './CodeGraphPanel';
import { CodeGraphNavBar } from './CodeGraphNav';

interface Props {
  projectId: string;
  isMobile: boolean;
  onClose: () => void;
  onOpenFile: (path: string) => void;
  onBuild: () => void;
}

// Форматирование времени сборки: ISO → «27.07 14:02» (компактно для шапки)
function formatBuiltAt(iso?: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (isNaN(d.getTime())) return null;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function CodeGraphDocument({ projectId, isMobile, onClose, onOpenFile, onBuild }: Props) {
  const s = useCodeGraph();
  const a = useCodeGraphActions();
  const [sheetOpen, setSheetOpen] = useState(false);
  const degree = useMemo(() => (s.data ? graphDegree(s.data) : undefined), [s.data]);

  // «Фокус»: окрестность выбранного типа — крошки, счётчик и сам холст рисуют одну
  // и ту же модель, второй раз её считать незачем
  const focus = useMemo(() => {
    if (!s.data || !s.selectedId) return null;
    return buildFocusModel(s.data, s.selectedId, {
      filters: s.filters,
      hideTests: s.hideTestNodes,
      depth2: s.focusDepth2,
      mobile: isMobile,
      degree,
    });
  }, [s.data, s.selectedId, s.filters, s.hideTestNodes, s.focusDepth2, isMobile, degree]);

  // «Обзор»: холст показывает группы неймспейсов по слоям, не типы.
  // Раскрытые пользователем группы — сверх автоматически раскрытого общего корня.
  const overviewExpanded = useMemo(() => {
    if (!s.data) return new Set<string>();
    const base = defaultExpandedGroups(s.data.nodes);
    for (const g of s.overviewExpanded) base.add(g);
    return base;
  }, [s.data, s.overviewExpanded]);

  const overviewScene = useMemo(() => {
    if (!s.data || s.viewMode !== 'overview') return null;
    return buildOverviewScene(s.data, {
      expanded: overviewExpanded,
      typesGroup: s.overviewTypesGroup,
      hideTests: s.hideTestNodes,
      filters: s.filters,
      typesLimit: isMobile ? 12 : 30,
    });
  }, [s.data, s.viewMode, overviewExpanded, s.overviewTypesGroup, s.hideTestNodes, s.filters, isMobile]);

  const overviewLayout = useMemo(
    () => (overviewScene ? layoutOverview(overviewScene, { size: isMobile ? 'mobile' : 'desktop' }) : null),
    [overviewScene, isMobile],
  );

  // Ключ анимации перехода «Обзора»: меняется при любой смене раскрытия — не завязан
  // на identity объектов сцены/раскладки (те пересоздаются на каждый рендер)
  const overviewAnimKey = useMemo(
    () => s.navPath.filter(step => step.kind === 'group').map(g => `${g.group}:${g.drilled}`).join('>'),
    [s.navPath],
  );

  // Клик по группе «Обзора»: есть куда раскрыть глубже — раскрываем на уровень, иначе —
  // сразу до типов. Двойной клик — всегда до типов. Клик по типу — переход в «Фокус»
  // (сквозной вход: цепочка группа-шагов пересчитывается заново от корня к типу).
  const onOverviewItemClick = (it: OverviewItem) => {
    if (it.kind === 'node') { a.select(it.node!.id); return; }
    if (it.kind !== 'group') return;
    if (it.hasChildren && !overviewExpanded.has(it.group!)) a.expandGroup(it.group!);
    else a.drillOverviewTypes(it.group!);
  };
  const onOverviewItemDblClick = (it: OverviewItem) => {
    if (it.kind === 'node') { a.select(it.node!.id); return; }
    if (it.kind === 'group') a.drillOverviewTypes(it.group!);
  };

  // Документ сам запускает загрузку при монтировании (мобила: граф открывается
  // из меню «⋯» без панели рельсы, которая обычно триггерит load). Идемпотентно.
  useEffect(() => { a.load(projectId); }, [a, projectId]);

  // Число активных режимов для бейджа FAB: сколько фильтров снято + поиск + выбор
  const modesBadge = useMemo(() => {
    let n = 0;
    if (!s.filters.Calls) n++;
    if (!s.filters.Implements) n++;
    if (!s.filters.References) n++;
    if (s.query.trim()) n++;
    if (s.selectedId) n++;
    return n;
  }, [s.filters, s.query, s.selectedId]);

  const meta = s.data?.metadata;
  const built = formatBuiltAt(meta?.builtAt);
  const isStale = !!meta?.isStale;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard, position: 'relative' }}>
      {/* Шапка — как у FileViewer: Toolbar с заголовком, действиями и крестиком закрытия */}
      <Toolbar isMobile={isMobile}>
        {/* Мобила: «назад» к чату (паттерн «список → документ», как у файла) */}
        {isMobile && (
          <BackButton onClick={onClose} title="К чату" style={{ height: 32 }}>
            <span style={{ fontSize: 13, fontWeight: 600, color: C.textSecondary }}>Чат</span>
          </BackButton>
        )}
        {/* Закрыть — слева, как у открытого файла: у документов центра одно место
            выхода, и искать его в разных концах шапки не приходится */}
        {!isMobile && (
          <ToolbarIconButton isMobile={isMobile} onClick={onClose} title="Закрыть">
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        )}
        <Network size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />
        <span style={{ fontFamily: FONT.sans, fontWeight: 600, fontSize: 14, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Граф зависимостей
        </span>

        {/* Метаданные сборки — только когда есть что показать и место (не мобила) */}
        {!isMobile && meta && (
          <>
            <MetaChip><b style={monoBold}>{meta.nodeCount}</b> типов</MetaChip>
            <MetaChip><b style={monoBold}>{meta.edgeCount}</b> рёбер</MetaChip>
            {built && <MetaChip>собрано <b style={monoBold}>{built}</b></MetaChip>}
          </>
        )}
        {isStale && (
          <span title="Код изменился после сборки — граф может отставать от реальности"
            style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: FS.xs, fontWeight: 600, padding: `${SP.xs} ${SP.sm}`, borderRadius: R.max, background: C.warningBg, color: C.warningText, cursor: 'help', flexShrink: 0 }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />устаревает
          </span>
        )}
        {/* Главное действие документа — той же формой, что «Править»/«Сохранить»
            у открытого файла: залитая кнопка size="sm" с иконкой и подписью */}
        <Button variant="primary" size="sm"
          style={{ flexShrink: 0, whiteSpace: 'nowrap' }}
          loading={s.status === 'loading' || s.status === 'building'}
          onClick={() => a.build(projectId)}
          leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          title="Перестроить граф">
          {!isMobile && 'Перестроить'}
        </Button>
      </Toolbar>

      {/* Тело документа */}
      <div style={{ flex: 1, minHeight: 0, position: 'relative', display: 'flex', flexDirection: 'column' }}>
        {/* Ожидание — тем же EmptyState, что empty/error ниже: в одном документе
            состояния обязаны говорить одним голосом */}
        {(s.status === 'loading' || s.status === 'idle') && (
          <EmptyState
            icon={<Loader size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Анализирую зависимости"
            subtitle="Roslyn разбирает исходники, извлекает типы и рёбра между ними."
            action={<WaitingIndicator />}
          />
        )}

        {s.status === 'building' && (
          <EmptyState
            icon={<Loader size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Строю граф зависимостей"
            subtitle="Сборка запущена — документ обновится автоматически, закрывать его не нужно."
            action={<WaitingIndicator hint="Сборка займёт около минуты — граф появится сам" />}
          />
        )}

        {s.status === 'empty' && (
          <EmptyState
            icon={<Network size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Граф ещё не построен"
            subtitle="Code Graph анализирует типы и связи между ними. Первичная сборка займёт около минуты, затем граф обновляется при каждом изменении кода."
            action={<Button variant="primary" size="md" onClick={onBuild} leftIcon={<Network size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Построить граф</Button>}
          />
        )}

        {s.status === 'error' && (
          <EmptyState
            icon={<Unlink size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Не удалось загрузить граф"
            subtitle={s.error ?? 'Повторите попытку позже.'}
            action={<Button variant="secondary" size="md" onClick={() => a.load(projectId, true)}>Повторить</Button>}
          />
        )}

        {s.status === 'ready' && s.data && (
          <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            {/* Навигация — общий компонент с картой в панели: «Назад» — один шаг,
                клик по ступени — возврат ровно на неё, всё правее отбрасывается */}
            <CodeGraphNavBar isMobile={isMobile}
              trailing={!isMobile && focus
                ? (
                  <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, paddingLeft: SP.sm }}>
                    {focus.center.fullyQualifiedName}
                  </span>
                )
                : undefined} />

            {/* Полотно холста — фон карточки: в центре граф читается как документ,
                а не как отдельная поверхность внутри острова */}
            <div style={{ flex: 1, minHeight: 0, position: 'relative', background: C.bgCard }}>
            {s.viewMode === 'focus' && focus && (
              <CodeGraphFocusCanvas
                focus={focus}
                onRefocus={a.refocus}
                onClear={() => a.select(null)}
                onExpandTail={side => { a.setFocusTail(side); if (isMobile) setSheetOpen(true); }}
              />
            )}
            {s.viewMode === 'overview' && overviewScene && overviewLayout && (
              <CodeGraphOverviewCanvas
                scene={overviewScene}
                layout={overviewLayout}
                animKey={overviewAnimKey}
                onItemClick={onOverviewItemClick}
                onItemDblClick={onOverviewItemDblClick}
              />
            )}
            {/* Мобила: FAB режимов/паспорта с бейджем числа активных фильтров */}
            {isMobile && (
              <button onClick={() => setSheetOpen(true)} title="Режимы и паспорт графа"
                style={{
                  position: 'absolute', right: SP.md, bottom: SP.md, width: 44, height: 44, borderRadius: R.full,
                  background: C.accent, color: C.onAccent, boxShadow: SHADOW.fab, cursor: 'pointer', border: 'none',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}>
                <SlidersHorizontal size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
                {modesBadge > 0 && (
                  <span style={{
                    position: 'absolute', top: -4, right: -4, minWidth: 16, height: 16, padding: `0 ${SP.xs}`,
                    borderRadius: R.max, background: C.bgCard, color: C.accent, border: `1px solid ${C.accentMuted}`,
                    fontSize: 9, fontWeight: 700, fontFamily: FONT.sans, lineHeight: '16px', textAlign: 'center',
                  }}>{modesBadge}</span>
                )}
              </button>
            )}
            </div>

            {/* Счётчик фокуса: честно проговаривает, сколько показано и сколько скрыто */}
            {s.viewMode === 'focus' && focus && (
              <StatusBar>
                {`Показано ${focus.shownCount} из ${s.data.nodes.length} узлов · глубина ${s.focusDepth2 ? 2 : 1}`}
                {s.focusDepth2 && ` (второе кольцо: ${focus.secondShown} из ${focus.secondTotal}, остальные скрыты)`}
                {` · связей у центра: ${focus.centerDegree}`}
                {(focus.incoming.length > focus.limit || focus.outgoing.length > focus.limit)
                  && ` · в заглушках: ${Math.max(0, focus.incoming.length - focus.limit) + Math.max(0, focus.outgoing.length - focus.limit)}`}
              </StatusBar>
            )}

            {/* Счётчик обзора: сколько элементов на холсте и сколько типов/связей за ними стоит */}
            {s.viewMode === 'overview' && overviewScene && (
              <StatusBar>
                {(() => {
                  const groups = overviewScene.items.filter(it => it.kind !== 'node').length;
                  const types = overviewScene.items.filter(it => it.kind === 'node').length;
                  return `На холсте ${overviewScene.items.length} элементов (${groups} групп + ${types} типов) · `
                    + `${overviewScene.totalTypeCount - types} типов свёрнуты в группы · ${overviewScene.bundles.length} пучков связей`;
                })()}
              </StatusBar>
            )}
          </div>
        )}
      </div>

      {/* Мобила: нижняя шторка с режимами и паспортом (панель рельсы не видна на мобиле) */}
      {isMobile && sheetOpen && (
        <Modal onClose={() => setSheetOpen(false)} title="Граф">
          <CodeGraphPanel projectId={projectId} graphOpen onEnsureGraphOpen={() => {}} onOpenFile={onOpenFile} onBuild={onBuild} />
        </Modal>
      )}
    </div>
  );
}

// === Мелкие презентационные куски ===

function MetaChip({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      fontSize: FS.xs, color: C.textSecondary, background: C.bgCard, border: `1px solid ${C.borderLight}`,
      borderRadius: R.max, padding: `${SP.xs} ${SP.sm}`, whiteSpace: 'nowrap', display: 'inline-flex', gap: 4, alignItems: 'center',
    }}>{children}</span>
  );
}

// Строка состояния под холстом: что именно сейчас показано и что скрыто. Обычным
// текстом, а не моноширинной телеметрией — это подпись к картинке, а не лог
function StatusBar({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      flexShrink: 0, padding: `${SP.xs}px ${SP.md}px`, borderTop: `1px solid ${C.borderLight}`,
      background: C.bgInset, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
      overflowX: 'auto', whiteSpace: 'nowrap',
    }}>{children}</div>
  );
}

const monoBold: React.CSSProperties = { color: C.textHeading, fontWeight: 600, fontFamily: FONT.mono };
