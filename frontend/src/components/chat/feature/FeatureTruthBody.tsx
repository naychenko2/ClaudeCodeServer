import { useState } from 'react';
import { ArrowRight, ChevronRight } from 'lucide-react';
import { C, FS, R, SP, FONT } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { getPersonaById } from '../../../lib/personas';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import {
  featureEstimateView, featureNowRows, featureTrackerIssues, featureTruthBar, type NameOf,
} from './featureTruthModel';
import type { FeatureRun } from '../../../types/featureRun';

// Тело «сводки фичи» — ОДНО на две поверхности (макет v3 .exp / v4 П1):
// раскрытие В2 в шапке и попап пилюли для чата БЕЗ шага (оркестратор, ревью). Порядок
// приоритета (v2 §4): «Сейчас» → «Готовность | Срок» → «По трекеру» → «Трекер
// расходится» → подвал. Пустые секции не рисуются; тревога здесь не повторяется
// (её действие — в строке). «Дальше» нет: поля нет в run (отмечено в featureTruthModel).
//
// Раскладка: columns (раскрытие В2 на десктопе) — две колонки («Сейчас» | остальное),
// подвал во всю ширину; без columns (попап 380 и мобила) — одна колонка.
//
// Данные — только run (stepProgress/now/tracker/epic.note.estimate). Фронт не
// пересчитывает: проценты и времена — из featureTruthModel (общие форматтеры).

const nameOfPersona: NameOf = (id) => getPersonaById(id)?.name ?? null;

const secStyle: React.CSSProperties = {
  fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '10px 0 4px',
};
const rowStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between', gap: SP.md, padding: '2px 0',
  alignItems: 'baseline', fontSize: FS.sm, color: C.textSecondary,
};
const rowWordStyle: React.CSSProperties = { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' };
const rowTimeStyle: React.CSSProperties = { fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 };
const tileStyle: React.CSSProperties = { padding: `${SP.sm}px 10px`, borderRadius: R.lg, background: C.bgInset, minWidth: 0 };

export function FeatureTruthBody({ run, sessionId, isMobile, roleText, isKey = false, onOpenPage, more, onCycle, columns = false }: {
  run: FeatureRun;
  sessionId: string | null;
  isMobile: boolean;
  // Подпись «этот чат — …»: из связи «чат × фича» (строка) или роли задачи (попап).
  roleText: string;
  isKey?: boolean;
  onOpenPage: () => void;
  // Несколько фич у чата: «· ещё N» в шапке раскрытия (циклический выбор — как в старой
  // плашке). В попапе выбор делает FeaturePicker — здесь его нет.
  more?: number;
  onCycle?: () => void;
  // Десктопное раскрытие В2: две колонки (макет v3 .exp). Попап и мобила — одна.
  columns?: boolean;
}) {
  const nowMs = Date.now();
  const [discOpen, setDiscOpen] = useState(false);

  const nowRows = featureNowRows(run, sessionId, nameOfPersona, nowMs);
  const est = featureEstimateView(run, nowMs);
  const sp = run.stepProgress ?? null;
  const bar = featureTruthBar(sp, sp?.inMaster);
  const issues = featureTrackerIssues(run);

  // Шапка: «Сейчас · N» (когда есть идущие) и «· ещё N» (когда у чата несколько фич —
  // переключатель работает и без «Сейчас»: фичу выбрать надо всегда).
  const headRow = (nowRows.length > 0 || (more != null && more > 0 && onCycle)) ? (
    <div style={{ ...secStyle, display: 'flex', gap: SP.sm, margin: '2px 0 4px' }}>
      {nowRows.length > 0 && <span>Сейчас · {nowRows.length}</span>}
      {more != null && more > 0 && onCycle && (
        <button
          type="button"
          data-testid="feature-truth-cycle"
          onClick={onCycle}
          aria-label={`У этого чата ещё ${more} фичи — показать следующую`}
          style={{ marginLeft: 'auto', border: 'none', background: 'transparent', cursor: 'pointer', fontFamily: 'inherit', fontSize: FS.xs, fontWeight: 600, color: C.textMuted, padding: 0 }}
        >
          · ещё {more}
        </button>
      )}
    </div>
  ) : null;

  const nowSection = nowRows.length > 0 ? (
    <>
      {nowRows.map(r => (
        <div key={r.taskId} data-testid="feature-truth-now" style={rowStyle}>
          <span style={rowWordStyle}>
            <b style={{ color: C.textHeading, fontWeight: 600 }}>{r.name ?? 'без исполнителя'}</b>
            {' — '}
            {r.what}
            {r.isThisChat && (
              <span data-testid="feature-truth-this-chat" style={{ color: C.accent, fontSize: FS.xs, fontWeight: 600 }}> · этот чат</span>
            )}
          </span>
          {r.time && <span style={rowTimeStyle}>{r.time}</span>}
        </div>
      ))}
    </>
  ) : null;

  const pairSection = est ? (
    <div data-testid="feature-truth-pair" style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.sm, marginTop: SP.sm }}>
      <div style={tileStyle}>
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>Готовность</div>
        <div style={{ fontFamily: FONT.mono, fontSize: 16, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap' }}>{est.percent} %</div>
        <div style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {est.sourceLabel}{est.ago ? ` · ${est.ago}` : ''}
        </div>
      </div>
      {est.etaMain && (
        <div style={tileStyle}>
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>Срок</div>
          <div style={{ fontFamily: FONT.mono, fontSize: 16, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap' }}>{est.etaMain}</div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{est.readyAt ?? ''}</div>
        </div>
      )}
    </div>
  ) : null;

  const meterSection = bar && sp ? (
    <div data-testid="feature-truth-meter">
      <div style={{ ...secStyle, display: 'flex', justifyContent: 'space-between', gap: SP.md, alignItems: 'baseline' }}>
        <span>По трекеру</span>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.textHeading, textTransform: 'none', letterSpacing: 0 }}>
          {bar.percent} % · {sp.accepted} из {sp.total}
        </span>
      </div>
      <FeatureTrackerBar bar={bar} ariaLabel={trackerBarAria(bar)} style={{ margin: '4px 0 2px' }} testId="feature-truth-bar" />
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '2px 10px', fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
        {/* «в master» — ортогональный счётчик бэка: реальное число (может быть больше
            «принято»), вне суммы корзин — на полосе это подсветка принятой части. */}
        {bar.inMaster > 0 && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
            <i style={{ width: 7, height: 7, borderRadius: 2, display: 'inline-block', background: C.successText }} />
            в master {bar.inMaster}
          </span>
        )}
        {bar.segments.map(s => (
          <span key={s.key} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
            <i style={{ width: 7, height: 7, borderRadius: 2, display: 'inline-block', background: s.color }} />
            {s.label} {s.count}
          </span>
        ))}
      </div>
    </div>
  ) : null;

  const discSection = issues.length > 0 ? (
    <div data-testid="feature-truth-disc">
      <button
        type="button"
        data-testid="feature-truth-disc-toggle"
        aria-expanded={discOpen}
        onClick={() => setDiscOpen(o => !o)}
        style={{ display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', border: 'none', background: 'transparent', cursor: 'pointer', padding: '2px 0', fontFamily: 'inherit', fontSize: FS.sm, color: C.warningText, textAlign: 'left' }}
      >
        <span style={{ display: 'flex', transform: discOpen ? 'rotate(90deg)' : 'none', transition: 'transform 120ms' }}>
          <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </span>
        Трекер расходится · {issues.length}
      </button>
      {discOpen && (
        <ul style={{ margin: '4px 0 0 16px', padding: 0, listStyle: 'none', fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5 }}>
          {issues.map((t, i) => <li key={`${t.text}-${i}`}>{t.text}</li>)}
        </ul>
      )}
    </div>
  ) : null;

  return (
    <div data-testid="feature-truth-body">
      {columns ? (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '4px 24px' }}>
          <div style={{ minWidth: 0 }}>{headRow}{nowSection}</div>
          <div style={{ minWidth: 0 }}>{pairSection}{meterSection}{discSection}</div>
        </div>
      ) : (
        <>{headRow}{nowSection}{pairSection}{meterSection}{discSection}</>
      )}
      {/* Подвал: общий с П2 (FeatureStepView). */}
      <FeatureTruthFooter roleText={roleText} isKey={isKey} isMobile={isMobile} onOpenPage={onOpenPage} />
    </div>
  );
}

