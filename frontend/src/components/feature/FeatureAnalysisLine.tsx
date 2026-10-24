// Строка анализа на вкладке «Статистика» (волна 5, W5-F1; спецификация features-overview-5.md §«Точка 6»).
// Первый ребёнок вкладки, до значков точности и Ганта: короб с маркером, фразой и действием.
// Состояние выбирает чистая функция analysisLineState — компонент только рисует.
// Без `run` (старые ответы) строки нет; пустых состояний у строки не бывает — худшее — other.

import { useEffect, useRef, useState } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { Badge } from '../ui/Badge';
import { Dot } from '../ui/Dot';
import { Button } from '../ui/Button';
import { ICON_SIZE, ICON_PROPS } from '../ui/icons';
import { Bell, Check, Clock, Moon, Sparkles } from 'lucide-react';
import { openChatById } from '../../lib/openChat';
import { api, type FeatureSummaryResult } from '../../lib/api';
import type { NavMarks } from '../../lib/featureNavMarks';
import type { FeatureRun } from '../../types/featureRun';
import { analysisLineState, type AnalysisBadge, type AnalysisLineState } from './FeatureAnalysisModel';
import { FeatureAnalysisSummary } from './FeatureAnalysisSummary';

const BADGE_ICON: Record<AnalysisBadge['icon'], typeof Clock> = {
  Clock, Bell, Moon, Check,
};

// Тон короба: фон и рамка по таблице спецификации (quiet — пунктир warning).
function boxStyle(st: AnalysisLineState, isMobile: boolean) {
  const bg = st.tone === 'danger' ? C.dangerBg : st.tone === 'warning' ? C.warningBg : C.bgCard;
  const border = st.dashed
    ? `1px dashed ${C.warning}`
    : st.tone === 'danger' ? `1px solid ${C.dangerBorder}`
    : st.tone === 'warning' ? `1px solid ${C.warning}`
    : `1px solid ${C.borderLight}`;
  return {
    display: 'flex',
    alignItems: isMobile ? 'flex-start' : 'center',
    gap: SP.sm,
    minHeight: 36,
    padding: isMobile ? `${SP.sm}px ${SP.sm + 2}px` : `${SP.xs + 2}px ${SP.sm + 2}px`,
    borderRadius: R.lg,
    fontSize: FS.sm,
    marginBottom: SP.md,
    background: bg,
    border,
    fontFamily: FONT.sans,
    ...(isMobile ? { flexWrap: 'wrap' as const } : {}),
  };
}

export interface FeatureAnalysisLineProps {
  run: FeatureRun;
  // Пометки страницы: чат в архиве — кнопки «Открыть чат» нет (фраза остаётся).
  marks?: NavMarks;
  isMobile?: boolean;
}

