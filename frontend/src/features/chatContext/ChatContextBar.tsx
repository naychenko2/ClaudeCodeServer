// Полоса контекста чата (фича chat-context, эскиз B2 §2): материалы, приложенные
// к чату явной кнопкой, — вкладками над правой половиной сплита (variant 'tabs')
// или чипами в шапке чата, когда правая половина закрыта (variant 'chips').
//
// Компонент НЕ маршрутизирует: клик отдаёт запись наружу (onOpen), а владелец
// экрана ведёт её существующим путём (open-file / readerActions / задача-aside).
// Активная вкладка тоже приходит снаружи — она вычисляется из состояния центра,
// а не хранится здесь: собственное «что открыто» разошлось бы с настоящим.
import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import { AlertTriangle, Globe, ListTodo, MoreHorizontal, Search, SquareStack, X } from 'lucide-react';
import type { SessionContextEntry, SessionContextType } from '../../types';
import { C, FONT, FS, R, SP, MODAL_W } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { IconButton, Menu, MenuItem, Modal, ModalActions, TextField, FileTypeTile } from '../../components/ui';
import { AttachPicker } from '../../components/chat/AttachPicker';
import { basename, middleEllipsis } from '../../lib/paths';
import { showToast } from '../../lib/toast';
import { useTasks } from '../../lib/tasks';
import {
  loadChatContext, removeFromChatContext, replaceChatContextEntry, useChatContext, contextKey,
} from '../../lib/chatContext';

// Габариты чипа (эскиз §2.2) — единственные литералы этого файла: это размеры
// самого элемента, а не отступы сетки
const CHIP = {
  desktop: { h: 28, pad: '0 10px 0 8px', maxW: 180 },
  mobile:  { h: 40, pad: '0 12px 0 10px', maxW: 148 },
} as const;

export interface ContextTarget { type: SessionContextType; id: string }

interface Props {
  projectId: string;
  sessionId: string;
  variant: 'tabs' | 'chips';
  // Что сейчас открыто в центре — из него и вычисляется активная вкладка
  // (в 'chips' активной не бывает: правая половина закрыта)
  active?: ContextTarget | null;
  onOpen: (entry: SessionContextEntry) => void;
  // Мобила: чип и кнопка «⋯» растут до 40 (чек-лист гайда, п.4). Планшет —
  // случай посередине: чип десктопной высоты, но кнопка тач-размера
  isMobile?: boolean;
  isTablet?: boolean;
}

// Подпись чипа: файл — имя, ссылка — домен, задача — заголовок (свежий из стора,
// сохранённый в записи — запасной: задачу могли переименовать после добавления)
function entryLabel(e: SessionContextEntry, taskTitle?: string): string {
  if (e.type === 'file') return basename(e.id);
  if (e.type === 'url') { try { return new URL(e.id).hostname; } catch { return e.id; } }
  return taskTitle || e.title || 'Задача';
}

function entryIcon(e: SessionContextEntry): ReactNode {
  if (e.missing) return <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  if (e.type === 'file') return <FileTypeTile name={basename(e.id)} />;
  if (e.type === 'url') return <Globe size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />;
  return <ListTodo size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />;
}

