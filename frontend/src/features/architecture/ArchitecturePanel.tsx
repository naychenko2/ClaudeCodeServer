// Панель «Архитектура» в рельсе проекта — навигатор по C4-модели: поиск и элементы по
// уровням. Холст в 280–400px не живёт, поэтому сам редактор — документ в центре
// (ArchitectureDocument), панель его открывает и фокусирует элемент (focus через мост).
// Схема «панель + документ» — как у «Графа».
import { useEffect, useMemo, useState } from 'react';
import { DraftingCompass, RefreshCw, Search, Loader, Unlink, Sparkles } from 'lucide-react';
import {
  C, FONT, FS, R, SP, Button, IconButton, IconField, EmptyState, WaitingIndicator, PanelHeaderSlot, useHasPanelHeader,
  ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import { useArchitecture, loadArchitecture, generateArchitecture, requestFocus } from './architectureStore';
import { parseOutline, LEVEL_LABEL, type ArchLevel } from './modelOutline';

interface Props {
  projectId: string;
  archOpen: boolean;
  onEnsureOpen: () => void;
  onCollapse: () => void;
}

const LEVELS: ArchLevel[] = ['system', 'container', 'component', 'code'];

export function ArchitecturePanel({ projectId, archOpen, onEnsureOpen, onCollapse }: Props) {
  const s = useArchitecture();
  const inHeader = useHasPanelHeader();
  const [query, setQuery] = useState('');

  useEffect(() => { void loadArchitecture(projectId); }, [projectId]);

  const elements = useMemo(() => parseOutline(s.localValue ?? s.content), [s.localValue, s.content]);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return elements;
    return elements.filter(e => e.name.toLowerCase().includes(q) || (e.technology ?? '').toLowerCase().includes(q));
  }, [elements, query]);

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

  if (s.status === 'missing') {
    return (
      <>{header}<EmptyState compact
        icon={<DraftingCompass size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Архитектура ещё не описана"
        subtitle="Соберу стартовую C4-модель из кода проекта."
        action={(
          <Button variant="primary" size="sm" fullWidth loading={s.generating}
            onClick={() => { onEnsureOpen(); void generateArchitecture(projectId); }}
            leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Собрать из кода</Button>
        )}
      /></>
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
        {filtered.length === 0 && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px` }}>
            {elements.length === 0 ? 'В модели пока нет элементов — откройте холст и добавьте их.' : 'Ничего не нашлось.'}
          </div>
        )}
        {LEVELS.map(level => {
          const list = filtered.filter(e => e.level === level);
          if (!list.length) return null;
          return (
            <div key={level} style={{ marginBottom: SP.sm }}>
              <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.4px', padding: `${SP.xs}px ${SP.sm}px` }}>
                {LEVEL_LABEL[level]} · {list.length}
              </div>
              {list.map(el => (
                <Row key={el.id} onClick={() => { onEnsureOpen(); requestFocus(el.id); }}
                  title={el.description ?? el.name}>
                  <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{el.name}</span>
                  {el.technology && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{el.technology}</span>}
                </Row>
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Row({ onClick, title, children }: { onClick: () => void; title?: string; children: React.ReactNode }) {
  const [hover, setHover] = useState(false);
  return (
    <button type="button" onClick={onClick} title={title}
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', textAlign: 'left',
        padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.lg, border: 'none', cursor: 'pointer',
        background: hover ? C.bgInset : 'transparent', fontFamily: FONT.sans,
      }}>
      {children}
    </button>
  );
}