// Подвал «этот чат — [роль] · Страница фичи →» — ОДИН на раскрытие В2 и попап П2
// (макет v3 .foot / v4 .foot): туда, где теперь живут этапы, — «Хронология» страницы фичи.
export function FeatureTruthFooter({ roleText, isKey = false, isMobile, onOpenPage }: {
  roleText: string;
  isKey?: boolean;
  isMobile: boolean;
  onOpenPage: () => void;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.md, paddingTop: SP.sm, borderTop: `1px solid ${C.bgInset}`, fontSize: FS.xs, color: C.textMuted }}>
      <span>этот чат —</span>
      <span
        data-testid="feature-truth-role"
        style={{ display: 'inline-flex', alignItems: 'center', height: 18, padding: '0 6px', borderRadius: R.sm, background: isKey ? C.accentLight : C.bgInset, color: isKey ? C.accent : C.textSecondary, fontWeight: 600, fontSize: FS.xs }}
      >
        {roleText || 'участник'}
      </span>
      <button
        type="button"
        data-testid="feature-truth-page"
        onClick={onOpenPage}
        style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: SP.xs, border: 'none', background: 'transparent', cursor: 'pointer', fontFamily: 'inherit', fontSize: FS.sm, fontWeight: 600, color: C.accent, whiteSpace: 'nowrap', flexShrink: 0, ...(isMobile ? { minHeight: 40 } : {}) }}
      >
        Страница фичи
        <ArrowRight size={12} strokeWidth={2} />
      </button>
    </div>
  );
}
