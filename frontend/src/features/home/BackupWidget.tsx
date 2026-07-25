import { useCallback, useEffect, useState } from 'react';
import { ShieldCheck } from 'lucide-react';
import type { BackupStatus, BackupEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Button, Dot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { WidgetCard, WidgetEmpty, relTime } from './WidgetCard';

function fmtSize(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} МБ`;
  return `${Math.max(1, Math.round(bytes / 1024))} КБ`;
}

// «12 чатов · 5 персон · 34 задачи» — состав снапшота словами. Числа берутся из манифеста,
// снятого вместе с архивом, поэтому строка честная даже для старых бэкапов.
function summaryLine(entry: BackupEntry): string {
  const s = entry.summary;
  const parts: string[] = [];
  if (s.chats) parts.push(`${s.chats} ${plural(s.chats, 'чат', 'чата', 'чатов')}`);
  if (s.personas) parts.push(`${s.personas} ${plural(s.personas, 'персона', 'персоны', 'персон')}`);
  if (s.tasks) parts.push(`${s.tasks} ${plural(s.tasks, 'задача', 'задачи', 'задач')}`);
  if (s.notes) parts.push(`${s.notes} ${plural(s.notes, 'заметка', 'заметки', 'заметок')}`);
  return parts.join(' · ');
}

function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}

// Бэкапы инстанса: настроен ли, когда снимался последний и что в нём. Виден только админу.
//
// Виджет ничего не настраивает — секция «Backup» правится руками в appsettings.Local.json
// (пути машинно-специфичны, а лежащая в data настройка откатывалась бы вместе с данными
// при восстановлении). Данные берём из data/backup-state.json, поэтому в папку архивов
// не ходим: она обычно синхронизируется с облаком, и на спящем OneDrive перечисление
// файлов подвесило бы дашборд.
export function BackupWidget() {
  const isMobile = useIsMobile();
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);

  const load = useCallback(() => {
    // Неудачный refresh НЕ обнуляет уже показанный статус: иначе после ручного запуска
    // виджет исчезал бы вместе с только что выставленной ошибкой, и снаружи это выглядело
    // бы как «нажал — и всё пропало». До первой удачной загрузки status и так null.
    api.backup.get().then(setStatus).catch(() => { /* оставляем прежний снимок */ });
  }, []);

  useEffect(() => { load(); }, [load]);

  const runNow = async () => {
    setRunning(true);
    setRunError(null);
    try {
      await api.backup.run();
    } catch (e) {
      // Молча гасить нельзя: снаружи это выглядело бы как «нажал, и ничего не произошло»
      setRunError(e instanceof Error ? e.message : 'Не удалось снять бэкап');
    } finally {
      setRunning(false);
      load();
    }
  };

  // Эндпоинт недоступен (старый бэкенд, сеть) — молча не показываем блок,
  // это честнее, чем карточка с пустыми полями
  if (!status) return null;

  const failed = !!status.lastError;
  const dotColor = failed ? C.danger : status.enabled ? C.success : C.textMuted;
  const problem = runError ?? status.lastError;

  return (
    <WidgetCard icon={<ShieldCheck size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} title="Бэкап">
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Dot color={dotColor} />
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary }}>
          {failed ? 'Последний бэкап не удался'
            : status.enabled ? `Настроен · раз в ${status.intervalHours} ч`
            : 'Не настроен'}
        </span>
      </div>

      {problem && (
        <div
          title={problem}
          style={{
            fontFamily: FONT.sans, fontSize: FS.xs, color: C.danger, lineHeight: 1.4,
            // Текст приходит от сервера сырым (путь без пробелов, стектрейс) —
            // без клампа он растянул бы карточку и сломал сетку дашборда
            display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical',
            overflow: 'hidden', overflowWrap: 'anywhere',
          }}
        >
          {problem}
        </div>
      )}

      {status.recent.length === 0 ? (
        <WidgetEmpty text={status.enabled
          ? 'Снимков пока нет — первый появится по расписанию'
          : 'Администратор должен настроить бэкап в файле конфигурации.'} />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {status.recent.map(entry => (
            <div key={entry.fileName} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
                <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary, flex: 1, minWidth: 0 }}>
                  {relTime(entry.createdAt)}
                </span>
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                  {fmtSize(entry.size)}
                </span>
              </div>
              <div style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
                {summaryLine(entry)}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Пока бэкап не включён в конфиге, снимать нечего и некуда — кнопку не показываем.
          На мобиле размер md: он даёт minHeight 40 под палец */}
      {status.enabled && (
        <Button
          variant="ghost"
          size={isMobile ? 'md' : 'sm'}
          loading={running}
          onClick={runNow}
          style={{ alignSelf: 'flex-start' }}
        >
          {running ? 'Снимаю…' : 'Сделать бэкап сейчас'}
        </Button>
      )}
    </WidgetCard>
  );
}
