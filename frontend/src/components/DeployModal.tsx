import { useCallback, useEffect, useRef, useState } from 'react';
import { LoadingOverlay, Modal } from './ui';
import { api, type DeployState, type DeployStatusFile } from '../lib/api';
import { C, MODAL_W } from '../lib/design';
import { applyUpdateAndReload } from '../lib/swUpdate';
import { setDeployInProgress } from '../lib/deployState';

interface Props {
  onClose: () => void;
}

// Фазы окна. Разделение не косметическое: пока идёт выкатка, сервер лежит, и «ошибка сети»
// означает успех, а не сбой, — поэтому каждая фаза трактует одни и те же события по-своему.
type Phase =
  | 'loading'     // читаем текущее состояние
  | 'ready'       // можно запускать (или уже показан прошлый итог)
  | 'awaiting'    // команда отправлена, ждём подтверждения от трея
  | 'running'     // выкатка идёт: продукт гаснет и возвращается
  | 'done'        // итог этой выкатки
  | 'unconfirmed' // выкатка была, но опознать итог как свой не вышло
  | 'notAccepted' // трей команду не принял (см. ниже)
  | 'timeout'     // не дождались за разумное время
  | 'updating';   // применяем новую версию фронта и перезагружаемся

// Опрос в фазе awaiting — частый: окно «продукт ещё отвечает, а running уже на диске»
// длится доли секунды (раннер пишет статус, а следующим шагом гасит продукт). Редкий опрос
// это окно просто проскочит.
const AWAIT_POLL_MS = 400;
// Сколько ждать подтверждения, прежде чем признать, что трей команду не принял. Отсчёт
// ведётся ТОЛЬКО по успешным ответам: пока продукт отвечает, а startedAt не менялся,
// никто ничего не гасил — это ровно случай «удалённая выкатка выключена в конфиге трея»
// или «выкатка уже идёт», когда файл статуса не перезаписывается вовсе.
const AWAIT_LIMIT_MS = 30_000;
const RUN_POLL_MS = 2500;
// Потолок ожидания результата. Штатная публикация «как есть» — пара минут; берём с запасом
// на медленную сборку фронта и возможный откат.
const RUN_LIMIT_MS = 15 * 60_000;

const RESULT_TEXT: Record<string, { title: string; tone: string }> = {
  'ok': { title: 'Готово: продукт опубликован и отвечает', tone: C.success },
  'rolled-back': { title: 'Сборка не поднялась — возвращена предыдущая версия', tone: C.danger },
  'build-failed': { title: 'Сборка не удалась', tone: C.danger },
  'blocked': { title: 'Раннер отказался выкатывать', tone: C.textMuted },
  'failed': { title: 'Выкатка не удалась', tone: C.danger },
  'error': { title: 'Ошибка выкатки', tone: C.danger },
  'running': { title: 'Выкатка не завершилась — трей мог умереть по дороге', tone: C.danger },
};

// Подписи под логотипом на заставке: коротко про то, что происходит прямо сейчас
const OVERLAY_HINT: Partial<Record<Phase, string>> = {
  awaiting: 'Отправляю команду на выкатку',
  running: 'Выкатываю на бой — продукт перезапускается',
  updating: 'Перехожу на новую версию',
};

