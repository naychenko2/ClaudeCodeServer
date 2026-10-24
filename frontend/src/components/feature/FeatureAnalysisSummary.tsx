// Резюме под строкой анализа (волна 5, W5-F2; спецификация features-overview-5.md §«Точка 6 · резюме»).
// Появляется под FeatureAnalysisLine по кнопке «Анализ моделью»: запрос делает строка,
// этот компонент рисует состояние — идёт (скелет), готово (три колонки), ошибка (заглушки 502/503).
// Результат держится в состоянии строки: повторное открытие страницы не плодит запрос —
// повторный клик по кнопке берёт кэш бэка (force=false), «Обновить» — новый прогон (force=true).
// Коды шагов в тексте становятся ссылками только если токен точно равен stepRef из run.stepChains.

import type { CSSProperties, ReactNode } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';
import { ICON_SIZE, ICON_PROPS } from '../ui/icons';
import { AlertTriangle, RotateCcw, Sparkles } from 'lucide-react';
import { setFeaturePageTab, focusFeatureStage } from '../../lib/featurePage';
import { formatClock } from './FeatureStatsBits';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureSummaryResult } from '../../lib/api';

export type SummaryPhase = 'running' | 'ready' | 'error';

export interface FeatureAnalysisSummaryProps {
  run: FeatureRun;
  phase: SummaryPhase;
  data?: FeatureSummaryResult | null;
  error?: { status: number; text: string } | null;
  isMobile?: boolean;
  // «Обновить» — force=true (новый прогон); «Повторить» после 502 — без force (есть кэш — мгновенно).
  onRefresh: () => void;
  onRetry: () => void;
  onCollapse: () => void;
  // «Остановить» — отменить запрос (abort в строке) и свернуть.
  onStop: () => void;
}

// Заголовки разделов постоянные (спека): делим по строкам, которые после снятия
// «#», «*», «:» равны этим (без регистра). Заголовков нет — одна колонка без заголовка.
const HEAD_KEYS = ['сделано', 'где застряло', 'дальше'];
const HEAD_TITLES = ['Сделано', 'Где застряло', 'Дальше'];

interface SummarySection { title: string | null; items: string[]; }