export function ChatContextBar({ projectId, sessionId, variant, active, onOpen, isMobile, isTablet }: Props) {
  const list = useChatContext(sessionId);
  // Заголовки задач берём из общего стора: там же они обновляются при переименовании
  const tasks = useTasks();
  const [allMenu, setAllMenu] = useState<DOMRect | null>(null);
  const [missMenu, setMissMenu] = useState<{ anchor: DOMRect; entry: SessionContextEntry } | null>(null);
  // Диалог «Указать заново…» для ненайденного материала
  const [repoint, setRepoint] = useState<SessionContextEntry | null>(null);

  // Первичная загрузка состава: полоса — первый, кто его спрашивает при входе в чат
  useEffect(() => { void loadChatContext(projectId, sessionId); }, [projectId, sessionId]);
  // Меню принадлежат конкретному чату — при переключении закрываем
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс попапов при смене чата
  useEffect(() => { setAllMenu(null); setMissMenu(null); setRepoint(null); }, [sessionId]);

  const taskTitle = (e: SessionContextEntry) =>
    e.type === 'task' ? tasks.find(t => t.id === e.id)?.title : undefined;

  const remove = (e: SessionContextEntry) => {
    const name = entryLabel(e, taskTitle(e));
    void removeFromChatContext(projectId, sessionId, e.type, e.id)
      .then(() => showToast('Контекст чата', `Убрано: ${name}`))
      .catch(() => showToast('Контекст чата', 'Не удалось убрать материал', 'info'));
  };

  // Пустой контекст — полосы нет вовсе: шапка чата не меняется ни на пиксель
  if (!list || list.length === 0) return null;

  const size = isMobile ? CHIP.mobile : CHIP.desktop;
  const chip = (e: SessionContextEntry) => {
    const label = entryLabel(e, taskTitle(e));
    const isActive = variant === 'tabs' && !!active
      && contextKey(active.type, active.id) === contextKey(e.type, e.id);
    const base: CSSProperties = {
      display: 'flex', alignItems: 'center', gap: SP.xs + 2, flexShrink: 0,
      height: size.h, maxWidth: size.maxW, padding: size.pad,
      border: 'none', borderRadius: R.md, cursor: 'pointer',
      fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
      background: e.missing ? C.warningBg : isActive ? C.bgWhite : C.bgInset,
      color: e.missing ? C.warningText : isActive ? C.textHeading : C.textSecondary,
      // Активная вкладка отделена от тела острова рамкой и подчёркнута акцентом
      ...(isActive ? { boxShadow: `inset 0 0 0 1px ${C.border}, inset 0 -2px 0 0 ${C.accent}` } : null),
      position: 'relative',
    };
    const title = e.missing
      ? 'Материал не найден — путь изменился или файл удалён'
      : e.type === 'task' ? label : e.id;
    const open = (rect: DOMRect) => {
      // Ненайденный материал открывать нечем — предлагаем убрать или переуказать
      if (e.missing) setMissMenu({ anchor: rect, entry: e });
      else onOpen(e);
    };
    return (
      <div key={contextKey(e.type, e.id)} style={base}
        title={title}
        onClick={ev => open((ev.currentTarget as HTMLElement).getBoundingClientRect())}
        onContextMenu={ev => {
          ev.preventDefault();
          setMissMenu({ anchor: (ev.currentTarget as HTMLElement).getBoundingClientRect(), entry: e });
        }}
      >
        {entryIcon(e)}
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {middleEllipsis(label, isMobile ? 16 : 24)}
        </span>
        {/* Крестик — только у активной вкладки: в чипах счёт идёт на пиксели,
            и удаление там живёт в меню «⋯» и в контекстном меню чипа */}
        {isActive && (
          <IconButton
            size="xs"
            onClick={ev => { ev.stopPropagation(); remove(e); }}
            title="Убрать из контекста"
          >
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>
    );
  };

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
      {/* Якорь — он же подпись полосы: текстовое «Контекст» на 360 CSS съело бы
          место у самих чипов */}
      <SquareStack
        size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textMuted}
        style={{ flexShrink: 0 }}
        aria-label="Контекст чата"
      />
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.xs + 2, flex: 1, minWidth: 0,
        flexWrap: 'nowrap', overflowX: 'auto', scrollbarWidth: 'thin',
      }}>
        {list.map(chip)}
      </div>
      <span style={{ flexShrink: 0, display: 'flex', position: 'relative' }}>
        <IconButton
          size={isMobile ? 'lg' : isTablet ? 'md' : 'sm'}
          title="Все материалы контекста"
          onClick={ev => setAllMenu((ev.currentTarget as HTMLElement).getBoundingClientRect())}
        >
          <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </span>

      {/* Весь состав списком: на узком экране это единственный путь к материалам,
          уехавшим за край ряда */}
      {allMenu && (
        <Menu anchor={allMenu} minWidth={240} maxHeight={340} onClose={() => setAllMenu(null)}>
          {list.map(e => (
            <MenuItem
              key={contextKey(e.type, e.id)}
              icon={entryIcon(e)}
              label={entryLabel(e, taskTitle(e))}
              isMobile={isMobile}
              onClick={() => {
                setAllMenu(null);
                if (e.missing) setMissMenu({ anchor: allMenu, entry: e }); else onOpen(e);
              }}
              action={{
                icon: <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
                title: 'Убрать из контекста',
                onClick: () => { setAllMenu(null); remove(e); },
              }}
            />
          ))}
        </Menu>
      )}

      {/* Ненайденный материал — молча не чистим: человек решает сам */}
      {missMenu && (
        <Menu anchor={missMenu.anchor} minWidth={220} maxHeight={160} onClose={() => setMissMenu(null)}>
          <MenuItem
            icon={<X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Убрать из контекста"
            isMobile={isMobile}
            onClick={() => { const e = missMenu.entry; setMissMenu(null); remove(e); }}
          />
          {missMenu.entry.missing && (
            <MenuItem
              icon={<Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label="Указать заново…"
              isMobile={isMobile}
              onClick={() => { const e = missMenu.entry; setMissMenu(null); setRepoint(e); }}
            />
          )}
        </Menu>
      )}

      {repoint && (
        <RepointDialog
          projectId={projectId} sessionId={sessionId} entry={repoint}
          onClose={() => setRepoint(null)}
        />
      )}
    </div>
  );
}

