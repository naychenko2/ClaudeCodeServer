// Панель «Архитектура» в рельсе проекта — навигатор по C4-модели: поиск и дерево
// «система → контейнеры → компоненты». Холст в 280–400px не живёт, поэтому сам редактор —
// документ в центре (ArchitectureDocument), панель его открывает и фокусирует элемент
// (focus через мост). Схема «панель + документ» — как у «Графа».
import { useEffect, useMemo, useState } from 'react';
import { DraftingCompass, RefreshCw, Search, Loader, Unlink, Sparkles, ChevronRight } from 'lucide-react';
import {
  C, FONT, FS, R, SP, Button, IconButton, IconField, EmptyState, WaitingIndicator, PanelHeaderSlot, useHasPanelHeader,
  ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import { useArchitecture, loadArchitecture, generateArchitecture, startBlank, requestFocus } from './architectureStore';
import { agentBlockedHint, useAgentBuild, useAgentPref } from './ArchitectureBuild';
import { ArchitectureCreateDialog } from './ArchitectureCreateDialog';
import { parseOutline, buildOutlineTree, outlinePath, type OutlineNode } from './modelOutline';

interface Props {
  projectId: string;
  archOpen: boolean;
  onEnsureOpen: () => void;
  onCollapse: () => void;
}

// Ключ свёрнутой группы внешних систем в наборе раскрытых веток
const EXTERNAL_GROUP = '__external__';
const OPEN_KEY_PREFIX = 'cc_arch_panel_open:';

// Раскрытые ветки дерева по проекту. Ничего не сохранено — по умолчанию раскрыты
// внутренние системы (видны контейнеры), компоненты и внешние системы свёрнуты
function useOpenBranches(projectId: string, defaults: string[]) {
  const [stored, setStored] = useState<Set<string> | null>(() => loadOpen(projectId));
  useEffect(() => { setStored(loadOpen(projectId)); }, [projectId]);
  const open = stored ?? new Set(defaults);
  const toggle = (id: string) => {
    const next = new Set(open);
    if (next.has(id)) next.delete(id); else next.add(id);
    try { localStorage.setItem(OPEN_KEY_PREFIX + projectId, JSON.stringify([...next])); } catch { /* квота */ }
    setStored(next);
  };
  return { open, toggle };
}

function loadOpen(projectId: string): Set<string> | null {
  try {
    const raw = localStorage.getItem(OPEN_KEY_PREFIX + projectId);
    const arr = raw ? JSON.parse(raw) : null;
    return Array.isArray(arr) ? new Set(arr.filter((x): x is string => typeof x === 'string')) : null;
  } catch {
    return null;
  }
}

export function ArchitecturePanel({ projectId, archOpen, onEnsureOpen, onCollapse }: Props) {
  const s = useArchitecture();
  const inHeader = useHasPanelHeader();
  const [query, setQuery] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [withAgent, setWithAgent] = useAgentPref(projectId);
  const agent = useAgentBuild(projectId, s.agent?.taskId ?? null);
  const agentHint = agentBlockedHint(agent.phase);
  const agentBusy = agentHint !== null;

  useEffect(() => { void loadArchitecture(projectId); }, [projectId]);

  const elements = useMemo(() => parseOutline(s.localValue ?? s.content), [s.localValue, s.content]);
  const tree = useMemo(() => buildOutlineTree(elements), [elements]);
  const internalRoots = tree.filter(n => !n.el.external);
  const externalRoots = tree.filter(n => n.el.external);
  const { open, toggle } = useOpenBranches(projectId, internalRoots.map(n => n.el.id));
  // Поиск — плоская выдача с путём предков: по дереву одноимённые «Controllers» не различить
  const q = query.trim().toLowerCase();
  const found = useMemo(() => {
    if (!q) return [];
    return elements
      .filter(e => [e.name, e.technology, e.description].some(v => (v ?? '').toLowerCase().includes(q)))
      .map(e => ({ el: e, path: outlinePath(elements, e.id) }));
  }, [elements, q]);
  const focus = (id: string) => { onEnsureOpen(); requestFocus(id); };

  const renderNode = (node: OutlineNode, depth: number): React.ReactNode => {
    const expanded = open.has(node.el.id);
    return (
      <div key={node.el.id}>
        <TreeRow depth={depth} name={node.el.name} technology={node.el.technology}
          title={node.el.description ?? node.el.name} childCount={node.children.length}
          expanded={expanded} onToggle={() => toggle(node.el.id)} onClick={() => focus(node.el.id)} />
        {expanded && node.children.map(c => renderNode(c, depth + 1))}
      </div>
    );
  };

  const refresh = (
    <IconButton size="xs" title="Обновить" onClick={() => void loadArchitecture(projectId, true)}>
      <RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </IconButton>
  );
  // Открытый холст — кнопка становится «Свернуть» без акцента: рядом уже горит кнопка рельсы.
  // ghostFilled, а не secondary: заливка secondary совпадает с подложкой панели, и кнопка
  // читалась как неактивная (смоук 26.09, решение Веры)
  const mainAction = archOpen
    ? <Button variant="ghostFilled" size="xs" onClick={onCollapse}>Свернуть холст</Button>
    : <Button variant="primary" size="xs" onClick={onEnsureOpen}>Открыть холст</Button>;

  const header = inHeader
    ? (<><PanelHeaderSlot>{refresh}</PanelHeaderSlot><PanelHeaderSlot pinned>{mainAction}</PanelHeaderSlot></>)
    : <div style={{ display: 'flex', gap: SP.xs, padding: `${SP.sm}px ${SP.md}px` }}>{refresh}{mainAction}</div>;

  if (s.projectId !== projectId || s.status === 'idle' || s.status === 'loading') {
    return (
      <>{header}<EmptyState compact
        icon={<Loader size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Загружаю архитектуру"
        subtitle="Читаю модель из проекта."
        action={<WaitingIndicator />}
      /></>
    );
  }

  if (s.status === 'error') {
    return (
      <>{header}<EmptyState compact
        icon={<Unlink size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Не удалось прочитать модель"
        subtitle={s.error ?? 'Повторите позже.'}
        action={<Button variant="secondary" size="sm" fullWidth onClick={() => void loadArchitecture(projectId, true)}>Повторить</Button>}
      /></>
    );
  }

  if (s.status === 'missing' && !s.blank) {
    return (
      <>{header}<EmptyState compact
        icon={<DraftingCompass size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Архитектура ещё не описана"
        subtitle="Соберу C4-модель из кода проекта или начну с пустого холста — выбор за вами."
        action={(
          <Button variant="primary" size="sm" fullWidth loading={s.generating}
            onClick={() => setCreateOpen(true)}
            leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Создать архитектуру</Button>
        )}
      />
      {createOpen && (
        <ArchitectureCreateDialog
          withAgent={withAgent} onWithAgentChange={setWithAgent} agentBusy={agentBusy} agentHint={agentHint}
          onClose={() => setCreateOpen(false)}
          onGenerate={() => { setCreateOpen(false); onEnsureOpen(); void generateArchitecture(projectId, withAgent && !agentBusy); }}
          onBlank={() => { setCreateOpen(false); onEnsureOpen(); startBlank(); }}
        />
      )}</>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
      {header}
      <div style={{ padding: `${SP.sm}px ${SP.md}px`, flexShrink: 0 }}>
        <IconField icon={<Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} />}
          value={query} onChange={setQuery} placeholder="Найти элемент" height={32} fontSize={FS.sm} radius={R.lg} />
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: `0 ${SP.md}px ${SP.md}px` }}>
        {(elements.length === 0 || (q && found.length === 0)) && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px` }}>
            {elements.length === 0 ? 'В модели пока нет элементов — откройте холст и добавьте их.' : 'Ничего не нашлось.'}
          </div>
        )}
        {q ? found.map(({ el, path }) => (
          <Row key={el.id} onClick={() => focus(el.id)} title={el.description ?? el.name}>
            <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
              <span style={nameStyle}>{el.name}</span>
              {path && <span style={{ ...nameStyle, fontWeight: 400, color: C.textMuted }}>{path}</span>}
            </span>
            {el.technology && <span style={techStyle}>{el.technology}</span>}
          </Row>
        )) : (<>
          {internalRoots.map(n => renderNode(n, 0))}
          {externalRoots.length > 0 && (
            <div style={{ marginTop: SP.sm }}>
              <TreeRow depth={0} name={`Внешние · ${externalRoots.length}`} muted
                childCount={externalRoots.length} expanded={open.has(EXTERNAL_GROUP)}
                onToggle={() => toggle(EXTERNAL_GROUP)} onClick={() => toggle(EXTERNAL_GROUP)} />
              {open.has(EXTERNAL_GROUP) && externalRoots.map(n => renderNode(n, 1))}
            </div>
          )}
        </>)}
      </div>
    </div>
  );
}

const nameStyle: React.CSSProperties = {
  fontSize: FS.xs, fontWeight: 600, color: C.textHeading, minWidth: 0,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const techStyle: React.CSSProperties = { fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 };

// Строка дерева: шеврон сворачивает ветку, клик по имени фокусирует элемент на холсте.
// У свёрнутой ветки справа число детей, у листа на месте шеврона — пустое место под выравнивание
function TreeRow({ depth, name, technology, title, childCount, expanded, muted, onToggle, onClick }: {
  depth: number; name: string; technology?: string; title?: string; childCount: number;
  expanded: boolean; muted?: boolean; onToggle: () => void; onClick: () => void;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 2, paddingLeft: depth * SP.md }}>
      {childCount > 0 ? (
        <IconButton size="xs" title={expanded ? 'Свернуть' : 'Развернуть'} onClick={onToggle}>
          <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ transform: expanded ? 'rotate(90deg)' : 'none', transition: 'transform .15s' }} />
        </IconButton>
      ) : <span style={{ width: 24, flexShrink: 0 }} />}
      <Row onClick={onClick} title={title}>
        <span style={{ ...nameStyle, flex: 1, ...(muted ? { color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.4px' } : null) }}>{name}</span>
        {!expanded && childCount > 0 && !muted && <span style={techStyle}>{childCount}</span>}
        {technology && <span style={techStyle}>{technology}</span>}
      </Row>
    </div>
  );
}

function Row({ onClick, title, children }: { onClick: () => void; title?: string; children: React.ReactNode }) {
  const [hover, setHover] = useState(false);
  return (
    <button type="button" onClick={onClick} title={title}
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, flex: 1, minWidth: 0, width: '100%', textAlign: 'left',
        padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.lg, border: 'none', cursor: 'pointer',
        background: hover ? C.bgInset : 'transparent', fontFamily: FONT.sans,
      }}>
      {children}
    </button>
  );
}
