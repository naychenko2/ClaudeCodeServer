import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, MessageCircle, TriangleAlert, Undo2, User, X } from 'lucide-react';
import { MarkdownViewer } from '../../components/MarkdownViewer';
import { api } from '../../lib/api';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { useNotesVersion } from '../../lib/notes';
import { ensurePersonasLoaded, usePersonas, personaLabel } from '../../lib/personas';
import type { DocAnnotation, Persona, Project } from '../../types';

// Комментарии к MD-документам (флаг doc-annotations): обёртка над MarkdownViewer
// для просмотра .md проекта — выделение → попап «Комментировать», подсветка якорных
// блоков с балунами-маркерами и панель комментариев справа (на мобиле — снизу).
// Заметка-комментарий создаётся на бэке (verify-guard, 409 при гонке с правкой).

const PRESET_TAGS = ['вопрос', 'правка', 'идея', 'обсудить'];

interface SelectionInfo { start: number; end: number; text: string; x: number; y: number }

const statusColor = (open: boolean) => (open ? C.warning : C.success);

// Чип статуса/состояния комментария
function StatusChip({ status, state }: { status: DocAnnotation['status']; state?: DocAnnotation['state'] }) {
  if (state === 'orphan')
    return <Chip color={C.textMuted} bg={C.bgInset} icon={<X size={11} />} label="сирота" />;
  if (state === 'changed')
    return <Chip color={C.warningText} bg={C.warningBg} icon={<TriangleAlert size={11} />} label="место изменилось" />;
  return status === 'open'
    ? <Chip color={C.warningText} bg={C.warningBg} icon={<MessageCircle size={11} />} label="открыт" />
    : <Chip color={C.successText} bg={C.successBg} icon={<Check size={11} />} label="решён" />;
}

function Chip({ color, bg, icon, label }: { color: string; bg: string; icon: React.ReactNode; label: string }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4, padding: '1px 8px',
      borderRadius: 11, fontSize: 11, fontWeight: 600, color, background: bg,
      whiteSpace: 'nowrap',
    }}>{icon}{label}</span>
  );
}

// Загрузка комментариев документа + перезагрузка на realtime notes_changed
export function useDocAnnotations(scope: string, path: string, enabled: boolean) {
  const [items, setItems] = useState<DocAnnotation[]>([]);
  const notesVersion = useNotesVersion();
  const reload = useCallback(() => {
    if (!enabled) return;
    api.notes.annotations(scope, path).then(setItems).catch(() => setItems([]));
  }, [scope, path, enabled]);
  useEffect(() => { reload(); }, [reload, notesVersion]);
  return { items, reload };
}

interface Props {
  project: Project;
  filePath: string;
  content: string;
  isMobile?: boolean;
}