// «Указать заново…»: замена адреса записи по месту. Форма зависит от типа —
// файл выбирается существующим пикером вложений, задача списком задач проекта,
// ссылка правится текстом
function RepointDialog({ projectId, sessionId, entry, onClose }: {
  projectId: string; sessionId: string; entry: SessionContextEntry; onClose: () => void;
}) {
  const tasks = useTasks();
  const [url, setUrl] = useState(entry.type === 'url' ? entry.id : '');
  const [query, setQuery] = useState('');

  const apply = (next: { type: SessionContextType; id: string; title?: string | null }) => {
    void replaceChatContextEntry(projectId, sessionId, entry.type, entry.id, next)
      .then(() => showToast('Контекст чата', 'Материал переуказан'))
      .catch(() => showToast('Контекст чата', 'Не удалось переуказать материал', 'info'));
    onClose();
  };

  if (entry.type === 'file') {
    return (
      <AttachPicker
        projectId={projectId}
        title="Указать материал заново"
        selected={[]}
        onToggle={path => apply({ type: 'file', id: path, title: basename(path) })}
        onClose={onClose}
      />
    );
  }

  if (entry.type === 'url') {
    return (
      <Modal
        width={MODAL_W.form} title="Указать заново" subtitle="Адрес страницы" onClose={onClose}
        footer={<ModalActions confirmLabel="Сохранить" confirmDisabled={!url.trim()}
          onConfirm={() => apply({ type: 'url', id: url.trim(), title: entry.title })} onCancel={onClose} />}
      >
        <TextField value={url} onChange={setUrl} placeholder="https://…" autoFocus
          onEnter={() => { if (url.trim()) apply({ type: 'url', id: url.trim(), title: entry.title }); }}
          onEscape={onClose} />
      </Modal>
    );
  }

  const found = tasks.filter(t => t.projectId === projectId
    && (!query.trim() || t.title.toLowerCase().includes(query.trim().toLowerCase())));
  return (
    <Modal width={MODAL_W.form} title="Указать заново" subtitle="Задача проекта" onClose={onClose}>
      <TextField type="search" value={query} onChange={setQuery} placeholder="Поиск по названию" autoFocus onEscape={onClose} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, maxHeight: 320, overflowY: 'auto', marginTop: SP.sm }}>
        {found.length === 0 && (
          <div style={{ fontFamily: FONT.sans, fontSize: FS.base, color: C.textMuted, padding: `${SP.sm}px 0` }}>
            Задач не нашлось
          </div>
        )}
        {found.map(t => (
          <button
            key={t.id} type="button"
            onClick={() => apply({ type: 'task', id: t.id, title: t.title })}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
              background: 'none', border: 'none', borderRadius: R.md, padding: `${SP.sm}px ${SP.sm}px`,
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
            }}
          >
            <ListTodo size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{t.title}</span>
          </button>
        ))}
      </div>
    </Modal>
  );
}
