import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, ExternalLink, Play, RefreshCw, Square } from 'lucide-react';
import { Badge, Button, Modal } from './ui';
import type { BadgeTone } from './ui';
import { api, type RemoteCommandAction, type RemoteCommandResult, type RemoteCommandState } from '../lib/api';
import { C, FONT, FS, MODAL_W, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useIsMobile } from '../lib/breakpoints';

interface Props {
  onClose: () => void;
}

// Как часто переспрашиваем состояние, пока по действию идёт ЧУЖАЯ операция (второе окно,
// соседний админ). Свою мы не поллим вовсе — её итог придёт ответом на тот же запрос.
// Дёшево: refresh во время busy не берёт gate и не исполняет команд, а отдаёт кэш.
const BUSY_POLL_MS = 2500;

// Подписи состояний. `unknown` — это «проверить не удалось», а не «остановлен»: пульт
// обязан говорить об этом словами, иначе человек прочитает честное незнание как поломку.
const STATE_TEXT: Record<RemoteCommandState, string> = {
  running: 'Запущен',
  stopped: 'Остановлен',
  unknown: 'Состояние неизвестно',
};

const STATE_TONE: Record<RemoteCommandState, BadgeTone> = {
  running: 'success',
  stopped: 'neutral',
  unknown: 'warning',
};

// Пояснение к состоянию, когда сервер не приложил своего detail. Для unknown оно
// обязательно: бейдж один, а причин у незнания много.
const STATE_HINT: Partial<Record<RemoteCommandState, string>> = {
  unknown: 'Проверить состояние не удалось — команда проверки не отработала.',
};

