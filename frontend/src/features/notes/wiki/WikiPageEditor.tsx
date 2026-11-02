// Редактор страницы вики (F4a, ADR-038 «Контракт для фронта», макет notes-wiki-unified-v1,
// экран «Правка»). Правится текст утверждений по секциям; шапку, заголовок, сноски и их
// определения человек не трогает — они уезжают на сервер как пришли. «Убрать» — не
// удаление: утверждение уходит в «Историю» страницы (так делает сервер, фронт лишь честно
// об этом говорит). Сохранение — editWikiPage(baseHash, полный текст).
//
// Конкурентность: двойной клик «Сохранить» — один запрос (замок в ref, state не успевает);
// ответ, пришедший после смены страницы или ухода с неё, отбрасывается по ключу страницы
// (раздел + путь) — ни состояния, ни onDone. Любой отказ ловится здесь: наружу rejection
// не выходит. 409 wiki_page_changed ничего не перезаписывает: «моё» и свежая версия хранятся
// в conflict — точка расширения для сравнения «Моё / ИИ / Было» (шаг F4b, WikiEditConflict).

import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, Archive, Check, Info, Undo2 } from 'lucide-react';
import { C, FONT, FS, R, SP, useIsMobile, WikiError, editWikiPage, type WikiSectionDto } from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { TextArea } from '../../../components/ui/Field';
import { IconButton } from '../../../components/ui/IconButton';
import { Notice } from '../../../components/ui/Notice';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { wikiFootnoteLabel, type WikiConflictPage, type WikiFootnoteDto } from '../../../lib/wiki';
import { EDIT_ISSUE_TITLE, EDIT_TEXT, stripViolationPrefix, wikiErrorText } from './wikiLabels';

// ─── Разбор текста для правки (зеркало разбора страницы: шапка → «# заголовок» → «## секции») ───

const TITLE_RX = /^# (.+)$/;
const SECTION_RX = /^## (.+)$/;
const FN_DEF_RX = /^\[\^([^\]\r\n]+)\]: (.+?) — «(.*)»$/;
const BULLET_RX = /^([-*]\s+)/;
const REF_RX = /\s*\[\^([^\]\r\n]+)\]/g;

// Секция, утверждения которой не убираются и не правятся (сервер: «физического удаления нет»)
const READONLY_SECTION = 'История';

export interface EditItem {
  // Порядковый номер среди строк секций — стабильный ключ строки на время правки
  id: number;
  section: string;
  // Индекс строки в исходном тексте
  line: number;
  // Маркер списка исходной строки («- »), пусто — строка без маркера
  bullet: string;
  // Текст для поля: без маркера и без ссылок на сноски
  text: string;
  // Метки сносок строки
  refs: string[];
  editable: boolean;
}

export interface EditEntry { text?: string; gone?: boolean }

export function scanEditable(content: string): { lines: string[]; items: EditItem[] } {
  const lines = content.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const items: EditItem[] = [];
  let i = 0;
  if (lines[0] === '---') {
    const close = lines.indexOf('---', 1);
    if (close > 0) i = close + 1;
  }
  let titleSeen = false;
  let current: string | null = null;
  for (; i < lines.length; i++) {
    const line = lines[i];
    if (!line || FN_DEF_RX.test(line)) continue;
    if (!titleSeen && TITLE_RX.test(line)) { titleSeen = true; continue; }
    const s = SECTION_RX.exec(line);
    if (s) { current = s[1]; continue; }
    if (current === null) continue;
    const bullet = BULLET_RX.exec(line)?.[1] ?? '';
    const refs = [...line.matchAll(REF_RX)].map(m => m[1]);
    items.push({
      id: items.length, section: current, line: i, bullet, refs,
      text: line.slice(bullet.length).replace(REF_RX, '').trim(),
      editable: current !== READONLY_SECTION,
    });
  }
  return { lines, items };
}

// Полный новый текст страницы: нетронутые строки — как были (вместе со сносками), изменённые
// — «маркер + новый текст» (сноску поставит сервер), убранные — выпадают из текста
export function buildContent(content: string, edits: ReadonlyMap<number, EditEntry>): string {
  const { lines, items } = scanEditable(content);
  const out = lines.slice();
  const drop = new Set<number>();
  for (const it of items) {
    const e = edits.get(it.id);
    if (!e || !it.editable) continue;
    if (e.gone) { drop.add(it.line); continue; }
    if (e.text !== undefined && e.text.trim() !== it.text) out[it.line] = `${it.bullet}${e.text.trim()}`;
  }
  return out.filter((_, idx) => !drop.has(idx)).join('\n');
}

// ─── Итог для родителя ───

export type EditDone =
  | { kind: 'saved' }
  | { kind: 'unchanged' }
  | { kind: 'cancel' }
  | { kind: 'off' };

interface Props {
  section: WikiSectionDto;
  pageKey: string;
  // Текст и хэш, с которыми открыт редактор (GET page)
  content: string;
  hash: string;
  footnotes?: WikiFootnoteDto[];
  onDone: (r: EditDone) => void;
}

