// Темы чатов проекта — UI режима «Темы» списка чатов по спеке
// «UI-спека — режим «Темы» в списке чатов»: выбор темы чата, заголовок секции с меню
// темы, переименование и удаление темы, полоса прогресса разбора.
import { useEffect, useState } from 'react';
import { Check, ChevronDown, ChevronUp, MoreHorizontal, Pencil, Plus, Sparkles, Trash2 } from 'lucide-react';
import type { ProjectSubject, Session } from '../types';
import { C, FONT, FS, MODAL_W, SP } from '../lib/design';
import { Button, ConfirmDialog, Field, IconButton, Menu, MenuItem, MenuSep, Modal, ModalActions, ProgressBar, TextField, WaitingIndicator } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { ChatGroupHeader } from './ChatGroupHeader';
import { useIsTouch } from '../lib/breakpoints';
import { useUserScrollGate } from '../lib/userScrollGate';
import { chatCountWord } from '../lib/chatFilters';
import {
  SUBJECT_AI_MARK, SUBJECT_AI_TITLE, SUBJECTS_MAX_HUMAN, sortedSubjects, subjectNameError,
  type SubjectRunView,
} from '../lib/chatSubjects';

// Тема чата, как её видит человек: висячий id — «без темы»
export function subjectOf(s: Session, registry: ProjectSubject[]): ProjectSubject | null {
  return s.subjectId ? registry.find(t => t.id === s.subjectId) ?? null : null;
}

// Вторая строка пункта «Тема» в меню карточки: кто положил чат в тему
export function subjectHint(s: Session, registry: ProjectSubject[]): string {
  const t = subjectOf(s, registry);
  if (!t) return 'без темы';
  return s.subjectSource === 'ai' ? `${t.name} ${SUBJECT_AI_MARK}` : `${t.name} · закреплена`;
}

const statusLine = (s: Session, registry: ProjectSubject[]) =>
  s.subjectSource === 'human' ? 'Закреплено вами'
    : s.subjectSource === 'ai' && subjectOf(s, registry) ? `Разложил AI ${SUBJECT_AI_MARK}`
      : 'Ещё не разобран';

const mutedLine = (text: string) => (
  <div style={{ padding: '6px 10px', fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>{text}</div>
);

// === Тело выбора темы — одно на десктопное меню и мобильную шторку ===
export function SubjectPickerBody({ chat, registry, isMobile, autoFocusCreate, onPick, onCreate, onRelease }: {
  chat: Session;
  registry: ProjectSubject[];
  isMobile?: boolean;
  autoFocusCreate?: boolean;
  // id темы или null — «Без темы» (оба закрепляют выбор человеком)
  onPick: (subjectId: string | null) => void;
  onCreate: (name: string) => void;
  onRelease: () => void;
}) {
  const [draft, setDraft] = useState('');
  const current = subjectOf(chat, registry)?.id ?? null;
  const full = registry.length >= SUBJECTS_MAX_HUMAN;
  const error = draft.trim() ? subjectNameError(draft, registry) : null;
  const canCreate = !full && draft.trim().length > 0 && !error;
  const submit = () => { if (canCreate) onCreate(draft.trim()); };
  const check = (on: boolean) => on
    ? <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />
    : null;
  const row = (label: string, on: boolean) => (
    <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flex: 1, gap: SP.sm, minWidth: 0 }}>
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
      {check(on)}
    </span>
  );
  return (
    <div onClick={e => e.stopPropagation()}>
      {mutedLine(statusLine(chat, registry))}
      {/* Список прокручивается внутри: строка создания и «Вернуть автоматике» на виду при
          любом числе тем (потолок 50). На мобиле — 5,5 строк по 44: половинка подсказывает прокрутку.
          Десктоп — 180, а не больше: худший случай под потолком меню 360 (рамка 12, статус 28,
          разделители 2×9, поле 36, строка ошибки 28, «Вернуть автоматике» с подсказкой 56)
          оставляет списку ~182; со списком 220 последний пункт уходил под прокрутку меню */}
      <div style={{ maxHeight: isMobile ? 5.5 * 44 : 180, overflowY: 'auto' }}>
        {sortedSubjects(registry).map(t => (
          <MenuItem key={t.id} isMobile={isMobile} label={row(t.name, t.id === current)} onClick={() => onPick(t.id)} />
        ))}
        <MenuItem isMobile={isMobile} label={row('Без темы', current === null)} onClick={() => onPick(null)} />
      </div>
      <MenuSep />
      {/* Новая тема — заводится и сразу принимает чат одним действием */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: '2px 4px' }}>
        <TextField
          value={draft}
          onChange={setDraft}
          onEnter={submit}
          placeholder="Новая тема…"
          disabled={full}
          invalid={!!error}
          autoFocus={autoFocusCreate && !full}
          isMobile={isMobile}
          // Компактное поле — только в десктопном меню; на мобиле штатная высота вровень с кнопкой
          style={isMobile ? undefined : { padding: '6px 10px', fontSize: FS.sm }}
        />
        <Button
          size={isMobile ? 'md' : 'sm'}
          onClick={submit}
          disabled={!canCreate}
          title={canCreate ? 'Создать тему и перенести в неё чат' : error ?? 'Введите имя новой темы'}
          leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        >
          Создать
        </Button>
      </div>
      {full
        ? mutedLine(`В проекте уже ${SUBJECTS_MAX_HUMAN} тем — переименуйте или удалите лишнюю`)
        : error && (
          // Ошибка имени — цветом ошибки, как в диалоге переименования (Field error)
          <div style={{ padding: '6px 10px', fontSize: FS.xs, color: C.dangerText, fontFamily: FONT.sans }}>{error}</div>
        )}
      {chat.subjectSource === 'human' && (
        <>
          <MenuSep />
          <MenuItem
            isMobile={isMobile}
            icon={<Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Вернуть автоматике"
            hint="AI разложит чат при следующем проходе"
            onClick={onRelease}
          />
        </>
      )}
    </div>
  );
}

