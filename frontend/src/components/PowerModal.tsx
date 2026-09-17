import { useCallback, useEffect, useRef, useState } from 'react';
import { Power, RotateCcw, Moon } from 'lucide-react';
import { Button, Modal } from './ui';
import { api, type PowerActionKind, type PowerState } from '../lib/api';
import { C, FS, MODAL_W, SP } from '../lib/design';
import { ICON_SIZE } from './ui/icons';

interface Props {
  onClose: () => void;
}

// Действия питания машины, на которой крутится продукт. Порядок — от самого разрушительного к
// самому безобидному, а главной (accent) кнопки здесь нет вовсе: ни одно из трёх не является
// «обычным» действием, и подсвеченная кнопка провоцировала бы нажать её не глядя.
const ACTIONS: { kind: PowerActionKind; label: string; icon: typeof Power; hint: string }[] = [
  { kind: 'shutdown', label: 'Выключить', icon: Power, hint: 'Машина погаснет. Поднять её удалённо будет нечем.' },
  { kind: 'restart', label: 'Перезагрузить', icon: RotateCcw, hint: 'Машина уйдёт в перезагрузку и вернётся сама.' },
  { kind: 'sleep', label: 'Усыпить', icon: Moon, hint: 'Машина уснёт; разбудить можно по сети или кнопкой.' },
];

const LABEL: Record<PowerActionKind, string> = {
  shutdown: 'Выключение',
  restart: 'Перезагрузка',
  sleep: 'Сон',
};

// Как часто пересчитываем оставшиеся секунды. Отсчёт локальный: сервер уже сказал, когда
// сработает, и лишний опрос ничего не уточнит.
const TICK_MS = 500;

export function PowerModal({ onClose }: Props) {
  const [state, setState] = useState<PowerState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Момент срабатывания как метка времени; null — ничего не запланировано
  const [runAt, setRunAt] = useState<number | null>(null);
  const [left, setLeft] = useState(0);
  const alive = useRef(true);

  useEffect(() => () => { alive.current = false; }, []);

  // Стартовый снимок. Команду мог поставить кто-то другой (или это же окно до перезагрузки
  // страницы) — тогда открытое окно обязано показать чужой обратный отсчёт, а не форму выбора.
  useEffect(() => {
    api.power.status()
      .then(s => {
        if (!alive.current) return;
        setState(s);
        if (s.pending) setRunAt(Date.now() + s.pending.secondsLeft * 1000);
      })
      .catch(() => { if (alive.current) setError('Не удалось прочитать состояние.'); });
  }, []);

  // Обратный отсчёт. Когда он доходит до нуля, команда уже ушла машине: дальше окно ничего не
  // решает, поэтому просто говорит об этом (продукт вот-вот погаснет вместе с машиной).
  useEffect(() => {
    if (runAt === null) return;
    const tick = () => setLeft(Math.max(0, Math.ceil((runAt - Date.now()) / 1000)));
    tick();
    const id = setInterval(tick, TICK_MS);
    return () => clearInterval(id);
  }, [runAt]);

  const schedule = useCallback(async (action: PowerActionKind) => {
    setError(null);
    setBusy(true);
    try {
      const accepted = await api.power.schedule(action);
      if (!alive.current) return;
      setRunAt(Date.now() + accepted.secondsLeft * 1000);
      setState(s => s ? { ...s, pending: { action, runAt: accepted.runAt, secondsLeft: accepted.secondsLeft, requestedBy: '' } } : s);
    } catch (e) {
      if (alive.current) setError((e as Error).message || 'Команда отклонена сервером.');
    } finally {
      if (alive.current) setBusy(false);
    }
  }, []);

  const cancel = useCallback(async () => {
    setError(null);
    setBusy(true);
    try {
      await api.power.cancel();
      if (!alive.current) return;
      setRunAt(null);
      setState(s => s ? { ...s, pending: null } : s);
    } catch (e) {
      if (alive.current) setError((e as Error).message || 'Отменить не вышло.');
    } finally {
      if (alive.current) setBusy(false);
    }
  }, []);

  const pendingAction = state?.pending?.action ?? null;
  const counting = runAt !== null && left > 0;
  const fired = runAt !== null && left === 0;

  return (
    <Modal width={MODAL_W.form} title="Питание компьютера" onClose={onClose} closeOnBackdrop={!counting}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontSize: FS.base, color: C.textPrimary }}>
        {error && <div style={{ color: C.danger }}>{error}</div>}

        {state && !state.available && (
          <div style={{ color: C.textMuted }}>
            Машина сервера не умеет выполнять такие команды — управление питанием доступно только
            на Windows.
          </div>
        )}

        {counting && (
          <>
            <div style={{ lineHeight: 1.5 }}>
              <b>{pendingAction ? LABEL[pendingAction] : 'Команда'}</b> через <b>{left} с</b>. Это
              последняя возможность передумать: после срабатывания продукт станет недоступен, а
              активные чаты и сессии оборвутся.
            </div>
            <Button variant="ghost" fullWidth loading={busy} onClick={() => void cancel()}>
              Отменить
            </Button>
          </>
        )}

        {fired && (
          <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
            Команда ушла машине. Отменить её уже нельзя — если продукт ещё отвечает, значит
            система только начала завершать работу.
          </div>
        )}

        {!counting && !fired && state?.available !== false && (
          <>
            <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
              Действие сработает через {state?.delaySeconds ?? 60} с — до этого его можно отменить.
              Несохранённые данные в открытых на машине программах будут потеряны: завершение
              работы принудительное, спрашивать там некому.
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              {ACTIONS.map(({ kind, label, icon: Icon, hint }) => (
                <div key={kind} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  <Button
                    variant={kind === 'shutdown' ? 'danger' : 'ghost'}
                    fullWidth
                    disabled={busy || state === null}
                    leftIcon={<Icon size={ICON_SIZE.sm} strokeWidth={2} />}
                    onClick={() => void schedule(kind)}
                  >
                    {label}
                  </Button>
                  <div style={{ color: C.textMuted, fontSize: FS.sm, lineHeight: 1.4 }}>{hint}</div>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}
