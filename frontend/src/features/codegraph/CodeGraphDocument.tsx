// Документ «Граф зависимостей» в центральной зоне workspace — ведёт себя как
// открытый файл: та же модель «документ поверх чата», что у FileViewer (корневой
// flex-контейнер + Toolbar-шапка), крестик закрытия возвращает центр к чату.
// Оборачивается в centerIsland в DesktopWorkspace — как прочие документы центра.
// Состояния: построен (SVG-холст) / empty (404) / loading / building (сборка идёт,
// авто-polling стора) / error. isStale — мягкий warning-бейдж в шапке + «Перестроить»
// как главное действие. На мобиле документ на весь экран, режимы и паспорт — в нижней
// шторке по FAB.
import { useMemo, useState, useEffect } from 'react';
import { Network, RefreshCw, X, SlidersHorizontal, AlertTriangle } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { Button, WaitingIndicator, BackButton, EmptyState } from '../../components/ui';
import { Toolbar, ToolbarIconButton } from '../../components/Toolbar';
import { Modal } from '../../components/ui/Modal';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions } from '../../lib/codeGraph';
import { layoutGraph } from './graphLayout';
import { CodeGraphCanvas } from './CodeGraphCanvas';
import { CodeGraphPanel } from './CodeGraphPanel';

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
  const layout = useMemo(() => (s.data ? layoutGraph(s.data) : null), [s.data]);

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
        <Button variant={isStale ? 'primary' : 'secondary'} size="sm" pill
          style={{ flexShrink: 0 }}
          loading={s.status === 'loading' || s.status === 'building'}
          onClick={() => a.build(projectId)}
          leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          title="Перестроить граф">
          {!isMobile && 'Перестроить'}
        </Button>
        {/* Крестик закрытия — только на десктопе (на мобиле его роль у BackButton) */}
        {!isMobile && (
          <ToolbarIconButton isMobile={isMobile} onClick={onClose} title="Закрыть">
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        )}
      </Toolbar>

      {/* Тело документа */}
      <div style={{ flex: 1, minHeight: 0, position: 'relative', display: 'flex', flexDirection: 'column' }}>
        {(s.status === 'loading' || s.status === 'idle') && (
          <Center>
            <WaitingIndicator />
            <div style={{ ...serifTitle, marginTop: SP.md }}>Анализирую зависимости</div>
            <p style={centerNote}>Roslyn разбирает исходники, извлекает типы и рёбра между ними.</p>
          </Center>
        )}

        {s.status === 'building' && (
          <Center>
            <WaitingIndicator hint="Сборка займёт около минуты — граф появится сам" />
            <div style={{ ...serifTitle, marginTop: SP.md }}>Строю граф зависимостей</div>
            <p style={centerNote}>Сборка запущена — документ обновится автоматически, закрывать его не нужно.</p>
          </Center>
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
            icon={<AlertTriangle size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Не удалось загрузить граф"
            subtitle={s.error ?? 'Повторите попытку позже.'}
            action={<Button variant="secondary" size="md" onClick={() => a.load(projectId, true)}>Повторить</Button>}
          />
        )}

        {s.status === 'ready' && layout && s.data && (
          <div style={{ flex: 1, minHeight: 0, position: 'relative' }}>
            <CodeGraphCanvas
              graph={s.data}
              layout={layout}
              filters={s.filters}
              selectedId={s.selectedId}
              query={s.query}
              onSelect={a.select}
              hideTestNodes={s.hideTestNodes}
              hideOrphanNodes={s.hideOrphanNodes}
            />
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
function Center({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: SP.md, padding: SP.xl, textAlign: 'center' }}>
      {children}
    </div>
  );
}

function MetaChip({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      fontSize: FS.xs, color: C.textSecondary, background: C.bgCard, border: `1px solid ${C.borderLight}`,
      borderRadius: R.max, padding: `${SP.xs} ${SP.sm}`, whiteSpace: 'nowrap', display: 'inline-flex', gap: 4, alignItems: 'center',
    }}>{children}</span>
  );
}

const monoBold: React.CSSProperties = { color: C.textHeading, fontWeight: 600, fontFamily: FONT.mono };
const serifTitle: React.CSSProperties = { fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading };
const centerNote: React.CSSProperties = { fontSize: FS.sm, color: C.textSecondary, maxWidth: 400, lineHeight: 1.55 };