export function DocCommentedMarkdown({ project, filePath, content, isMobile }: Props) {
  const enabled = useFeature(FLAGS.docAnnotations);
  const docRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const { items, reload } = useDocAnnotations(project.id, filePath, enabled);
  const [selection, setSelection] = useState<SelectionInfo | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [comment, setComment] = useState('');
  const [tags, setTags] = useState<string[]>([]);
  const [customTag, setCustomTag] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [filter, setFilter] = useState<'all' | 'open' | 'none'>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const shown = useMemo(
    () => (filter === 'none' ? [] : items.filter(a => filter === 'all' || a.status === 'open')),
    [items, filter]);
  const openCount = items.filter(a => a.status === 'open').length;

  // ── Выделение → плавающая кнопка «Комментировать» ──
  const onMouseUp = () => {
    if (!enabled) return;
    window.setTimeout(() => {
      const sel = window.getSelection();
      const root = docRef.current;
      if (!sel || sel.isCollapsed || !root) { setSelection(null); return; }
      const text = sel.toString().trim();
      if (text.length < 3) { setSelection(null); return; }
      const range = sel.getRangeAt(0);
      if (!root.contains(range.commonAncestorContainer)) { setSelection(null); return; }
      // Ближайший блок с позициями исходника — офсеты ищем в его срезе (хинт для
      // verify-guard; не нашли локально — сервер примет единственное вхождение)
      let node: Node | null = range.startContainer;
      if (node.nodeType === Node.TEXT_NODE) node = node.parentElement;
      const blockEl = (node as HTMLElement | null)?.closest?.('[data-md-start]') as HTMLElement | null;
      let start = 0; let end = text.length;
      if (blockEl) {
        const bs = Number(blockEl.dataset.mdStart);
        const be = Number(blockEl.dataset.mdEnd);
        const idx = content.slice(bs, be + 1).indexOf(text);
        if (idx >= 0) { start = bs + idx; end = start + text.length; }
        else {
          const first = content.indexOf(text);
          if (first >= 0) { start = first; end = first + text.length; }
        }
      }
      const rect = range.getBoundingClientRect();
      setSelection({ start, end, text, x: rect.left + rect.width / 2, y: rect.top });
      setFormOpen(false);
    }, 10);
  };

  const openForm = () => {
    setComment(''); setTags([]); setCustomTag(''); setFormError(null);
    setFormOpen(true);
  };

  const create = async () => {
    if (!selection) return;
    setSaving(true); setFormError(null);
    try {
      await api.notes.annotate({
        doc: { scope: project.id, path: filePath },
        selection: { start: selection.start, end: selection.end, text: selection.text },
        comment: comment.trim() || undefined,
        tags: tags.length ? tags : undefined,
      });
      setFormOpen(false); setSelection(null);
      window.getSelection()?.removeAllRanges();
      reload();
    } catch (e) {
      const msg = e instanceof Error ? e.message : '';
      setFormError(msg.includes('409') || msg.toLowerCase().includes('изменился')
        ? 'Документ изменился — выделите фрагмент заново'
        : 'Не удалось создать комментарий');
    } finally { setSaving(false); }
  };

  const toggleStatus = async (a: DocAnnotation) => {
    try {
      await api.notes.setStatus(a.noteId, a.status === 'open' ? 'resolved' : 'open');
      reload();
    } catch { /* realtime подтянет актуальное */ }
  };

  const openNote = (a: DocAnnotation) => {
    sessionStorage.setItem('cc_pending_note_id', a.noteId);
    window.dispatchEvent(new Event('cc-open-note'));
  };

  // «Поручить персоне»: создаёт задачу проекта с персоной-исполнителем и ссылкой
  // на комментарий («create issue from comment» — второй трекер не строим)
  const personas = usePersonas();
  const [assignFor, setAssignFor] = useState<string | null>(null);   // noteId открытого подменю
  const [assignedMsg, setAssignedMsg] = useState<string | null>(null);
  useEffect(() => { if (enabled) void ensurePersonasLoaded(); }, [enabled]);
  useEffect(() => {
    if (!assignFor) return;
    const close = () => setAssignFor(null);
    const t = window.setTimeout(() => window.addEventListener('mousedown', close), 0);
    return () => { window.clearTimeout(t); window.removeEventListener('mousedown', close); };
  }, [assignFor]);
  const assignTo = async (a: DocAnnotation, p: Persona) => {
    setAssignFor(null);
    try {
      await api.tasks.create(project.id, {
        title: `Обработать комментарий: ${a.title}`,
        description: [
          `Комментарий к документу \`${filePath}\`${a.anchorHeading ? ` (${a.anchorHeading})` : ''}:`,
          '',
          `> ${a.quote}`,
          '',
          a.excerpt && a.excerpt !== a.title ? a.excerpt : a.title,
          '',
          `Заметка-комментарий: [${a.title}](#/notes/${encodeURIComponent(a.noteId)}). ` +
          'После обработки пометь комментарий решённым (notes_set_status).',
        ].join('\n'),
        assignee: 'claude',
        personaId: p.id,
      });
      setAssignedMsg(`Задача создана и поручена: ${personaLabel(p)}`);
      window.setTimeout(() => setAssignedMsg(null), 3500);
    } catch {
      setAssignedMsg('Не удалось создать задачу');
      window.setTimeout(() => setAssignedMsg(null), 3500);
    }
  };

  const gotoBlock = (a: DocAnnotation) => {
    setSelectedId(a.noteId);
    if (a.start < 0) return;
    const el = findBlockEl(docRef.current, a.start);
    if (el) {
      el.scrollIntoView({ block: 'center', behavior: 'smooth' });
      el.style.transition = 'outline-color .2s';
      el.style.outline = `2px solid ${C.accent}`;
      el.style.outlineOffset = '2px';
      window.setTimeout(() => { el.style.outline = ''; el.style.outlineOffset = ''; }, 900);
    }
  };

  // ── Подсветка якорных блоков + балуны-маркеры (DOM-слой поверх рендера) ──
  useEffect(() => {
    const root = docRef.current;
    if (!root || !enabled) return;
    const cleanups: (() => void)[] = [];
    const byBlock = new Map<HTMLElement, DocAnnotation[]>();
    for (const a of shown) {
      if (a.start < 0) continue;
      const el = findBlockEl(root, a.start);
      if (!el) continue;
      byBlock.set(el, [...(byBlock.get(el) ?? []), a]);
    }
    byBlock.forEach((anns, el) => {
      const hasOpen = anns.some(a => a.status === 'open');
      const prev = {
        background: el.style.background, borderLeft: el.style.borderLeft,
        paddingLeft: el.style.paddingLeft, borderRadius: el.style.borderRadius,
        position: el.style.position,
      };
      el.style.background = C.accentLight;
      el.style.borderLeft = `3px solid ${C.accent}`;
      el.style.paddingLeft = '10px';
      el.style.borderRadius = '0 8px 8px 0';
      el.style.position = 'relative';

      // Балун: иконка-статус (пузырёк — есть открытые, галочка — решены) + счётчик
      const mark = document.createElement('button');
      mark.type = 'button';
      mark.title = hasOpen
        ? `Открытых: ${anns.filter(a => a.status === 'open').length} из ${anns.length}`
        : 'Все решены';
      const color = statusColor(hasOpen);
      Object.assign(mark.style, {
        position: 'absolute', top: '-11px', right: '-4px', display: 'flex',
        alignItems: 'center', gap: '4px', padding: '2px 8px 2px 6px',
        border: `1px solid ${color}`, borderRadius: '12px', background: C.bgCard,
        color, fontSize: '11px', fontWeight: '700', cursor: 'pointer',
        boxShadow: '0 1px 4px rgba(0,0,0,.12)', fontFamily: FONT.sans, lineHeight: '1.3',
      } satisfies Partial<CSSStyleDeclaration>);
      mark.innerHTML = (hasOpen ? SVG_BUBBLE : SVG_CHECK) + `<span>${anns.length}</span>`;
      mark.addEventListener('click', e => {
        e.stopPropagation();
        const first = anns[0];
        setSelectedId(first.noteId);
        const card = panelRef.current?.querySelector(`[data-ann="${first.noteId}"]`);
        card?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      });
      el.appendChild(mark);

      cleanups.push(() => {
        mark.remove();
        el.style.background = prev.background;
        el.style.borderLeft = prev.borderLeft;
        el.style.paddingLeft = prev.paddingLeft;
        el.style.borderRadius = prev.borderRadius;
        el.style.position = prev.position;
      });
    });
    return () => cleanups.forEach(f => f());
  }, [shown, enabled, content]);

  const panel = enabled && items.length > 0 && (
    <div ref={panelRef} style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontWeight: 600, fontSize: 13, color: C.textHeading }}>
          <MessageCircle size={14} style={{ color: C.accent }} /> Комментарии
          <span style={{ color: C.textMuted, fontWeight: 400 }}>· {items.length}{openCount > 0 && ` · ${openCount} откр.`}</span>
        </span>
        <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: 8, overflow: 'hidden', marginLeft: 'auto' }}>
          {([['all', 'Все'], ['open', 'Открытые'], ['none', 'Скрыть']] as const).map(([key, label]) => (
            <button key={key} onClick={() => setFilter(key)} style={{
              padding: '3px 9px', fontSize: 11.5, border: 'none', cursor: 'pointer',
              background: filter === key ? C.accentLight : 'transparent',
              color: filter === key ? C.textHeading : C.textMuted,
              fontWeight: filter === key ? 600 : 400, fontFamily: FONT.sans,
            }}>{label}</button>
          ))}
        </div>
      </div>
      {assignedMsg && (
        <div style={{ fontSize: 12, color: C.successText, background: C.successBg, borderRadius: R.sm, padding: '5px 9px' }}>
          {assignedMsg}
        </div>
      )}
      {filter === 'none'
        ? <div style={{ fontSize: 12, color: C.textMuted }}>Комментарии скрыты фильтром.</div>
        : shown.map(a => (
          <div key={a.noteId} data-ann={a.noteId} onClick={() => gotoBlock(a)} style={{
            border: `1px solid ${selectedId === a.noteId ? C.accent : C.border}`,
            boxShadow: selectedId === a.noteId ? `0 0 0 1px ${C.accent}` : undefined,
            borderRadius: R.lg, background: C.bgCard, padding: '9px 11px',
            display: 'flex', flexDirection: 'column', gap: 6, cursor: 'pointer',
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
              <StatusChip status={a.status} state={a.state} />
              {a.anchorHeading && (
                <span style={{ fontSize: 11, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {a.anchorHeading}
                </span>
              )}
            </div>
            <div style={{ fontSize: 12.5, color: C.textHeading, fontWeight: 600 }}>{a.title}</div>
            {a.quote && (
              <div style={{
                fontSize: 11.5, color: C.textSecondary, fontStyle: 'italic',
                borderLeft: `2px solid ${C.accent}`, paddingLeft: 8,
                overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
              }}>«{a.quote}»</div>
            )}
            {a.excerpt && a.excerpt !== a.title && (
              <div style={{ fontSize: 12, color: C.textSecondary }}>{a.excerpt}</div>
            )}
            {a.tags.length > 0 && (
              <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                {a.tags.map(t => (
                  <span key={t} style={{ fontSize: 10.5, color: C.accent, background: C.accentLight, borderRadius: 9, padding: '1px 7px', fontFamily: FONT.mono }}>#{t}</span>
                ))}
              </div>
            )}
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', position: 'relative' }}>
              <ActionBtn onClick={e => { e.stopPropagation(); void toggleStatus(a); }}>
                {a.status === 'open' ? <><Check size={11} /> Решён</> : <><Undo2 size={11} /> Снова открыть</>}
              </ActionBtn>
              <ActionBtn onClick={e => { e.stopPropagation(); openNote(a); }}>Открыть заметку</ActionBtn>
              {personas.length > 0 && (
                <ActionBtn onClick={e => { e.stopPropagation(); setAssignFor(assignFor === a.noteId ? null : a.noteId); }}>
                  <User size={11} /> Поручить ▾
                </ActionBtn>
              )}
              {assignFor === a.noteId && (
                <div onMouseDown={e => e.stopPropagation()} style={{
                  position: 'absolute', bottom: 'calc(100% + 6px)', left: 0, zIndex: 5,
                  background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.lg,
                  boxShadow: SHADOW.dropdown, minWidth: 200, maxHeight: 220, overflowY: 'auto', padding: 4,
                }}>
                  {personas.map(p => (
                    <button key={p.id} onClick={e => { e.stopPropagation(); void assignTo(a, p); }} style={{
                      display: 'flex', alignItems: 'center', gap: 8, width: '100%', padding: '7px 10px',
                      border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
                      fontFamily: FONT.sans, fontSize: 12.5, color: C.textPrimary, borderRadius: 6,
                    }}
                    onMouseEnter={e => { e.currentTarget.style.background = C.bgInset; }}
                    onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}>
                      <User size={12} style={{ color: C.textMuted, flexShrink: 0 }} />
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{personaLabel(p)}</span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>
        ))}
    </div>
  );

  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 18 }}>
      <div ref={docRef} onMouseUp={onMouseUp} style={{ flex: 1, minWidth: 0 }}>
        <MarkdownViewer content={content} blockPos={enabled} />
        {isMobile && panel && (
          <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>{panel}</div>
        )}
      </div>
      {!isMobile && panel && (
        <aside style={{
          width: 290, flex: 'none', position: 'sticky', top: 0,
          maxHeight: 'calc(100vh - 160px)', overflowY: 'auto',
          borderLeft: `1px solid ${C.border}`, paddingLeft: 14,
        }}>{panel}</aside>
      )}

      {/* Плавающая кнопка над выделением */}
      {selection && !formOpen && createPortal(
        <button onClick={openForm} style={{
          position: 'fixed', zIndex: Z.modal,
          left: clamp(selection.x - 80, 8, window.innerWidth - 180),
          top: Math.max(8, selection.y - 44),
          display: 'flex', alignItems: 'center', gap: 7, padding: '7px 13px',
          background: C.textHeading, color: C.bgMain, border: 'none', borderRadius: 10,
          fontSize: 13, fontWeight: 600, cursor: 'pointer', boxShadow: SHADOW.dropdown,
          fontFamily: FONT.sans,
        }}>
          <MessageCircle size={13} /> Комментировать
        </button>,
        document.body,
      )}

      {/* Форма создания */}
      {selection && formOpen && createPortal(
        <div style={{
          position: 'fixed', zIndex: Z.modal,
          left: clamp(selection.x - 165, 8, window.innerWidth - 348),
          top: Math.min(selection.y + 14, window.innerHeight - 320),
          width: 330, background: C.bgCard, border: `1px solid ${C.border}`,
          borderRadius: R.xl, boxShadow: SHADOW.dropdown, overflow: 'hidden',
          fontFamily: FONT.sans,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '11px 14px', borderBottom: `1px solid ${C.border}`, fontWeight: 600, fontSize: 13, color: C.textHeading }}>
            <MessageCircle size={14} style={{ color: C.accent }} /> Новый комментарий
            <button onClick={() => setFormOpen(false)} style={{ marginLeft: 'auto', border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted }}>
              <X size={14} />
            </button>
          </div>
          <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{
              borderLeft: `3px solid ${C.accent}`, background: C.accentLight,
              borderRadius: '0 8px 8px 0', padding: '7px 10px', fontSize: 12.5,
              color: C.textSecondary, fontStyle: 'italic', maxHeight: 76, overflow: 'hidden',
            }}>
              «{selection.text.length > 140 ? selection.text.slice(0, 140) + '…' : selection.text}»
            </div>
            <textarea
              value={comment} onChange={e => setComment(e.target.value)} autoFocus
              placeholder="Комментарий… (например: уточнить, поправить, обсудить)"
              style={{
                width: '100%', boxSizing: 'border-box', border: `1px solid ${C.border}`,
                borderRadius: 9, background: C.bgMain, color: C.textHeading,
                font: `13px/1.5 ${FONT.sans}`, padding: '8px 10px', resize: 'vertical', minHeight: 64,
              }}
            />
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
              {[...PRESET_TAGS, ...tags.filter(t => !PRESET_TAGS.includes(t))].map(t => {
                const on = tags.includes(t);
                return (
                  <button key={t} onClick={() => setTags(on ? tags.filter(x => x !== t) : [...tags, t])} style={{
                    border: `1px solid ${on ? C.accent : C.border}`, borderRadius: 14,
                    padding: '2px 10px', fontSize: 12, cursor: 'pointer', fontFamily: FONT.sans,
                    background: on ? C.accentLight : 'transparent',
                    color: on ? C.textHeading : C.textMuted,
                  }}>#{t}</button>
                );
              })}
              <input
                value={customTag} onChange={e => setCustomTag(e.target.value)}
                onKeyDown={e => {
                  if (e.key !== 'Enter') return;
                  e.preventDefault();
                  const v = customTag.trim().replace(/^#/, '');
                  if (v && !tags.includes(v)) setTags([...tags, v]);
                  setCustomTag('');
                }}
                placeholder="+ тег" aria-label="Новый тег"
                style={{
                  border: `1px dashed ${C.border}`, borderRadius: 14, padding: '2px 10px',
                  font: `12px ${FONT.sans}`, color: C.textHeading, background: 'none', width: 74,
                }}
              />
            </div>
            {formError && <div style={{ fontSize: 12, color: C.danger }}>{formError}</div>}
          </div>
          <div style={{ display: 'flex', gap: 8, padding: '10px 14px', borderTop: `1px solid ${C.border}` }}>
            <button onClick={() => void create()} disabled={saving} style={{
              padding: '5px 14px', background: C.accent, color: '#fff', border: 'none',
              borderRadius: 8, fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
              opacity: saving ? 0.6 : 1, fontFamily: FONT.sans,
            }}>{saving ? 'Создаю…' : 'Создать'}</button>
            <button onClick={() => setFormOpen(false)} style={{
              padding: '5px 11px', background: C.bgCard, color: C.textMuted,
              border: `1px solid ${C.border}`, borderRadius: 8, fontSize: 12.5, cursor: 'pointer', fontFamily: FONT.sans,
            }}>Отмена</button>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

function ActionBtn({ children, onClick }: { children: React.ReactNode; onClick: (e: React.MouseEvent) => void }) {
  return (
    <button onClick={onClick} style={{
      display: 'inline-flex', alignItems: 'center', gap: 4, border: `1px solid ${C.border}`,
      borderRadius: 7, padding: '3px 9px', fontSize: 11.5, color: C.textMuted,
      background: C.bgCard, cursor: 'pointer', fontFamily: FONT.sans,
    }}>{children}</button>
  );
}

// Блок с позициями, содержащий офсет (при вложенности — самый глубокий)
function findBlockEl(root: HTMLElement | null, offset: number): HTMLElement | null {
  if (!root) return null;
  let best: HTMLElement | null = null;
  let bestStart = -1;
  for (const el of Array.from(root.querySelectorAll<HTMLElement>('[data-md-start]'))) {
    const s = Number(el.dataset.mdStart);
    const e = Number(el.dataset.mdEnd);
    if (Number.isNaN(s) || Number.isNaN(e)) continue;
    if (s <= offset && offset < e && s > bestStart) { best = el; bestStart = s; }
  }
  return best;
}

function clamp(v: number, min: number, max: number): number {
  return Math.min(Math.max(v, min), max);
}

// Инлайн-SVG для DOM-инъекции балуна (lucide message-circle / check)
const SVG_ATTRS = 'viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"';
const SVG_BUBBLE = `<svg ${SVG_ATTRS}><path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/></svg>`;
const SVG_CHECK = `<svg ${SVG_ATTRS}><path d="M20 6 9 17l-5-5"/></svg>`;
