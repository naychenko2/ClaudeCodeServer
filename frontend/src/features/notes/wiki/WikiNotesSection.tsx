// Секция «Заметки» страницы вики (F5a, ADR-038, решение 1; макет notes-wiki-unified-v1 §6):
// последняя секция страницы, свободный текст человека без сносок, допускает таблицу. ИИ её не
// меняет, но читает. Правится тем же редактором страницы (одно поле, без «Убрать»): сюда
// вынесены чистые помощники над текстом страницы и сама секция в видах «чтение» и «правка».

import type { ReactNode } from 'react';
import { PenLine } from 'lucide-react';
import { C, FONT, FS, R, SP, TextArea } from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { NOTES_TEXT } from './wikiLabels';

const SECTION_RX = /^## (.+)$/;
const NOTES_RX = /^## Заметки\s*$/;
const FN_DEF_RX = /^\[\^([^\]\r\n]+)\]: (.+?) — «(.*)»$/;
const TABLE_SEP_RX = /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;

const splitLines = (content: string) => content.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');

// Границы секции «Заметки» в строках: [заголовок, конец) — конец это следующая секция или хвост
function notesRange(lines: string[]): { head: number; end: number } | null {
  const head = lines.findIndex(l => NOTES_RX.test(l));
  if (head < 0) return null;
  let end = lines.length;
  for (let i = head + 1; i < lines.length; i++) if (SECTION_RX.test(lines[i])) { end = i; break; }
  return { head, end };
}

// Текст секции без определений сносок; null — секции на странице нет
export function extractNotes(content: string): string | null {
  const lines = splitLines(content);
  const r = notesRange(lines);
  if (!r) return null;
  return lines.slice(r.head + 1, r.end).filter(l => !FN_DEF_RX.test(l)).map(l => l.trimEnd()).join('\n').trim();
}

// Страница с новым текстом «Заметок»: остальное не трогаем. Нет секции — дописываем последней
// (перед хвостом определений сносок); пустой текст у отсутствующей секции её не заводит
export function withNotes(content: string, text: string): string {
  const lines = splitLines(content);
  const body = text.trim() ? text.trim().split('\n').map(l => l.trimEnd()) : [];
  const r = notesRange(lines);
  if (r) {
    const defs = lines.slice(r.head + 1, r.end).filter(l => FN_DEF_RX.test(l));
    return [
      ...lines.slice(0, r.head + 1),
      ...(body.length ? ['', ...body] : []),
      ...(defs.length ? ['', ...defs] : []),
      ...lines.slice(r.end),
    ].join('\n');
  }
  if (!body.length) return content;
  let e = lines.length;
  while (e > 0 && (!lines[e - 1].trim() || FN_DEF_RX.test(lines[e - 1]))) e--;
  const tail = lines.slice(e).filter(l => l.trim());
  return [...lines.slice(0, e), '', '## Заметки', '', ...body, ...(tail.length ? ['', ...tail] : []), ''].join('\n');
}

// ─── Чтение ───

type Block = { kind: 'p'; text: string } | { kind: 'table'; rows: string[][] };

const cells = (l: string) => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());

export function parseNotesBlocks(text: string): Block[] {
  const lines = text.split('\n');
  const out: Block[] = [];
  let para: string[] = [];
  const flush = () => { if (para.length) { out.push({ kind: 'p', text: para.join('\n') }); para = []; } };
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].trim().startsWith('|') && i + 1 < lines.length && TABLE_SEP_RX.test(lines[i + 1].trim())) {
      flush();
      const rows = [cells(lines[i])];
      i += 2;
      for (; i < lines.length && lines[i].trim().startsWith('|'); i++) rows.push(cells(lines[i]));
      i--;
      out.push({ kind: 'table', rows });
    } else para.push(lines[i]);
  }
  flush();
  return out;
}

function Heading({ caption }: { caption: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
      <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{NOTES_TEXT.section}</h3>
      <span style={{ fontSize: FS.xs, fontWeight: 500, color: C.textMuted }}>{caption}</span>
    </div>
  );
}

interface ViewProps { text: string; canWrite: boolean; isMobile: boolean; onWrite: () => void }

// Чтение: текст в карточке; пусто — приглашение записать (если страницу можно править)
export function WikiNotesSection({ text, canWrite, isMobile, onWrite }: ViewProps): ReactNode {
  if (!text && !canWrite) return null;
  return (
    <section data-testid="wiki-notes" data-section-name={NOTES_TEXT.section} style={{ marginTop: SP.lg }}>
      <Heading caption={NOTES_TEXT.caption} />
      {text ? (
        <div data-testid="wiki-notes-body" style={{ marginTop: SP.sm, padding: `${SP.sm}px ${SP.md}px`, background: C.bgCard, borderRadius: R.lg, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.6, overflowWrap: 'anywhere' }}>
          {parseNotesBlocks(text).map((b, i) => b.kind === 'p' ? (
            <div key={i} style={{ whiteSpace: 'pre-wrap' }}>{b.text}</div>
          ) : (
            <div key={i} style={{ overflowX: 'auto', margin: `${SP.xs}px 0` }}>
              <table style={{ borderCollapse: 'collapse', fontSize: FS.base }}>
                <tbody>
                  {b.rows.map((row, ri) => (
                    <tr key={ri}>
                      {row.map((cell, ci) => {
                        const Cell = ri === 0 ? 'th' : 'td';
                        return <Cell key={ci} style={{ border: `1px solid ${C.border}`, padding: `${SP.xxs}px ${SP.sm}px`, textAlign: 'left', fontWeight: ri === 0 ? 600 : 400 }}>{cell}</Cell>;
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </div>
      ) : (
        <div data-testid="wiki-notes-empty" style={{ marginTop: SP.sm, padding: SP.md, background: C.bgInset, borderRadius: R.lg }}>
          <div style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{NOTES_TEXT.emptyTitle}</div>
          <div style={{ marginTop: SP.xxs, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>{NOTES_TEXT.emptyText}</div>
          <Button
            variant="secondary"
            size={isMobile ? 'md' : 'sm'}
            dataTestId="wiki-notes-write"
            leftIcon={<PenLine size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            style={{ marginTop: SP.sm }}
            onClick={onWrite}
          >
            {NOTES_TEXT.write}
          </Button>
        </div>
      )}
    </section>
  );
}

// ─── Правка: одно поле, без «Убрать», сносок нет ───

export function WikiNotesEditor({ value, disabled, isMobile, onChange }: {
  value: string; disabled: boolean; isMobile: boolean; onChange: (v: string) => void;
}): ReactNode {
  return (
    <section data-testid="wiki-edit-notes" data-section-name={NOTES_TEXT.section} style={{ marginTop: SP.lg }}>
      <Heading caption={NOTES_TEXT.captionEdit} />
      <div style={{ marginTop: SP.sm }}>
        <TextArea value={value} onChange={onChange} autoGrow minHeight={96} placeholder={NOTES_TEXT.placeholder} disabled={disabled} isMobile={isMobile} />
      </div>
    </section>
  );
}
