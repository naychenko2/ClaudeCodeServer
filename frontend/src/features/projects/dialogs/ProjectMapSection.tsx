import { useCallback, useEffect, useState } from 'react';
import { FileText } from 'lucide-react';
import type { Project, MapHygieneReport } from '../../../types';
import { api } from '../../../lib/api';
import { C, FONT, FS, SP } from '../../../lib/design';
import { Button } from '../../../components/ui';
import { MapHygieneDialog } from './MapHygieneDialog';
import { AccordionSection, type AccordionSummaryTone } from './AccordionSection';

// Секция «Карта проекта (CLAUDE.md)» в настройках проекта: аккордеон со сводкой в
// заголовке и встроенной кнопкой «Проверить карту проекта», открывающей модалку с
// фактами сканера. Скан идёт через общий эндпоинт — модалка принимает уже загруженный
// отчёт и сама его не перезапрашивает: повторная загрузка только на «Проверить
// заново», когда человек видит, что файл изменился с момента скана.
interface Props {
  project: Project;
}

type ReportState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'ready'; report: MapHygieneReport };

export function ProjectMapSection({ project }: Props) {
  const [state, setState] = useState<ReportState>({ kind: 'loading' });
  const [openDialog, setOpenDialog] = useState(false);

  const reload = useCallback(async () => {
    try {
      const r = await api.projects.mapHygiene.scan(project.id);
      setState({ kind: 'ready', report: r });
    } catch {
      setState({ kind: 'error' });
    }
  }, [project.id]);

  // Применить новый отчёт (приехал из review/apply внутри модалки) к нашей сводке.
  // Если модалка закрыта — отчёт не тронется, и при следующем открытии человек увидит
  // устаревшую шапку; но это редкая ситуация, а лишний fetch при каждом обновлении —
  // хуже: на rescan'е человек только что получил свежий отчёт
  const applyReport = useCallback((r: MapHygieneReport) => {
    setState({ kind: 'ready', report: r });
  }, []);

  // Загрузка ОДИН раз на mount: отчёт дешёвый, и без неё открытие аккордеона
  // показало бы «пусто» до клика. Повторный fetch из модалки (по «Проверить заново»)
  // идёт через onReloaded
  // eslint-disable-next-line react-hooks/set-state-in-effect -- одноразовая загрузка фактов карты при открытии настроек
  useEffect(() => { void reload(); }, [reload]);

  const summary: { text: string; tone: AccordionSummaryTone } | undefined = (() => {
    if (state.kind === 'loading') return undefined;
    if (state.kind === 'error') return { text: 'Не получилось проверить', tone: 'err' };
    const report = state.report;
    if (!report.exists) return { text: 'Карты нет', tone: 'neutral' };
    const n = report.suggestions.length;
    if (n === 0) return { text: 'Карта в порядке', tone: 'ok' };
    return {
      text: `${n} ${pluralRu(n, 'замечание', 'замечания', 'замечаний')}`,
      tone: 'err',
    };
  })();

  return (
    <>
      <AccordionSection
        icon={FileText}
        title="Карта проекта (CLAUDE.md)"
        summary={summary?.text}
        summaryTone={summary?.tone}
      >
        {state.kind === 'loading' && (
          <BodyRow muted>Проверяю размер карты и состояние ссылок…</BodyRow>
        )}
        {state.kind === 'error' && (
          <>
            <BodyRow err>Сервер не ответил — попробуйте ещё раз.</BodyRow>
            <Button variant="ghost" size="sm" onClick={reload} style={{ marginTop: SP.sm }}>
              Проверить снова
            </Button>
          </>
        )}
        {state.kind === 'ready' && state.report.exists && (
          <>
            <BodyRow>{subtitleLine(state.report)}</BodyRow>
            <div style={{ marginTop: SP.md }}>
              <Button variant="primary" size="sm" onClick={() => setOpenDialog(true)}>
                Проверить карту проекта
              </Button>
            </div>
          </>
        )}
        {state.kind === 'ready' && !state.report.exists && (
          <BodyRow muted>
            В корне проекта нет <code style={{ fontFamily: FONT.mono }}>CLAUDE.md</code>.
            Ассистент сам создаст его при первом знакомстве с проектом.
          </BodyRow>
        )}
      </AccordionSection>
      {openDialog && state.kind === 'ready' && (
        <MapHygieneDialog
          projectId={project.id}
          report={state.report}
          onReloaded={reload}
          onReport={applyReport}
          onClose={() => setOpenDialog(false)}
        />
      )}
    </>
  );
}

function BodyRow({ children, muted, err }: { children: React.ReactNode; muted?: boolean; err?: boolean }) {
  return (
    <div style={{
      fontSize: FS.base,
      color: err ? C.dangerText : muted ? C.textSecondary : C.textPrimary,
      lineHeight: 1.5,
    }}>
      {children}
    </div>
  );
}

// «CLAUDE.md · 974 строки · 98 КБ · ≈33 000 токенов в каждой сессии» — дословно из таблицы
// Майи. Оценку токенов показываем как оценку: сервер пишет в ApproxTokensNote, что это
// «байты ÷ 3, точное число зависит от токенизатора модели». Строка остаётся про сам
// файл — раскрытый размер платёжный, но за него отвечает отдельный блок
function subtitleLine(report: MapHygieneReport): React.ReactNode {
  const kb = Math.round(report.bytes / 1024);
  return (
    <span>
      CLAUDE.md · {report.lines.toLocaleString('ru')} {pluralRu(report.lines, 'строка', 'строки', 'строк')} ·{' '}
      {kb.toLocaleString('ru')} КБ · ≈{report.approxTokens.toLocaleString('ru')} токенов в каждой сессии
    </span>
  );
}

function pluralRu(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}
