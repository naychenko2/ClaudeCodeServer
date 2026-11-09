// Строка «Предгейт» в карточке шага/доработки: последний вердикт авто-проверок
// (бэк TaskItem.Pregate, record PregateRecord). У прочих ролей строки нет — предгейт
// прогоняют только шаг и доработка. Без записи — «не прогонялся»; бэковский вердикт
// «нечего проверять» (NothingApplicable) показываем так же: бэк сам относит его к NotRun
// (TaskPregateService.Format).

import type { Task } from '../../types';
import { C, FONT, FS } from '../../lib/design';
import { Badge, type BadgeTone } from '../../components/ui/Badge';

// HH:MM из отметки записи (она UTC) — в локальной зоне смотрящего, та же конвенция,
// что у отметок в истории решений (DossierHistoryPanel)
function timeLabel(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

export function PregateLine({ task }: { task: Pick<Task, 'role' | 'pregate'> }) {
  const role = task.role;
  if (role !== 'step' && role !== 'rework') return null;
  const p = task.pregate;

  let tone: BadgeTone;
  let verdict: string;
  let meta = '';
  if (!p || p.nothingApplicable) {
    // Нет свидетельства авто-проверок — тот же сигнал, что «Нет свидетельства»
    // у критерия приёмки рядом (AcceptanceBlock), поэтому тот же тон
    tone = 'warning';
    verdict = 'не прогонялся';
  } else if (p.green) {
    tone = 'success';
    verdict = 'зелёный';
    // Привязка к коммиту — техническое значение, оно в mono; время — только у зелёного:
    // у красного ревьюеру нужен вердикт, а не отметка прогона
    const t = timeLabel(p.at);
    meta = `по ${p.sha.slice(0, 7)}${t ? `, ${t}` : ''}`;
  } else {
    tone = 'danger';
    // Серия — сколько красных подряд по той же причине: «2» — сигнал эскалации (PregateRules)
    verdict = `красный (серия ${p.redStreak})`;
  }

  return (
    <div data-testid="pregate-line" style={{ marginBottom: 26 }}>
      <span style={{
        fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textMuted,
        lineHeight: 1.6, overflowWrap: 'anywhere',
      }}>
        Предгейт:{' '}
        <Badge tone={tone} size="xs" dot>{verdict}</Badge>
        {meta && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}> {meta}</span>}
      </span>
    </div>
  );
}
