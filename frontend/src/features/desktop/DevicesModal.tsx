import { useCallback, useEffect, useRef, useState } from 'react';
import { Laptop, Plug, ShieldOff } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Button, ConfirmDialog, EmptyState, Modal } from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import type { DesktopDevice, DesktopPairingCode } from '../../types';

// Раздел «Устройства» — модалка из меню аватара (ADR-008, вторая волна): что подключено
// к рукам и как подключить новое.
//
// Здесь ровно одна операция с секретом — выпуск кода сопряжения. Код показывается человеку,
// человек вводит его в окне клиента; API-ключ владельца и его JWT на устройство не уезжают
// никогда, устройство получает СВОЙ токен и только его.

const POLL_MS = 3000;

export function DevicesModal({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  const [devices, setDevices] = useState<DesktopDevice[] | null>(null);
  const [pairing, setPairing] = useState<DesktopPairingCode | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [revoking, setRevoking] = useState<DesktopDevice | null>(null);
  // Сколько устройств было в момент выпуска кода: прибавилось — сопряжение состоялось
  const countAtStart = useRef<number | null>(null);

  const reload = useCallback(async () => {
    const list = await api.devices.list().catch(() => null);
    if (list) setDevices(list);
    return list;
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  // Пока висит код — опрашиваем список: клиент сопрягается на своей стороне, и веб-морда
  // узнаёт об этом только по появившемуся устройству. Заодно снимаем истёкший код.
  useEffect(() => {
    if (!pairing) return;
    const timer = setInterval(() => {
      void (async () => {
        const list = await reload();
        if (list && countAtStart.current != null && list.length > countAtStart.current) {
          setPairing(null);
          countAtStart.current = null;
          return;
        }
        if (new Date(pairing.expiresAt).getTime() <= Date.now()) setPairing(null);
      })();
    }, POLL_MS);
    return () => clearInterval(timer);
  }, [pairing, reload]);

  const startPairing = async () => {
    setBusy(true);
    setErr('');
    try {
      countAtStart.current = devices?.length ?? 0;
      setPairing(await api.devices.startPairing());
    } catch (e: unknown) {
      setErr(e instanceof Error && e.message ? e.message : 'Не удалось выпустить код');
    } finally {
      setBusy(false);
    }
  };

  const cancelPairing = async () => {
    setPairing(null);
    countAtStart.current = null;
    await api.devices.cancelPairing().catch(() => { /* заявка уже истекла — это не новость */ });
  };

  const revoke = async (device: DesktopDevice) => {
    setRevoking(null);
    setErr('');
    try {
      await api.devices.revoke(device.id);
      await reload();
    } catch (e: unknown) {
      setErr(e instanceof Error && e.message ? e.message : 'Не удалось отозвать устройство');
    }
  };

  const live = (devices ?? []).filter(d => !d.revoked);

  return (
    <>
      <Modal
        title="Устройства"
        subtitle="Компьютеры, которым можно отдать руки в десктопном чате: что подключено и как подключить новое."
        width={MODAL_W.form}
        onClose={onClose}
      >
        {err && (
          <div style={{
            background: C.dangerBg, color: C.dangerText, border: `1px solid ${C.dangerBorder}`,
            borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm,
            fontFamily: FONT.sans, marginBottom: SP.md,
          }}>
            {err}
          </div>
        )}

        {pairing
          ? <PairingCard code={pairing} onCancel={() => void cancelPairing()} isMobile={isMobile} />
          : (
            <div style={{ marginBottom: SP.md }}>
              <Button
                variant="primary" size="md" loading={busy} fullWidth={isMobile}
                leftIcon={<Plug size={15} strokeWidth={2.2} />}
                onClick={() => void startPairing()}
              >
                Подключить устройство
              </Button>
            </div>
          )}

        {devices !== null && live.length === 0 && !pairing && (
          <EmptyState
            compact
            icon={<Laptop size={20} strokeWidth={2} />}
            title="Устройств пока нет"
            subtitle="Поставьте AI Home Desktop на свой компьютер и введите там код подключения."
          />
        )}

        {live.map(d => (
          <div key={d.id} style={{
            display: 'flex', alignItems: 'center', gap: SP.md,
            padding: `${SP.sm}px 0`, borderBottom: `1px solid ${C.borderLight}`,
          }}>
            <Laptop size={16} strokeWidth={2} style={{ color: C.textMuted, flexShrink: 0 }} />
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ fontSize: FS.base, color: C.textPrimary, fontFamily: FONT.sans }}>{d.name}</div>
              <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>
                {/* Отпечаток — примета машины для человека, а не проверка */}
                {d.fingerprint}
                {d.clientVersion ? ` · клиент ${d.clientVersion}` : ''}
                {d.lastSeenAt
                  ? ` · последний раз на связи ${new Date(d.lastSeenAt).toLocaleString('ru-RU')}`
                  : ' · ещё не выходило на связь'}
              </div>
            </div>
            <Button
              variant="ghost" size="sm"
              leftIcon={<ShieldOff size={14} strokeWidth={2.2} />}
              onClick={() => setRevoking(d)}
            >
              Отозвать
            </Button>
          </div>
        ))}
      </Modal>

      {revoking && (
        <ConfirmDialog
          title="Отозвать устройство?"
          subtitle={`Токен устройства «${revoking.name}» перестанет работать немедленно, а идущий сеанс рук погаснет. Подключить его снова можно новым кодом.`}
          confirmLabel="Отозвать"
          confirmVariant="danger"
          onConfirm={() => void revoke(revoking)}
          onCancel={() => setRevoking(null)}
        />
      )}
    </>
  );
}

// Карточка выпущенного кода: сам код, сколько ему жить и сколько попыток осталось.
// Код не секрет длительного действия — он живёт 5 минут и сгорает после пятой ошибки,
// поэтому показывается прямо, без «показать/скрыть».
function PairingCard({ code, onCancel, isMobile }: {
  code: DesktopPairingCode; onCancel: () => void; isMobile: boolean;
}) {
  const [left, setLeft] = useState(() => Math.max(0, new Date(code.expiresAt).getTime() - Date.now()));

  useEffect(() => {
    const timer = setInterval(
      () => setLeft(Math.max(0, new Date(code.expiresAt).getTime() - Date.now())), 1000);
    return () => clearInterval(timer);
  }, [code.expiresAt]);

  const mm = Math.floor(left / 60000);
  const ss = Math.floor((left % 60000) / 1000);

  return (
    <div style={{
      background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
      padding: SP.lg, marginBottom: SP.md,
    }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: SP.sm }}>
        Введите этот код в окне клиента AI Home Desktop на подключаемом компьютере.
      </div>
      <div style={{
        fontFamily: FONT.mono, fontSize: isMobile ? 26 : 30, fontWeight: 700,
        letterSpacing: '0.18em', color: C.accent, userSelect: 'all',
      }}>
        {code.code}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans, marginTop: SP.xs }}>
        {left > 0
          ? `Годен ещё ${mm}:${String(ss).padStart(2, '0')} · попыток осталось ${code.attemptsLeft}`
          : 'Код истёк — выпустите новый'}
      </div>
      <div style={{ marginTop: SP.md, display: 'flex', gap: SP.sm }}>
        <Button variant="ghost" size="sm" onClick={onCancel}>Отменить</Button>
      </div>
    </div>
  );
}
