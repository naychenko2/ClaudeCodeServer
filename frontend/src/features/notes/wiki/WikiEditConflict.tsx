// Сравнение при 409 wiki_page_changed (F4b, ADR-038 «Контракт для фронта», макет
// notes-wiki-unified-v1, экран «Конфликт 409»): по секциям «Моё / ИИ / Было».
// «Моё» — текст человека (conflict.mine), «ИИ» — свежая версия (conflict.theirs),
// «Было» — версия, с которой начата правка. Решает человек одним из трёх действий;
// ничего не уходит на сервер без явного клика (отправку и замки держит редактор).

import { AlertTriangle } from 'lucide-react';
import { C, FS, R, SP, useIsMobile, type WikiConflictPage } from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { Notice } from '../../../components/ui/Notice';
import { EDIT_TEXT } from './wikiLabels';
import { scanEditable } from './WikiPageEditor';

export interface Conflict { mine: string; theirs: WikiConflictPage | null }

export type SectionVerdict = 'both' | 'mine' | 'ai' | 'same';

export interface SectionCompare {
  name: string;
  verdict: SectionVerdict;
  was: string[];
  mine: string[];
  ai: string[];
}

function linesBySection(content: string): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const it of scanEditable(content).items) {
    const list = out.get(it.section) ?? [];
    list.push(it.text);
    out.set(it.section, list);
  }
  return out;
}

const same = (a: string[], b: string[]) => a.length === b.length && a.every((x, i) => x === b[i]);

// Секции, где версии расходятся, в порядке «моя → ИИ → была». Совсем не тронутые не попадают
export function compareSections(was: string, mine: string, theirs: string): SectionCompare[] {
  const w = linesBySection(was), m = linesBySection(mine), t = linesBySection(theirs);
  const names = [...new Set([...m.keys(), ...t.keys(), ...w.keys()])];
  const out: SectionCompare[] = [];
  for (const name of names) {
    const wl = w.get(name) ?? [], ml = m.get(name) ?? [], tl = t.get(name) ?? [];
    const mineChanged = !same(ml, wl), aiChanged = !same(tl, wl);
    if (!mineChanged && !aiChanged) continue;
    const verdict: SectionVerdict = mineChanged && aiChanged ? (same(ml, tl) ? 'same' : 'both') : mineChanged ? 'mine' : 'ai';
    out.push({ name, verdict, was: wl, mine: ml, ai: tl });
  }
  return out;
}

const VERDICT_TEXT: Record<SectionVerdict, string> = {
  both: EDIT_TEXT.conflictBoth,
  mine: EDIT_TEXT.conflictOnlyMine,
  ai: EDIT_TEXT.conflictOnlyAi,
  same: EDIT_TEXT.conflictSame,
};

function Column({ testId, label, lines, was, tint }: { testId: string; label: string; lines: string[]; was: string[]; tint?: boolean }) {
  return (
    <div data-testid={testId} style={{ minWidth: 0, padding: SP.sm, borderRadius: R.md, background: tint ? C.accentLight : C.bgInset }}>
      <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, marginBottom: SP.xs }}>{label}</div>
      {lines.length === 0 && <div style={{ fontSize: FS.sm, color: C.textMuted }}>{EDIT_TEXT.conflictRemoved}</div>}
      {lines.map((l, i) => (
        <div
          key={i}
          style={{
            fontSize: FS.sm, lineHeight: 1.5, overflowWrap: 'anywhere', color: C.textPrimary,
            background: was.includes(l) ? undefined : C.diffAddBg, borderRadius: R.sm,
          }}
        >
          {l}
        </div>
      ))}
    </div>
  );
}

export function WikiEditConflict({ conflict, was, busy, onTakeMine, onTakeAi, onKeepEditing, onClose }: {
  conflict: Conflict;
  // Текст страницы, с которым человек начал правку
  was: string;
  busy: boolean;
  onTakeMine: () => void;
  onTakeAi: () => void;
  onKeepEditing: () => void;
  onClose: () => void;
}) {
  const isMobile = useIsMobile();
  const size = isMobile ? 'md' : 'sm';
  const theirs = conflict.theirs;
  const rows = theirs ? compareSections(was, conflict.mine, theirs.content) : [];
  const wide = rows.filter(r => r.verdict === 'both');
  const rest = rows.filter(r => r.verdict !== 'both');
  return (
    <div data-testid="wiki-edit-conflict" style={{ marginTop: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="warning" size="xs">{EDIT_TEXT.conflictBadge}</Badge>
      </div>
      <div style={{ marginTop: SP.sm }}>
        <Notice
          tone="warning"
          icon={AlertTriangle}
          title={EDIT_TEXT.conflictTitle}
          action={!theirs
            ? <Button variant="secondary" size={size} dataTestId="wiki-edit-conflict-close" onClick={onClose}>{EDIT_TEXT.closeNoSave}</Button>
            : undefined}
        >
          {theirs ? EDIT_TEXT.conflictText : EDIT_TEXT.conflictGone}
        </Notice>
      </div>

      {theirs && (
        <>
          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.md }}>
            <Button variant="primary" size={size} dataTestId="wiki-conflict-take-mine" disabled={busy} loading={busy} onClick={onTakeMine}>{EDIT_TEXT.takeMine}</Button>
            <Button variant="secondary" size={size} dataTestId="wiki-conflict-take-ai" disabled={busy} onClick={onTakeAi}>{EDIT_TEXT.takeAi}</Button>
            <Button variant="ghost" size={size} dataTestId="wiki-conflict-keep" disabled={busy} onClick={onKeepEditing}>{EDIT_TEXT.keepEditing}</Button>
          </div>

          {wide.map(r => (
            <div key={r.name} data-testid="wiki-conflict-section" data-section-name={r.name} data-verdict={r.verdict}
              style={{ marginTop: SP.md, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: SP.md }}>
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'baseline', marginBottom: SP.sm }}>
                <b style={{ fontSize: FS.md, color: C.textHeading }}>{r.name}</b>
                <span style={{ fontSize: FS.sm, color: C.warningText }}>{VERDICT_TEXT[r.verdict]}</span>
              </div>
              <div style={{ display: 'grid', gap: SP.sm, gridTemplateColumns: isMobile ? 'minmax(0,1fr)' : 'repeat(3, minmax(0,1fr))' }}>
                <Column testId="wiki-conflict-col-mine" label={EDIT_TEXT.conflictMine} lines={r.mine} was={r.was} tint />
                <Column testId="wiki-conflict-col-ai" label={EDIT_TEXT.conflictAi} lines={r.ai} was={r.was} />
                <Column testId="wiki-conflict-col-was" label={EDIT_TEXT.conflictWas} lines={r.was} was={r.was} />
              </div>
            </div>
          ))}

          {rest.map(r => (
            <div key={r.name} data-testid="wiki-conflict-section" data-section-name={r.name} data-verdict={r.verdict}
              style={{ marginTop: SP.sm, padding: SP.sm, borderRadius: R.md, background: C.successBg, fontSize: FS.sm, color: C.successText }}>
              <b>{r.name}</b> · {VERDICT_TEXT[r.verdict]}
            </div>
          ))}
        </>
      )}
    </div>
  );
}
