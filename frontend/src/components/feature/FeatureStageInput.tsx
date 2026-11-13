// Блок «Вход и транскрипт» экрана этапа (D1c): слепок этапа (промпт этапа отдельно, модель,
// ограничения, таймаут), текст постановки (свёрнут), прочитанные файлы и вызванные инструменты.
// Презентационный: всё приходит пропсами из ответа GET /api/tasks/{id}/stage как есть.

import { useState, type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { SEC_LABEL } from './FeatureStageArtifacts';
import type { StageInput, StageSnapshot } from '../../types/featureStage';

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

function Collapsible({ id, title, children, isMobile }: { id: string; title: string; children: React.ReactNode; isMobile: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <div data-testid={id} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={SEC_LABEL}>{title}</span>
        <Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
          dataTestId={`${id}-toggle`} ariaLabel={`${open ? 'Скрыть' : 'Показать'}: ${title}`}
          onClick={() => setOpen(v => !v)}>
          {open ? 'Скрыть' : 'Показать'}
        </Button>
      </div>
      {open && children}
    </div>
  );
}

export function FeatureStageInput({ input, isMobile }: { input: StageInput; isMobile: boolean }) {
  const snap = input.stage;
  const tr = input.transcript;
  return (
    <section data-testid="stage-input" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Вход и транскрипт</div>
      {snap ? (
        <>
          <div data-testid="stage-snapshot"><ReviewKv rows={snapshotRows(snap)} /></div>
          {snap.prompt && (
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
          Транскрипт недоступен: чат исполнителя не привязан или закрыт для вас.
        </div>
      ) : (
        <>
          {tr.prompt && (
            <Collapsible id="stage-prompt" title="Постановка исполнителю" isMobile={isMobile}>
              <pre style={PRE}>{tr.prompt}</pre>
            </Collapsible>
          )}
          <div data-testid="stage-files-read" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            <div style={SEC_LABEL}>Прочитанные файлы · {tr.filesRead.length}</div>
            {tr.filesRead.map(f => (
              <span key={f} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>{f}</span>
            ))}
          </div>
          <Collapsible id="stage-tool-calls" title={`Вызовы инструментов · ${tr.toolCallsTotal}`} isMobile={isMobile}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
              {tr.toolCalls.map((c, i) => (
                <div key={i} data-testid="stage-tool-call" style={{ fontSize: FS.xs, overflowWrap: 'anywhere', color: c.isError ? C.dangerText : C.textSecondary }}>
                  <span style={{ fontFamily: FONT.mono, fontWeight: 600 }}>{c.name}</span>
                  {c.summary ? ` · ${c.summary}` : ''}{c.isError ? ' · ошибка' : ''}
                </div>
              ))}
              {tr.truncated && (
                <div style={{ fontSize: FS.xs, color: C.textMuted }}>Показаны первые {tr.toolCalls.length} из {tr.toolCallsTotal}.</div>
              )}
            </div>
          </Collapsible>
        </>
      )}
    </section>
  );
}
