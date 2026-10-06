// Строка превью последнего сообщения в карточке чата. Отдельным файлом — чтобы тест не тянул
// всю карточку. Порядок: сводка хода механики команды, затем сообщение сторожа (иконка
// будильника вместо цветного эмодзи), иначе текст как есть
import { AlarmClock } from 'lucide-react';
import { C } from '../lib/design';
import { splitWatchdogPreview } from '../lib/watchdogPreview';
import { teamTurnPreview } from '../features/team/teamMechanics';

// Иконка будильника — по кеглю строки превью (12): ICON_SIZE.xs (14) выше строки 12/1.4,
// так же мимо шкалы сделан MessageOriginChip
const ALARM_ICON = 12;

export function ChatPreviewLine({ text }: { text: string }) {
  const team = teamTurnPreview(text);
  if (team != null) return <>{team}</>;
  const wd = splitWatchdogPreview(text);
  if (!wd.watchdog) return <>{text}</>;
  return (
    <>
      <AlarmClock size={ALARM_ICON} strokeWidth={2} color={C.textMuted} aria-label="Сторож"
        style={{ display: 'inline-block', verticalAlign: '-1px', marginRight: 4 }} />
      {wd.text}
    </>
  );
}