export function DeployModal({ onClose }: Props) {
  const [phase, setPhase] = useState<Phase>('loading');
  const [state, setState] = useState<DeployState | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Время начала ПРОШЛОЙ выкатки. Это опорная точка всей логики ожидания: пока оно не
  // изменилось, любой лежащий в файле итог относится к прошлому запуску, а не к нашему.
  // Берём из снимка, показанного в фазе ready, а не из ответа на запуск: ответ может не
  // доехать — продукт гаснет через мгновение после него.
  const baseline = useRef<string | null>(null);
  const stopped = useRef(false);

  useEffect(() => () => { stopped.current = true; setDeployInProgress(false); }, []);

  // Пока идёт выкатка, интерфейс вокруг должен помолчать: продукт остановлен намеренно, и
  // «Офлайн» в индикаторе связи с плашкой «Доступно обновление» — правда, которая сейчас
  // никому не адресована (см. lib/deployState). Снимаем флаг, как только дошли до итога.
  useEffect(() => {
    setDeployInProgress(phase === 'awaiting' || phase === 'running');
  }, [phase]);

  const load = useCallback(async () => {
    try {
      const s = await api.deploy.status();
      setState(s);
      baseline.current = s.status?.startedAt ?? null;
      setPhase('ready');
    } catch {
      setError('Не удалось прочитать состояние выкатки.');
      setPhase('ready');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  // Ожидание после нажатия. Возвращает фазу, в которую перешли.
  const watch = useCallback(async () => {
    const awaitUntil = Date.now() + AWAIT_LIMIT_MS;
    let phaseNow: Phase = 'awaiting';

    while (!stopped.current) {
      await sleep(phaseNow === 'awaiting' ? AWAIT_POLL_MS : RUN_POLL_MS);
      if (stopped.current) return;

      // Без начального значения: catch уходит на continue, поэтому ниже fresh всегда присвоен
      let fresh: DeployState;
      try {
        fresh = await api.deploy.status();
      } catch {
        // Продукт не отвечает. В фазе ожидания это ДОКАЗАТЕЛЬСТВО того, что команда принята:
        // сам себя сервер не гасит, значит его погасил трей. В фазе выкатки — просто норма.
        if (phaseNow === 'awaiting') {
          phaseNow = 'running';
          setPhase('running');
        }
        continue;
      }

      setState(fresh);
      const started = fresh.status?.startedAt ?? null;
      const result = fresh.status?.result ?? null;
      const isOurs = started !== baseline.current;

      if (phaseNow === 'awaiting') {
        if (isOurs) { phaseNow = 'running'; setPhase('running'); }
        else if (Date.now() > awaitUntil) { setPhase('notAccepted'); return; }
        continue;
      }

      // Дальше — фаза выкатки. Вердикт «трей команду не принял» здесь НЕ выносим ни при каких
      // условиях, и это не мелочь: в эту фазу попадают в том числе по обрыву связи, а обрыв уже
      // доказал, что команду приняли и продукт погасили. Прежняя версия объявляла «не принял»,
      // стоило startedAt разойтись с базисом, — и врала поверх успешной выкатки (19.08: раннер
      // отработал за 2 минуты с Result: ok, а окно рапортовало об отказе).
      if (result && result !== 'running') {
        // Штатный случай: итог наш. Сверка с базисом ловит чужой терминальный статус — краш
        // продукта с подъёмом по watchdog оставил бы в файле «ok» прошлой выкатки.
        if (isOurs) { setPhase('done'); return; }
        // Итог есть, но опознать его как свой не выходит (базис устарел, файл не обновился).
        // Врать в любую сторону нельзя: показываем, что есть, и говорим, что уверенности нет.
        setPhase('unconfirmed');
        return;
      }
      if (Date.now() > awaitUntil + RUN_LIMIT_MS) { setPhase('timeout'); return; }
    }
  }, []);

  const launch = useCallback(async () => {
    setError(null);
    setPhase('awaiting');
    try {
      // Ответ, если доехал, уточняет базис: сервер прочитал файл прямо перед сигналом, а снимок
      // из фазы «готов» мог устареть — модалку могли открыть задолго до нажатия. Полагаться
      // только на него нельзя (продукт гаснет следом, ответ может пропасть), поэтому он именно
      // уточнение поверх снимка, а не единственный источник.
      const accepted = await api.deploy.launch();
      if (accepted) baseline.current = accepted.previousStartedAt ?? null;
    } catch (e) {
      // Потеря ответа — ожидаемый исход (продукт гаснет), поэтому ошибку здесь не показываем
      // как отказ: наблюдение всё равно рассудит по файлу статуса. Реальный отказ (409/404)
      // прилетит раньше, чем что-либо погаснет, — его и покажем.
      const status = (e as { status?: number }).status;
      if (status === 409 || status === 404 || status === 403) {
        setError((e as Error).message || 'Выкатка отклонена сервером.');
        setPhase('ready');
        return;
      }
    }
    void watch();
  }, [watch]);

  const s = state?.status ?? null;

  // Пока идёт выкатка, приложение всё равно нерабочее: продукт остановлен, любой запрос падает.
  // Показывать в это время окно поверх мёртвого интерфейса — врать про доступность, поэтому
  // берём ту же заставку, что и при старте приложения.
  if (phase === 'awaiting' || phase === 'running' || phase === 'updating') {
    return <LoadingOverlay hint={OVERLAY_HINT[phase]} />;
  }

  return (
    <Modal width={MODAL_W.form} title="Выкатить на бой" onClose={onClose}
      closeOnBackdrop={phase === 'ready' || phase === 'done' || phase === 'loading'}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14, fontSize: 13.5, color: C.textPrimary }}>
        {phase === 'loading' && <div style={{ color: C.textMuted }}>Читаю состояние…</div>}

        {phase === 'ready' && (
          <>
            <LastResult status={s} />
            {error && <div style={{ color: C.danger }}>{error}</div>}
            {state && !state.canLaunch
              ? <div style={{ color: C.textMuted }}>{state.reason}</div>
              : (
                <>
                  <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
                    Публикуется рабочее дерево репозитория <b>как есть</b>, вместе с незакоммиченными
                    правками. Продукт будет остановлен: активные чаты и сессии оборвутся, веб-морда
                    будет недоступна пару минут. Если новая сборка не поднимется, раннер сам вернёт
                    предыдущую.
                  </div>
                  <button onClick={() => void launch()} style={primaryButton}>Выкатить</button>
                </>
              )}
          </>
        )}

        {/* Фазы ожидания и выкатки сюда не доходят: их целиком закрывает заставка на весь
            экран (см. ранний return выше) */}

        {phase === 'notAccepted' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <b style={{ color: C.danger }}>Трей команду не принял</b>
            <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
              Продукт всё это время отвечал, а выкатка не начиналась. Обычно это значит, что
              удалённая выкатка выключена в конфиге трея (<code>AllowRemoteDeploy</code>) или он
              уже занят другой публикацией. Подробности — в <code>tray-*.log</code>.
            </div>
            {/* Факты рядом с вердиктом: если время начала подозрительно свежее, вердикт врёт,
                и это видно сразу, без похода в логи */}
            <LastResult status={s} />
          </div>
        )}

        {phase === 'unconfirmed' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <b>Выкатка прошла, но подтвердить, что это была именно твоя, не берусь</b>
            <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
              Продукт уходил в перезапуск и вернулся, в файле статуса лежит итог — но сверить
              его с тем, что было до нажатия, не вышло. Смотри время начала: если оно похоже на
              момент нажатия, это твоя выкатка.
            </div>
            <LastResult status={s} highlight />
          </div>
        )}

        {phase === 'timeout' && (
          <div style={{ color: C.textMuted, lineHeight: 1.5 }}>
            Не дождался итога. Выкатка могла и завершиться — загляни сюда снова или проверь
            <code> deploy-status.json</code> рядом с продуктом.
          </div>
        )}

        {phase === 'done' && (
          <>
            <LastResult status={s} highlight />
            {/* Тот, кто выкатил, сидит на старом бандле: страница пережила рестарт продукта из
                кеша service worker, а плашка обновления приходит по таймеру, до минуты спустя.
                Ему свежая версия нужна прямо сейчас — он ради неё и нажимал.
                При откате кнопки нет: на бою осталась прежняя сборка, обновляться не к чему. */}
            {s?.result === 'ok' && (
              <button
                onClick={() => { setPhase('updating'); void applyUpdateAndReload(); }}
                style={primaryButton}
              >
                Перезагрузить с новой версией
              </button>
            )}
          </>
        )}
      </div>
    </Modal>
  );
}