interface Conflict { mine: string; theirs: WikiConflictPage | null }
interface Refusal { lines: { title: string | null; text: string }[]; invalid: boolean }

// Плашка конфликта. Точка расширения F4b: сюда придёт сравнение «Моё / ИИ / Было»
// (mine — текст редактора, theirs — свежая версия, Было — content при открытии)
export function WikiEditConflict({ conflict, onClose }: { conflict: Conflict; onClose: () => void }) {
  return (
    <div data-testid="wiki-edit-conflict" style={{ marginTop: SP.md }}>
      <Notice
        tone="warning"
        icon={AlertTriangle}
        title={EDIT_TEXT.conflictTitle}
        action={<Button variant="secondary" size="sm" dataTestId="wiki-edit-conflict-close" onClick={onClose}>Закрыть без сохранения</Button>}
      >
        {conflict.theirs ? EDIT_TEXT.conflictText : EDIT_TEXT.conflictGone}
      </Notice>
    </div>
  );
}

export function WikiPageEditor({ section, pageKey, content, hash, footnotes, onDone }: Props) {
  const isMobile = useIsMobile();
  const pageId = `${section.key}\n${pageKey}`;
  // Страница, на которой редактор открыт: ответ после смены страницы сверяется с ней
  const idRef = useRef<string | null>(pageId);
  idRef.current = pageId;
  useEffect(() => () => { idRef.current = null; }, []);
  const onDoneRef = useRef(onDone);
  onDoneRef.current = onDone;
  const busyRef = useRef(false);

  // Базовая версия — снимок момента открытия: стор может обновиться, пока человек правит
  const [base] = useState(() => ({ content, hash }));
  const { items } = scanEditable(base.content);
  const humanRefs = new Set((footnotes ?? []).filter(f => f.origin === 'human').map(f => wikiFootnoteLabel(f.label)));

  const [edits, setEdits] = useState<ReadonlyMap<number, EditEntry>>(new Map());
  const [ask, setAsk] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<Refusal | null>(null);
  const [conflict, setConflict] = useState<Conflict | null>(null);

  const patch = (id: number, e: EditEntry) => setEdits(prev => new Map(prev).set(id, { ...prev.get(id), ...e }));
  const textOf = (it: EditItem) => edits.get(it.id)?.text ?? it.text;
  const changed = (it: EditItem) => it.editable && textOf(it).trim() !== it.text;
  const goneOf = (it: EditItem) => edits.get(it.id)?.gone === true;

  const blank = items.some(it => it.editable && !goneOf(it) && textOf(it).trim() === '');
  const dirty = items.some(it => it.editable && (goneOf(it) || changed(it)));

  const save = async () => {
    // Замок в ref: state обновится только к следующему рендеру, а второй клик приходит раньше
    if (busyRef.current || conflict || blank) return;
    busyRef.current = true;
    setBusy(true);
    setRefusal(null);
    const startId = idRef.current;
    const mine = buildContent(base.content, edits);
    try {
      const r = await editWikiPage(section.key, pageKey, base.hash, mine);
      if (idRef.current !== startId) return;
      onDoneRef.current({ kind: r.status === 'applied' ? 'saved' : 'unchanged' });
    } catch (e) {
      if (idRef.current !== startId) return;
      if (e instanceof WikiError && e.code === 'wiki_page_changed') {
        setConflict({ mine, theirs: e.page });
      } else if (e instanceof WikiError && e.code === 'wiki_unified_off') {
        onDoneRef.current({ kind: 'off' });
      } else if (e instanceof WikiError && e.code === 'wiki_edit_invalid') {
        const lines = e.issues.length
          ? e.issues.map(i => ({ title: EDIT_ISSUE_TITLE[i.code] ?? null, text: stripViolationPrefix(i.message) }))
          : (e.violations.length ? e.violations : [e.message]).map(v => ({ title: null, text: stripViolationPrefix(v) }));
        setRefusal({ lines, invalid: true });
      } else {
        setRefusal({ lines: [{ title: null, text: e instanceof WikiError ? wikiErrorText(e) : EDIT_TEXT.failText }], invalid: false });
      }
    } finally {
      busyRef.current = false;
      if (idRef.current === startId) setBusy(false);
    }
  };

  const bySection: { name: string; items: EditItem[] }[] = [];
  for (const it of items) {
    const last = bySection[bySection.length - 1];
    if (last && last.name === it.section) last.items.push(it);
    else bySection.push({ name: it.section, items: [it] });
  }

  const btnSize = isMobile ? 'md' : 'sm';
  const isAi = (it: EditItem) => it.refs.length > 0 && it.refs.some(r => !humanRefs.has(r));

  return (
    <div data-testid="wiki-editor" style={{ marginTop: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="neutral" size="xs">Правка</Badge>
        <span style={{ flex: 1 }} />
        <Button variant="ghost" size={btnSize} dataTestId="wiki-edit-cancel" disabled={busy} onClick={() => onDone({ kind: 'cancel' })}>
          {EDIT_TEXT.cancel}
        </Button>
        <Button
          variant="primary"
          size={btnSize}
          dataTestId="wiki-edit-save"
          loading={busy}
          disabled={!dirty || blank || conflict !== null}
          leftIcon={<Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => { void save(); }}
        >
          {EDIT_TEXT.save}
        </Button>
      </div>

      {conflict && <WikiEditConflict conflict={conflict} onClose={() => onDone({ kind: 'cancel' })} />}

      {refusal && (
        <div data-testid="wiki-edit-error" style={{ marginTop: SP.md }}>
          <Notice tone={refusal.invalid ? 'warning' : 'danger'} icon={AlertTriangle} title={refusal.invalid ? EDIT_TEXT.invalidTitle : EDIT_TEXT.failTitle}>
            <ul style={{ margin: 0, paddingLeft: SP.lg }}>
              {refusal.lines.map((l, i) => (
                <li key={i}>{l.title ? <b>{l.title}. </b> : null}{l.text}</li>
              ))}
            </ul>
            <div style={{ marginTop: SP.xs }}>{refusal.invalid ? EDIT_TEXT.invalidKeep : EDIT_TEXT.failText}</div>
          </Notice>
        </div>
      )}

      {blank && (
        <div style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.textMuted }}>{EDIT_TEXT.blank}</div>
      )}

      {bySection.map(sec => (
        <section key={sec.name} data-testid="wiki-edit-section" data-section-name={sec.name} style={{ marginTop: SP.lg }}>
          <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{sec.name}</h3>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.sm }}>
            {sec.items.map(it => (
              <EditRow
                key={it.id}
                it={it}
                text={textOf(it)}
                gone={goneOf(it)}
                asking={ask === it.id}
                aiWarn={isAi(it) && changed(it)}
                disabled={busy}
                isMobile={isMobile}
                onText={v => patch(it.id, { text: v.replace(/\s*\n\s*/g, ' ') })}
                onAsk={() => setAsk(it.id)}
                onAskNo={() => setAsk(null)}
                onGone={() => { patch(it.id, { gone: true }); setAsk(null); }}
                onBack={() => patch(it.id, { gone: false })}
              />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

function EditRow({ it, text, gone, asking, aiWarn, disabled, isMobile, onText, onAsk, onAskNo, onGone, onBack }: {
  it: EditItem; text: string; gone: boolean; asking: boolean; aiWarn: boolean; disabled: boolean; isMobile: boolean;
  onText: (v: string) => void; onAsk: () => void; onAskNo: () => void; onGone: () => void; onBack: () => void;
}): ReactNode {
  const tid = `wiki-edit-item-${it.id}`;
  if (!it.editable) {
    return (
      <div data-testid={tid} style={{ fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5, overflowWrap: 'anywhere', padding: `0 ${SP.sm}px` }}>
        {it.text}
      </div>
    );
  }
  if (gone) {
    return (
      <div data-testid={tid} data-gone="true" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.sm, background: C.bgInset, borderRadius: R.md }}>
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.md, color: C.textMuted, textDecoration: 'line-through', overflowWrap: 'anywhere' }}>{it.text}</span>
        <Button variant="ghost" size={isMobile ? 'md' : 'sm'} dataTestId={`wiki-edit-restore-${it.id}`} leftIcon={<Undo2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} onClick={onBack}>
          {EDIT_TEXT.restore}
        </Button>
      </div>
    );
  }
  return (
    <div data-testid={tid}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <TextArea value={text} onChange={onText} autoGrow minHeight={44} disabled={disabled} isMobile={isMobile} />
        </div>
        <IconButton
          size={isMobile ? 'lg' : 'md'}
          tone="danger"
          ariaLabel="Убрать утверждение"
          disabled={disabled}
          onClick={onAsk}
        >
          <Archive size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      {aiWarn && (
        <div data-testid={`wiki-edit-ai-warn-${it.id}`} style={{ marginTop: SP.xs }}>
          <Notice tone="info" icon={Info}>{EDIT_TEXT.aiWarn}</Notice>
        </div>
      )}
      {asking && (
        <div data-testid={`wiki-edit-remove-ask-${it.id}`} style={{ marginTop: SP.xs }}>
          <Notice
            tone="warning"
            icon={Archive}
            title={EDIT_TEXT.removeAsk}
            action={
              <span style={{ display: 'inline-flex', gap: SP.xs }}>
                <Button variant="danger" size="sm" dataTestId={`wiki-edit-remove-ok-${it.id}`} onClick={onGone}>{EDIT_TEXT.remove}</Button>
                <Button variant="ghost" size="sm" dataTestId={`wiki-edit-remove-no-${it.id}`} onClick={onAskNo}>{EDIT_TEXT.cancel}</Button>
              </span>
            }
          >
            {EDIT_TEXT.removeHistory}
          </Notice>
        </div>
      )}
    </div>
  );
}