export function parseSummary(text: string): SummarySection[] {
  const sections: SummarySection[] = [];
  let loose: string[] = [];
  let cur: SummarySection | null = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    const key = line.replace(/[#*:]/g, '').trim().toLowerCase();
    const idx = HEAD_KEYS.indexOf(key);
    if (idx >= 0) {
      cur = { title: HEAD_TITLES[idx], items: [] };
      sections.push(cur);
      continue;
    }
    const m = line.match(/^([-*]|\d+\.)\s+(.+)$/);
    if (!m) continue;
    if (cur) cur.items.push(m[2].trim());
    else loose.push(m[2].trim());
  }
  if (!sections.length) return [{ title: null, items: loose }];
  return sections;
}

// Коды шагов фичи: ссылкой станет только токен, точно равный stepRef (никаких «похожих» регэкспов).
function stepRefsOf(run: FeatureRun): Set<string> {
  const refs = new Set<string>();
  for (const ch of run.stepChains ?? []) if (ch.stepRef) refs.add(ch.stepRef);
  return refs;
}

// Клик по коду — «Хронология» + якорь этапа шага (тот же путь, что метки 6А).
function focusStep(run: FeatureRun, stepRef: string) {
  setFeaturePageTab('timeline');
  const chain = (run.stepChains ?? []).find(c => c.stepRef === stepRef);
  const taskId = chain?.taskIds?.[0];
  if (!taskId) return;
  const stage = (run.stages ?? []).find(s => (s.taskIds ?? []).includes(taskId));
  if (stage) focusFeatureStage(stage.key);
}

// Пункт: текст, где коды шагов — кнопки-ссылки (моно, акцент, без фона).
function renderItem(item: string, refs: Set<string>, run: FeatureRun, isMobile: boolean): ReactNode[] {
  return item.split(/\s+/).map((token, i) => refs.has(token) ? (
    <button
      key={`${i}-${token}`}
      type="button"
      data-testid={`summary-step-link-${token}`}
      onClick={() => focusStep(run, token)}
      style={{
        background: 'none', border: 'none', padding: 0, cursor: 'pointer',
        fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.accent,
        ...(isMobile ? { minHeight: 40 } : {}),
      }}
    >
      {token}
    </button>
  ) : token);
}

export function FeatureAnalysisSummary({
  run, phase, data, error, isMobile = false, onRefresh, onRetry, onCollapse, onStop,
}: FeatureAnalysisSummaryProps) {
  const box: CSSProperties = {
    marginTop: -SP.xs,
    marginBottom: SP.md,
    background: C.bgWhite,
    border: `1px solid ${C.borderLight}`,
    borderRadius: R.xl,
    padding: `${SP.sm + 2}px ${SP.md + 2}px`,
    fontSize: FS.sm,
    fontFamily: FONT.sans,
  };

  // «Идёт»: текст-подсказка + три полосы-скелета, действие — «Остановить».
  if (phase === 'running') {
    return (
      <div data-testid="feature-analysis-summary" style={box}>
        <div style={{ display: 'flex', gap: SP.xs + 2, alignItems: 'center' }}>
          <Sparkles size={ICON_SIZE.xs} {...ICON_PROPS} style={{ color: C.textMuted }} />
          <span style={{ flex: 1, minWidth: 0, color: C.textSecondary }}>Модель читает хронологию фичи…</span>
          <Button variant="ghost" size="sm" dataTestId="summary-stop" onClick={onStop}>Остановить</Button>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs + 2, marginTop: SP.xs }}>
          {[92, 78, 60].map(w => (
            <div key={w} style={{ height: 10, borderRadius: R.sm, background: C.bgSelected, width: `${w}%` }} />
          ))}
        </div>
      </div>
    );
  }

  // «Ошибка»: спокойная заглушка без toast-цепочек. 502 — причина от бэка и «Повторить»;
  // 503 — подсистема выключена, повторять бессмысленно.
  if (phase === 'error') {
    const status = error?.status ?? 0;
    return (
      <div data-testid="feature-analysis-summary" style={box}>
        <Notice
          tone={status === 503 ? 'warning' : 'danger'}
          icon={AlertTriangle}
          children={status === 503
            ? 'Анализ недоступен на этом сервере'
            : `Не удалось составить анализ: ${error?.text || 'модель не ответила'}`}
          action={status === 502 ? (
            <Button variant="secondary" size="sm" dataTestId="summary-retry" onClick={onRetry}>Повторить</Button>
          ) : undefined}
        />
      </div>
    );
  }

  // «Готово»: шапка (дата, модель, «устарело») + три колонки.
  const sections = parseSummary(data?.text ?? '');
  const refs = stepRefsOf(run);
  // «Устарело» = активность фичи позже генерации. Числа событий бэк не отдаёт — пишем просто «устарело».
  const stale = !!data?.generatedAt && !!run.lastActivityAt &&
    Date.parse(run.lastActivityAt) > Date.parse(data.generatedAt);
  const btnSize = isMobile ? 'sm' : 'xs';

  return (
    <div data-testid="feature-analysis-summary" style={box}>
      <div style={{
        display: 'flex', gap: SP.xs + 2, alignItems: 'center',
        fontSize: FS.xs, color: C.textMuted,
        ...(isMobile ? { flexWrap: 'wrap' as const } : {}),
      }}>
        <Sparkles size={ICON_SIZE.xs} {...ICON_PROPS} />
        <span>анализ · {formatClock(data?.generatedAt ?? '')} · {data?.model || 'локальная модель'}</span>
        {stale && <Badge tone="warning" size="xs">устарело</Badge>}
        <span style={{ flex: 1 }} />
        <Button
          variant="ghost" size={btnSize} dataTestId="summary-refresh"
          leftIcon={<RotateCcw size={ICON_SIZE.xs} {...ICON_PROPS} />}
          onClick={onRefresh}
        >
          Обновить
        </Button>
        <Button variant="ghost" size={btnSize} dataTestId="summary-collapse" onClick={onCollapse}>Свернуть</Button>
      </div>

      <div style={{
        display: 'grid',
        gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, 1fr)',
        gap: `${SP.sm + 2}px ${SP.lg}px`,
        marginTop: SP.sm,
      }}>
        {sections.map((s, i) => (
          <div key={i} data-testid={`summary-section-${i}`}>
            {s.title && (
              <div style={{
                fontSize: FS.xxs, fontWeight: 700, textTransform: 'uppercase',
                letterSpacing: '.05em', color: C.textMuted, marginBottom: SP.xs,
              }}>
                {s.title}
              </div>
            )}
            {s.items.length ? (
              <ul style={{ listStyle: 'none', paddingLeft: SP.lg, margin: 0 }}>
                {s.items.map((item, j) => (
                  <li key={j} style={{ marginBottom: SP.xxs }}>
                    {renderItem(item, refs, run, isMobile)}
                  </li>
                ))}
              </ul>
            ) : (
              <span style={{ color: C.textMuted }}>—</span>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
