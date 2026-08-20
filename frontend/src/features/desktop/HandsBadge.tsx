import { useCallback, useEffect, useState } from 'react';
import { MonitorSmartphone } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { Button } from '../../components/ui';
import type { DesktopHandsChatStatus, Session } from '../../types';

// Бейдж «руки на …» в шапке десктопного чата (ADR-008). Три вещи разом: идут ли руки,
// у какого устройства, и «Стоп» — вне канала агента (разрыв делает сервер, а не просьба
// к модели остановиться).
//
// Состояние берём отдельным запросом, а не из ленты: событие сеанса эфемерное, и после
// перезагрузки страницы бейдж погас бы при живых руках. Начать сеанс отсюда нельзя ни
// при каких условиях — эта дверь на самом устройстве, веб-морда может только попросить.

const POLL_MS = 5000;

export function HandsBadge({ session }: { session: Session }) {
  const [status, setStatus] = useState<DesktopHandsChatStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const desktop = session.desktopChat === true;

  const reload = useCallback(async () => {
    if (!desktop) return;
    const s = await api.devices.handsChat(session.id).catch(() => null);
    if (s) setStatus(s);
  }, [desktop, session.id]);

  useEffect(() => {
    if (!desktop) return;
    void reload();
    const timer = setInterval(() => void reload(), POLL_MS);
    return () => clearInterval(timer);
  }, [desktop, reload]);

  if (!desktop || !status) return null;

  const ask = async () => {
    setBusy(true);
    try {
      await api.devices.handsRequest(session.id);
      await reload();
    } catch { /* отказ гейта виден следующим статусом */ }
    finally { setBusy(false); }
  };

  const stop = async () => {
    setBusy(true);
    try {
      await api.devices.handsStop(session.id);
      await reload();
    } catch { /* сеанс мог погаснуть сам — статус покажет */ }
    finally { setBusy(false); }
  };

  // Грань чату не выдана (тумблер проекта снят, флаг выключен) — говорим ровно то,
  // что ответил сервер: причин ровно столько, сколько он назвал.
  if (status.facetRefusal) {
    return <Pill tone="muted" title={status.facetRefusal}>Руки недоступны</Pill>;
  }

  if (status.active) {
    const device = status.session?.device;
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
        <Pill tone="ok" title="Сеанс рук идёт: устройство исполняет вызовы этого чата">
          {device ? `Руки на ${device}` : 'Руки подключены'}
        </Pill>
        <Button variant="ghost" size="xs" loading={busy} onClick={() => void stop()}
          title="Остановить сеанс рук. Разрыв делает сервер, а не просьба к модели">
          Стоп
        </Button>
      </span>
    );
  }

  if (status.requestedAt) {
    return (
      <Pill tone="wait" title="Заявка ушла на устройство: сеанс начинает человек в окне клиента">
        Ждём подтверждения на устройстве
      </Pill>
    );
  }

  return (
    <Button variant="ghost" size="xs" loading={busy} onClick={() => void ask()}
      title="Поставить заявку в очередь клиента. Начать сеанс может только человек у машины"
      leftIcon={<MonitorSmartphone size={13} strokeWidth={2.2} />}>
      Попросить руки
    </Button>
  );
}

function Pill({ tone, title, children }: {
  tone: 'ok' | 'wait' | 'muted';
  title: string;
  children: React.ReactNode;
}) {
  const palette = tone === 'ok'
    ? { bg: C.successBg, fg: C.successText }
    : tone === 'wait'
      ? { bg: C.warningBg, fg: C.warningText }
      : { bg: C.bgPanel, fg: C.textMuted };

  return (
    <span
      title={title}
      style={{
        flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 4,
        fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, letterSpacing: '0.02em',
        padding: '1px 7px', borderRadius: R.pill,
        background: palette.bg, color: palette.fg, whiteSpace: 'nowrap',
      }}
    >
      <MonitorSmartphone size={11} strokeWidth={2.2} />
      {children}
    </span>
  );
}
