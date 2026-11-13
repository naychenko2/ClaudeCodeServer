// Блок «Вход и транскрипт» экрана этапа (D1c, F17): слепок этапа, постановка тремя сворачиваемыми
// секциями, прочитанные файлы чипами, таблица инструментов и переход в чат исполнителя.
// Презентационный: данные приходят пропсами из ответа GET /api/tasks/{id}/stage; «×N» у файлов и
// «Раз / Последний» у инструментов считаются на клиенте по списку вызовов (бэк их не отдаёт).

import { useState, type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { Chip } from '../ui/Chip';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { SEC_LABEL } from './FeatureStageArtifacts';
import type { StageInput, StageSnapshot, StageToolCall } from '../../types/featureStage';

const PRE: CSSProperties = {
  margin: 0, padding: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textPrimary,
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
  whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: 320, overflowY: 'auto',
};

function join(list: string[] | null | undefined): string | null {
  return list && list.length > 0 ? list.join(', ') : null;
}

function snapshotRows(s: StageSnapshot): ReviewKvRow[] {
  const models = [s.tierStrong && `сильная: ${s.tierStrong}`, s.tierMedium && `средняя: ${s.tierMedium}`, s.tierWeak && `слабая: ${s.tierWeak}`]
    .filter((x): x is string => !!x);
  return [
    { label: 'Роль', value: s.role || null },
    { label: 'Уровень по умолчанию', value: s.defaultTier ?? null },
    { label: 'Модели', value: join(models) },
    { label: 'Запрещённые инструменты', value: join(s.disallowedTools) },
    { label: 'Разрешённые MCP', value: join(s.mcpAllow) },
    { label: 'Таймаут без движения', value: s.stallMinutes != null ? `${s.stallMinutes} мин${s.stallPolicy ? ` · ${s.stallPolicy}` : ''}` : null },
  ];
}

// ---- Постановка: разбор на три секции по заголовкам «## …» (TaskExecutionService.BuildPersonaPrompt)

export interface PromptSection { key: 'rules' | 'stage' | 'task'; title: string; text: string }

const TITLES: Record<PromptSection['key'], string> = {
  rules: 'ПРАВИЛА · ДЕЛЕГИРОВАНИЕ', stage: '## ЭТАП', task: 'ЗАДАЧА · КОНТЕКСТ',
};

// ЭТАП — своя секция; ЗАДАЧА, КОНТЕКСТ и текст до первого заголовка — «задача»; остальное
// (ПРАВИЛА, ДЕЛЕГИРОВАНИЕ, ПРЕДГЕЙТ…) — «правила». Пустые секции не возвращаются.
export function splitPromptSections(prompt: string): PromptSection[] {
  const buckets: Record<PromptSection['key'], string[]> = { rules: [], stage: [], task: [] };
  let key: PromptSection['key'] = 'task';
  for (const line of prompt.split('\n')) {
    const m = /^##\s+(.+?)\s*$/.exec(line);
    if (m) {
      const name = m[1].toUpperCase();
      key = name === 'ЭТАП' ? 'stage' : name === 'ЗАДАЧА' || name === 'КОНТЕКСТ' ? 'task' : 'rules';
      if (key === 'stage') continue; // заголовок показан в шапке секции
    }
    buckets[key].push(line);
  }
  return (['rules', 'stage', 'task'] as const)
    .map(k => ({ key: k, title: TITLES[k], text: buckets[k].join('\n').trim() }))
    .filter(sec => sec.text.length > 0);
}

export function formatChars(n: number): string {
  return `${n.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ')} знаков`;
}

// ---- Агрегаты по списку вызовов

export interface ToolRow { name: string; count: number; last: string | null }

function clock(iso: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

export function toolRows(calls: StageToolCall[]): ToolRow[] {
  const map = new Map<string, { count: number; lastMs: number | null }>();
  for (const c of calls) {
    const ms = c.at ? new Date(c.at).getTime() : NaN;
    const cur = map.get(c.name) ?? { count: 0, lastMs: null };
    cur.count++;
    if (!Number.isNaN(ms) && (cur.lastMs == null || ms > cur.lastMs)) cur.lastMs = ms;
    map.set(c.name, cur);
  }
  return [...map.entries()]
    .map(([name, v]) => ({ name, count: v.count, last: v.lastMs == null ? null : clock(new Date(v.lastMs).toISOString()) }))
    .sort((a, b) => b.count - a.count);
}

export interface FileChip { path: string; name: string; count: number }

// «×N» — число чтений файла по вызовам Read, у которых summary = путь (если вызовов в списке нет — 1).
export function fileChips(files: string[], calls: StageToolCall[]): FileChip[] {
  const reads = new Map<string, number>();
  for (const c of calls) if (c.name === 'Read' && c.summary) reads.set(c.summary, (reads.get(c.summary) ?? 0) + 1);
  return files.map(path => ({ path, name: path.split('/').pop() || path, count: Math.max(1, reads.get(path) ?? 1) }));
}

const FILES_SHOWN = 6;

function Collapsible({ id, title, children, isMobile, hint, badge, defaultOpen = false }: {
  id: string; title: string; children: React.ReactNode; isMobile: boolean;
  hint?: string; badge?: string; defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div data-testid={id} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={SEC_LABEL}>{title}</span>
        {badge && <span data-testid={`${id}-badge`}><Chip dense>{badge}</Chip></span>}
        {hint && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span>}
        <Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40, marginLeft: 'auto' } : { marginLeft: 'auto' }}
          dataTestId={`${id}-toggle`} ariaLabel={`${open ? 'Скрыть' : 'Показать'}: ${title}`}
          onClick={() => setOpen(v => !v)}>
          {open ? 'Скрыть' : 'Показать'}
        </Button>
      </div>
      {open && children}
    </div>
  );
}