export function RemoteCommandsModal({ onClose }: Props) {
  const isMobile = useIsMobile();
  const [actions, setActions] = useState<RemoteCommandAction[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Причина по действию: короткий итог последней операции или отказ сервера
  const [details, setDetails] = useState<Record<string, string | null>>({});
  // Операции, запущенные ИЗ ЭТОГО окна. Отдельно от serverside busy: по ним не нужен
  // поллинг (ответ придёт сам), а кнопки обязаны показывать спиннер сразу по нажатию.
  const [pending, setPending] = useState<Record<string, boolean>>({});
  const [openOutput, setOpenOutput] = useState<Record<string, boolean>>({});
  const [outputs, setOutputs] = useState<Record<string, string | null>>({});
  const alive = useRef(true);

  useEffect(() => () => { alive.current = false; }, []);

  // Итог операции ложится поверх карточки: сервер уже прогнал Status и вернул конечное
  // состояние, второй запрос за ним не нужен.
  const applyResult = useCallback((key: string, r: RemoteCommandResult) => {
    if (!alive.current) return;
    setActions(list => list?.map(a => a.key === key ? {
      ...a,
      state: r.state,
      busy: r.busy ?? false,
      checkedAt: r.checkedAt ?? a.checkedAt,
      lastExitCode: r.lastExitCode ?? a.lastExitCode,
    } : a) ?? list);
    setDetails(d => ({ ...d, [key]: r.detail ?? null }));
  }, []);

  const refreshOne = useCallback(async (key: string) => {
    try {
      applyResult(key, await api.remoteCommands.refresh(key));
    } catch (e) {
      if (alive.current) setDetails(d => ({ ...d, [key]: (e as Error).message || 'Обновить состояние не вышло.' }));
    }
  }, [applyResult]);

  // Стартовый снимок: список отдаёт КЭШ статуса, команд не исполняет. Свежесть догоняем
  // явным refresh — по одному действию за раз, чтобы открытие пульта не порождало
  // N процессов на машине разом.
  useEffect(() => {
    let stopped = false;
    void (async () => {
      let snapshot: RemoteCommandAction[];
      try {
        const s = await api.remoteCommands.status();
        if (!alive.current || stopped) return;
        snapshot = s.enabled ? s.actions : [];
        setActions(snapshot);
      } catch {
        if (alive.current) setError('Не удалось прочитать список действий.');
        return;
      }
      for (const a of snapshot) {
        if (!alive.current || stopped) return;
        await refreshOne(a.key);
      }
    })();
    return () => { stopped = true; };
  }, [refreshOne]);

  // Чужая операция: пока сервер говорит busy, переспрашиваем — иначе второе окно так и
  // осталось бы с надписью «идёт операция» до ручного обновления.
  const busyKeys = (actions ?? []).filter(a => a.busy && !pending[a.key]).map(a => a.key).join(',');
  useEffect(() => {
    if (!busyKeys) return;
    const id = setInterval(() => {
      for (const key of busyKeys.split(',')) void refreshOne(key);
    }, BUSY_POLL_MS);
    return () => clearInterval(id);
  }, [busyKeys, refreshOne]);

  const run = useCallback(async (key: string, op: 'start' | 'stop') => {
    setPending(p => ({ ...p, [key]: true }));
    setDetails(d => ({ ...d, [key]: null }));
    try {
      applyResult(key, await api.remoteCommands[op](key));
    } catch (e) {
      if (!alive.current) return;
      setDetails(d => ({ ...d, [key]: (e as Error).message || 'Команда отклонена сервером.' }));
      // Отказ (409 «занято», 404, обрыв) ничего не говорит о состоянии действия —
      // спрашиваем сервер, вместо того чтобы догадываться.
      await refreshOne(key);
    } finally {
      if (alive.current) setPending(p => ({ ...p, [key]: false }));
    }
  }, [applyResult, refreshOne]);

  const toggleOutput = useCallback(async (key: string) => {
    const willOpen = !openOutput[key];
    setOpenOutput(o => ({ ...o, [key]: willOpen }));
    if (!willOpen) return;
    try {
      const { text } = await api.remoteCommands.output(key);
      if (alive.current) setOutputs(o => ({ ...o, [key]: text }));
    } catch {
      if (alive.current) setOutputs(o => ({ ...o, [key]: null }));
    }
  }, [openOutput]);

  const empty = actions !== null && actions.length === 0;

  return (
    <Modal width={MODAL_W.form} title="Пульт управления" onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontSize: FS.base, color: C.textPrimary }}>
        {error && <div style={{ color: C.danger }}>{error}</div>}

        {actions === null && !error && <div style={{ color: C.textMuted }}>Читаю состояние…</div>}

        {empty && (
          <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
            Действий нет. Список объявляется в конфиге сервера на диске
            (<code>RemoteCommands</code> в <code>appsettings.Local.json</code>) и применяется
            после его перезапуска — из веб-морды команды не заводятся.
          </div>
        )}

        {actions !== null && actions.length > 0 && (
          <>
            <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
              Действия объявлены в конфиге сервера; отсюда их можно только запускать и
              останавливать. Состояние сервер берёт из команды проверки, а не из памяти,
              поэтому оно честно и после его перезапуска.
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              {actions.map(a => (
                <ActionCard
                  key={a.key}
                  action={a}
                  detail={details[a.key] ?? null}
                  pending={!!pending[a.key]}
                  isMobile={isMobile}
                  outputOpen={!!openOutput[a.key]}
                  output={outputs[a.key] ?? null}
                  onRun={op => void run(a.key, op)}
                  onRefresh={() => void refreshOne(a.key)}
                  onToggleOutput={() => void toggleOutput(a.key)}
                />
              ))}
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}

interface CardProps {
  action: RemoteCommandAction;
  detail: string | null;
  pending: boolean;
  isMobile: boolean;
  outputOpen: boolean;
  output: string | null;
  onRun: (op: 'start' | 'stop') => void;
  onRefresh: () => void;
  onToggleOutput: () => void;
}

// Карточка одного действия. Accent-кнопки здесь нет вовсе, и это осознанно: действий на
// экране несколько и равноправных, а подсвеченная оранжевым кнопка в каждой карточке
// превратила бы дисциплину «один акцент на экран» в гирлянду.
function ActionCard({ action, detail, pending, isMobile, outputOpen, output, onRun, onRefresh, onToggleOutput }: CardProps) {
  const busy = pending || action.busy;
  const hint = detail ?? STATE_HINT[action.state] ?? null;

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm,
      padding: SP.md, borderRadius: R.xl,
      background: C.bgCard, border: `1px solid ${C.borderLight}`,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <div style={{ fontWeight: 600, flex: 1, minWidth: 0 }}>{action.title}</div>
        {busy
          ? <Badge tone="info" dot>Идёт операция…</Badge>
          : <Badge tone={STATE_TONE[action.state]} dot>{STATE_TEXT[action.state]}</Badge>}
      </div>

      {hint && (
        <div style={{ color: C.textMuted, fontSize: FS.sm, lineHeight: 1.5, whiteSpace: 'pre-wrap' }}>
          {hint}
        </div>
      )}

      <div style={{ color: C.textMuted, fontSize: FS.xs }}>
        {action.checkedAt
          ? `Проверено ${formatChecked(action.checkedAt)}`
          : 'Ещё не проверялось'}
        {action.lastExitCode !== null && `, код выхода ${action.lastExitCode}`}
      </div>

      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button
          variant="ghost"
          size={isMobile ? 'md' : 'sm'}
          disabled={busy}
          loading={pending}
          leftIcon={<Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          style={{ flex: isMobile ? 1 : undefined }}
          onClick={() => onRun('start')}
        >
          Запустить
        </Button>
        <Button
          variant="ghost"
          size={isMobile ? 'md' : 'sm'}
          disabled={busy}
          leftIcon={<Square size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          style={{ flex: isMobile ? 1 : undefined }}
          onClick={() => onRun('stop')}
        >
          Остановить
        </Button>
        <Button
          variant="ghost"
          size={isMobile ? 'md' : 'sm'}
          title="Проверить состояние заново"
          // Кнопка НЕ блокируется по busy: refresh не берёт gate действия и во время чужой
          // операции честно отдаёт кэш с пометкой «занято»
          leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          style={{ flex: isMobile ? 1 : undefined }}
          onClick={onRefresh}
        >
          Обновить
        </Button>
        {action.url && (
          <Button
            variant="ghost"
            size={isMobile ? 'md' : 'sm'}
            title={action.url}
            // Ссылка из конфига действия (например, vscode.dev/tunnel/…): открывается в новой
            // вкладке и не блокируется по busy — чтение страницы от состояния не зависит
            href={action.url}
            leftIcon={<ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            style={{ flex: isMobile ? 1 : undefined }}
          >
            Открыть
          </Button>
        )}
      </div>

      <div>
        <Button
          variant="ghost"
          size="xs"
          leftIcon={outputOpen
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={onToggleOutput}
        >
          Вывод
        </Button>
        {outputOpen && (
          <pre style={{
            margin: `${SP.sm}px 0 0`, padding: SP.sm, borderRadius: R.md,
            background: C.termBg, color: C.termText,
            fontFamily: FONT.mono, fontSize: FS.xs, lineHeight: 1.5,
            maxHeight: 240, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          }}>
            {output === null ? 'Вывода нет.' : output || 'Вывода нет.'}
          </pre>
        )}
      </div>
    </div>
  );
}

// Время последней проверки — местное и без даты: пульт смотрят «сейчас», а полная метка
// в карточке только шумит.
function formatChecked(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}