// Карточка итога. Показывает всё, по чему потом восстанавливают «что за код на бою»:
// режим, ветку, коммит и сколько было незакоммиченных файлов.
function LastResult({ status, highlight }: { status: DeployStatusFile | null; highlight?: boolean }) {
  if (!status) return <div style={{ color: C.textMuted }}>Выкаток ещё не было.</div>;

  const known = RESULT_TEXT[status.result ?? ''];
  const title = known?.title ?? `Состояние: ${status.result ?? 'неизвестно'}`;

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: 6,
      padding: highlight ? '10px 12px' : 0,
      borderRadius: 8,
      background: highlight ? C.bgSelected : 'transparent',
    }}>
      <b style={{ color: known?.tone ?? C.textPrimary }}>{title}</b>
      {status.note && <div style={{ color: C.textMuted }}>{status.note}</div>}
      <div style={{ color: C.textMuted, fontSize: 12.5, lineHeight: 1.6 }}>
        {status.startedAt} → {status.finishedAt ?? '…'}<br />
        режим {status.mode ?? '—'}, ветка {status.branch ?? '—'}, коммит {status.head ?? '—'}
        {status.dirtyFiles > 0 && `, незакоммиченных файлов: ${status.dirtyFiles}`}
        {status.productUp === false && <><br />продукт не отвечает</>}
      </div>
    </div>
  );
}

const primaryButton: React.CSSProperties = {
  padding: '9px 14px', borderRadius: 8, border: 'none', cursor: 'pointer',
  background: C.accent, color: C.onAccent, fontSize: 13.5, fontWeight: 600, fontFamily: 'inherit',
};

const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