function FilesChips({ chips, isMobile }: { chips: FileChip[]; isMobile: boolean }) {
  const [all, setAll] = useState(false);
  const shown = all ? chips : chips.slice(0, FILES_SHOWN);
  const rest = chips.length - shown.length;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
      {shown.map(f => (
        <span key={f.path} data-testid="stage-file-chip" style={{ fontFamily: FONT.mono, minWidth: 0 }}>
          <Chip title={f.path} maxW="100%">{f.count > 1 ? `${f.name} ×${f.count}` : f.name}</Chip>
        </span>
      ))}
      {(rest > 0 || all) && chips.length > FILES_SHOWN && (
        <span data-testid="stage-files-more">
          <Chip variant="toggle" minHeight={isMobile ? 40 : undefined} onClick={() => setAll(v => !v)}>
            {all ? 'свернуть' : `ещё ${rest}`}
          </Chip>
        </span>
      )}
    </div>
  );
}

const TH: CSSProperties = { textAlign: 'left', fontSize: FS.xxs, fontWeight: 600, color: C.textMuted, padding: `${SP.xxs}px ${SP.sm}px`, borderBottom: `1px solid ${C.borderLight}` };
const TD: CSSProperties = { fontSize: FS.xs, padding: `${SP.xxs}px ${SP.sm}px`, borderBottom: `1px solid ${C.borderLight}`, overflowWrap: 'anywhere' };

export function FeatureStageInput({ input, isMobile, templateName, executorSessionId, onOpenChat }: {
  input: StageInput; isMobile: boolean;
  // Название шаблона для плашки у секции «## ЭТАП» (B07); нет — берём id шаблона из слепка
  templateName?: string | null;
  // Чат исполнителя (B07): без него кнопки перехода нет
  executorSessionId?: string | null;
  onOpenChat?: (sessionId: string) => void;
}) {
  const snap = input.stage;
  const tr = input.transcript;
  const sections = tr?.prompt ? splitPromptSections(tr.prompt) : [];
  const stageBadge = snap ? `из шаблона «${templateName || snap.templateId}» v${snap.templateVersion}` : undefined;
  return (
    <section data-testid="stage-input" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Вход и транскрипт</div>
      {snap ? (
        <>
          <div data-testid="stage-snapshot"><ReviewKv rows={snapshotRows(snap)} /></div>
          {snap.prompt && !sections.some(x => x.key === 'stage') && (
            <Collapsible id="stage-snapshot-prompt" title="Промпт этапа" isMobile={isMobile}>
              <pre style={PRE}>{snap.prompt}</pre>
            </Collapsible>
          )}
        </>
      ) : (
        <div data-testid="stage-snapshot-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Слепка этапа нет: задача создана без шаблона.
        </div>
      )}
      {!input.transcriptAvailable || !tr ? (
        <div data-testid="stage-transcript-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Транскрипт старого хода не разобран — откройте чат исполнителя.
        </div>
      ) : (
        <>
          {sections.length > 0 && (
            <div data-testid="stage-prompt" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
              <div style={SEC_LABEL}>Постановка исполнителю</div>
              {sections.map(sec => (
                <Collapsible key={sec.key} id={`stage-prompt-${sec.key}`} title={sec.title} isMobile={isMobile}
                  defaultOpen={sec.key === 'stage'}
                  hint={sec.key === 'task' ? formatChars(sec.text.length) : sec.key === 'rules' ? 'общие' : undefined}
                  badge={sec.key === 'stage' ? stageBadge : undefined}>
                  <pre style={PRE}>{sec.text}</pre>
                </Collapsible>
              ))}
            </div>
          )}
          <div data-testid="stage-files-read" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            <div style={SEC_LABEL}>Прочитанные файлы · {tr.filesRead.length}</div>
            {tr.filesRead.length > 0 && <FilesChips chips={fileChips(tr.filesRead, tr.toolCalls)} isMobile={isMobile} />}
          </div>
          <div data-testid="stage-tool-calls" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
            <div style={SEC_LABEL}>Вызванные инструменты</div>
            {tr.toolCalls.length > 0 && (
              <table style={{ borderCollapse: 'collapse', width: '100%', tableLayout: 'fixed' }}>
                <thead>
                  <tr>
                    <th style={TH}>Инструмент</th>
                    <th style={{ ...TH, width: 56, textAlign: 'right' }}>Раз</th>
                    <th style={{ ...TH, width: 84 }}>Последний</th>
                  </tr>
                </thead>
                <tbody>
                  {toolRows(tr.toolCalls).map(r => (
                    <tr key={r.name} data-testid="stage-tool-row">
                      <td style={{ ...TD, fontFamily: FONT.mono }}>{r.name}</td>
                      <td style={{ ...TD, textAlign: 'right' }}>{r.count}</td>
                      <td style={{ ...TD, color: C.textSecondary }}>{r.last ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {tr.truncated && (
              <div style={{ fontSize: FS.xs, color: C.textMuted }}>Посчитано по первым {tr.toolCalls.length} из {tr.toolCallsTotal} вызовов.</div>
            )}
          </div>
        </>
      )}
      {executorSessionId && onOpenChat && (
        <div>
          <Button size="sm" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
            dataTestId="stage-input-open-chat" onClick={() => onOpenChat(executorSessionId)}>
            Весь транскрипт в чате исполнителя →
          </Button>
        </div>
      )}
    </section>
  );
}