export function FeatureAnalysisLine({ run, marks, isMobile = false }: FeatureAnalysisLineProps) {
  const st = analysisLineState(run, Date.now());

  // Резюме (W5-F2): состояние живёт здесь — «результат держится в состоянии страницы».
  // Открыто и готово — кнопки «Анализ моделью» в строке нет (действия — в шапке резюме);
  // идёт — кнопка остаётся, но disabled.
  const [openSummary, setOpenSummary] = useState(false);
  const [summaryData, setSummaryData] = useState<FeatureSummaryResult | null>(null);
  const [summaryError, setSummaryError] = useState<{ status: number; text: string } | null>(null);
  const [summaryRunning, setSummaryRunning] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  // Смена фичи: WorkspacePage переиспользует этот экземпляр с другим run, а состояние
  // привязано к featureId (спека §«Точка 6»). Сброс состояния — здесь; отмена висящего
  // запроса — в useEffect по run.featureId (вердикт W5-R-F2a, п.4: без побочного
  // эффекта abort в рендере). Без отмены ответ старого запроса вернулся бы под другой
  // фичей и «Анализ моделью» не запросил бы ничего.
  const lastFeatureRef = useRef(run.featureId);
  if (lastFeatureRef.current !== run.featureId) {
    lastFeatureRef.current = run.featureId;
    setOpenSummary(false);
    setSummaryData(null);
    setSummaryError(null);
    setSummaryRunning(false);
  }

  // Cleanup-форма (вердикт W5-R-F2c, п.2): покрывает и смену фичи, и размонтирование —
  // висящий запрос снимается в обоих случаях.
  useEffect(() => () => abortRef.current?.abort(), [run.featureId]);

  // force=false — берётся кэш бэка; force=true — новый прогон («Обновить»).
  const fetchSummary = (force: boolean) => {
    if (summaryRunning) return;
    const ac = new AbortController();
    const fid = run.featureId;
    abortRef.current = ac;
    setSummaryRunning(true);
    setSummaryError(null);
    api.tasks.featureSummary(fid, force, ac.signal)
      .then(d => {
        // Ответ принимает только текущий запрос (вердикт W5-R-F2c, п.1; половина про
        // «другой запрос» убрана по вердикту W5-R-F2d, п.1: новый запрос не начинается,
        // пока старый «идёт» (summaryRunning не пускает второй fetchSummary). Если фича
        // сменилась — старый ответ не липнет.
        if (lastFeatureRef.current !== fid) return;
        setSummaryData(d);
      })
      .catch(e => {
        // Симметрия с .then (вердикт W5-R-F2d, п.2): ошибка старого запроса не пишет
        // заглушку под другой фичей.
        if (lastFeatureRef.current !== fid) return;
        if (ac.signal.aborted) return; // «Остановить» — не ошибка.
        const err = e as Error & { status?: number; body?: { error?: string } };
        setSummaryError({ status: err.status ?? 0, text: err.body?.error || err.message });
      })
      .finally(() => {
        // Страховка (вердикт W5-R-F2a, п.3): гасим состояние, только если этот запрос
        // всё ещё текущий — иначе старый finally затрёт «идёт» нового запроса.
        if (abortRef.current === ac) {
          abortRef.current = null;
          setSummaryRunning(false);
        }
      });
  };

  // Чат шага в архиве или пуст — действие «Открыть чат» снимается; «Анализ моделью»
  // снимается только когда резюме раскрыто и готово (спека: «когда резюме раскрыто —
  // кнопки в строке нет»).
  const actions = st.actions.filter(
    a => a.kind === 'analysis'
      ? !(openSummary && !summaryRunning)
      : (a.sessionId && !marks?.chatArchived(a.sessionId)),
  );

  const open = (sessionId: string) => { void openChatById(sessionId); };
  // Компонент иконки бейджа — до JSX: тег не принимает computed-индекс.
  const BadgeIcon = st.badge ? BADGE_ICON[st.badge.icon] : null;

  // Хвост показываем целиком, когда он есть. Спека (features-overview-5.md, «Мобильная 360»)
  // убирает на 360 лишь клаузу «, следом …» — модель для moving её не строит, поэтому
  // по ключу состояния хвост не скрывать: на 360 остаются и «{Шаг} у {Имя} {dur}.»,
  // и «Последнее событие {dur} назад.».

  return (
    <>
    <div data-testid="feature-analysis-line" style={boxStyle(st, isMobile)}>
      {st.badge && BadgeIcon ? (
        <Badge tone={st.badge.tone} size="xs" icon={<BadgeIcon size={ICON_SIZE.xs} {...ICON_PROPS} />}>
          {st.badge.text}
        </Badge>
      ) : (
        <span style={{ display: 'inline-block', boxShadow: st.dot?.glow ? SHADOW.focus : undefined }}>
          <Dot color={st.dot!.color} size={7} />
        </span>
      )}

      <span style={{ flex: 1, minWidth: 0, color: C.textPrimary }}>
        {st.strong && <b style={{ color: C.textHeading }}>{st.strong}</b>}
        {st.tail && (
          <span style={{ color: C.textMuted }}> {st.tail}</span>
        )}
      </span>

      <div style={{ display: 'flex', gap: SP.xs + 2, ...(isMobile ? { flexBasis: '100%', flexDirection: 'column' as const } : {}) }}>
        {actions.map((a, i) => (
          a.kind === 'analysis' ? (
            <Button
              key="analysis"
              variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              leftIcon={<Sparkles size={ICON_SIZE.xs} {...ICON_PROPS} />}
              dataTestId="analysis-line-summary"
              disabled={summaryRunning}
              onClick={() => {
                setOpenSummary(true);
                // Повторный клик — без force: результат уже в состоянии, не плодим запрос.
                if (!summaryData) fetchSummary(false);
              }}
            >
              {a.label}
            </Button>
          ) : (
            <Button
              key={`chat-${i}`}
              variant={a.variant} size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              onClick={() => open(a.sessionId!)}
              dataTestId={`analysis-line-open-chat-${i}`}
            >
              {a.label}
            </Button>
          )
        ))}
      </div>
    </div>

    {openSummary && (
      <FeatureAnalysisSummary
        run={run}
        phase={summaryRunning ? 'running' : summaryError ? 'error' : 'ready'}
        data={summaryData}
        error={summaryError}
        isMobile={isMobile}
        onRefresh={() => fetchSummary(true)}
        onRetry={() => fetchSummary(false)}
        onCollapse={() => setOpenSummary(false)}
        onStop={() => { abortRef.current?.abort(); setOpenSummary(false); }}
      />
    )}
    </>
  );
}