// === Выбор темы: десктоп — меню по якорю, мобила — шторка (поле ввода у края экрана
// под клавиатурой в меню неудобно) ===
export function SubjectPickMenu({ anchor, isMobile, onClose, ...body }: {
  anchor: DOMRect;
  isMobile: boolean;
  onClose: () => void;
} & Omit<Parameters<typeof SubjectPickerBody>[0], 'isMobile' | 'autoFocusCreate'>) {
  const isUserScroll = useUserScrollGate();
  const isTouch = useIsTouch();
  useEffect(() => {
    if (isMobile) return;
    // Как меню тегов: Esc и пользовательский скролл списка закрывают меню
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    const onScroll = () => { if (isUserScroll()) onClose(); };
    document.addEventListener('keydown', onKey);
    document.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('scroll', onScroll, true);
    };
  }, [isMobile, onClose, isUserScroll]);
  if (isMobile) {
    return (
      <Modal title="Тема чата" onClose={onClose}>
        <SubjectPickerBody {...body} isMobile />
      </Modal>
    );
  }
  return (
    <Menu onClose={onClose} anchor={anchor} maxHeight={360} minWidth={240}>
      <SubjectPickerBody {...body} autoFocusCreate={!isTouch} />
    </Menu>
  );
}

// === Переименование темы ===
export function SubjectRenameDialog({ subject, registry, onSave, onClose }: {
  subject: ProjectSubject;
  registry: ProjectSubject[];
  onSave: (name: string) => Promise<void>;
  onClose: () => void;
}) {
  const [name, setName] = useState(subject.name);
  const [busy, setBusy] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const error = subjectNameError(name, registry, subject.id);
  const unchanged = name.trim() === subject.name;
  const save = async () => {
    if (error || busy) return;
    if (unchanged) { onClose(); return; }
    setBusy(true);
    try {
      await onSave(name.trim());
      onClose();
    } catch (e) {
      setServerError(e instanceof Error ? e.message : 'Не удалось переименовать');
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal
      title="Переименовать тему"
      width={MODAL_W.confirm}
      onClose={onClose}
      footer={<ModalActions confirmLabel="Сохранить" loading={busy} confirmDisabled={!!error} onConfirm={() => { void save(); }} onCancel={onClose} />}
    >
      <Field error={(name.trim() && error) || serverError}>
        <TextField value={name} onChange={v => { setName(v); setServerError(null); }} onEnter={() => { void save(); }} autoFocus invalid={!!error && !!name.trim()} />
      </Field>
    </Modal>
  );
}

// === Удаление темы ===
export function SubjectDeleteDialog({ subject, chats, onConfirm, onClose }: {
  subject: ProjectSubject;
  // Сколько корневых чатов уйдёт в «Без темы»
  chats: number;
  onConfirm: () => Promise<void>;
  onClose: () => void;
}) {
  return (
    <ConfirmDialog
      title={`Удалить тему «${subject.name}»?`}
      subtitle={`${chats} ${chatCountWord(chats)} ${chats === 1 ? 'перейдёт' : 'перейдут'} в «Без темы». Сами чаты не удаляются.`}
      confirmLabel="Удалить"
      confirmVariant="danger"
      onConfirm={async () => { await onConfirm(); onClose(); }}
      onCancel={onClose}
    />
  );
}

// === Заголовок секции темы: имя, ✦ у темы AI, счётчик, ⋮ (реестровые темы, онлайн) или
// кнопка «Разложить» у хвоста «Без темы» ===
export function SubjectSectionHeader({ subject, count, registry, isMobile, online, run, hideRunButton, onRename, onMove, onDelete, onRun }: {
  subject: ProjectSubject | null;
  count: number;
  registry: ProjectSubject[];
  isMobile: boolean;
  online: boolean;
  run: SubjectRunView;
  // Кнопку «Разложить» уже показывает пустое состояние реестра — не дублируем
  hideRunButton?: boolean;
  onRename: (s: ProjectSubject) => void;
  onMove: (s: ProjectSubject, dir: -1 | 1) => void;
  onDelete: (s: ProjectSubject) => void;
  onRun: () => void;
}) {
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const ordered = sortedSubjects(registry);
  const idx = subject ? ordered.findIndex(s => s.id === subject.id) : -1;
  const ai = subject?.createdBy === 'ai';
  let actions: React.ReactNode = null;
  if (subject && online) {
    actions = (
      <IconButton size={isMobile ? 'lg' : 'xs'} title={`Тема «${subject.name}»`} active={!!menu}
        onClick={e => setMenu(e.currentTarget.getBoundingClientRect())}>
        <MoreHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    );
  } else if (!subject && online && !hideRunButton && !run.running) {
    actions = (
      <Button variant="ghost" size={isMobile ? 'sm' : 'xs'} loading={run.starting} onClick={onRun}
        leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        title="AI разложит чаты без темы, включая старые">
        Разложить
      </Button>
    );
  }
  return (
    <>
      <ChatGroupHeader
        title={subject?.name ?? 'Без темы'}
        count={count}
        muted={!subject}
        sticky
        isMobile={isMobile}
        badge={ai ? (
          <span title={SUBJECT_AI_TITLE} style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{SUBJECT_AI_MARK}</span>
        ) : undefined}
        actions={actions}
      />
      {menu && subject && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={200} maxHeight={4 * (isMobile ? 44 : 38) + 30}>
          <MenuItem isMobile={isMobile} icon={<Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} label="Переименовать"
            onClick={() => { setMenu(null); onRename(subject); }} />
          <MenuItem isMobile={isMobile} icon={<ChevronUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} label="Выше"
            disabled={idx <= 0} onClick={() => { setMenu(null); onMove(subject, -1); }} />
          <MenuItem isMobile={isMobile} icon={<ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} label="Ниже"
            disabled={idx < 0 || idx >= ordered.length - 1} onClick={() => { setMenu(null); onMove(subject, 1); }} />
          <MenuSep />
          <MenuItem isMobile={isMobile} danger icon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} label="Удалить тему"
            onClick={() => { setMenu(null); onDelete(subject); }} />
        </Menu>
      )}
    </>
  );
}

// === Полоса прогресса разбора под тулбаром (только в режиме «Темы») ===
export function SubjectRunBar({ run }: { run: SubjectRunView }) {
  if (!run.running) return null;
  const s = run.state;
  const known = !!s && s.total > 0;
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs, padding: `6px ${SP.md}px`,
      borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0,
    }}>
      {/* Живой индикатор: на «0 из N» и при неизвестном total полоса стоит, а работа идёт */}
      <WaitingIndicator hint={known ? `Разбираю чаты: ${s.done} из ${s.total}` : 'Разбираю чаты…'} />
      <ProgressBar size="thin" value={known ? (s.done / s.total) * 100 : 0} indeterminate={!known} label="Разбор чатов по темам" />
    </div>
  );
}
