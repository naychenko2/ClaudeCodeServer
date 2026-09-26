import { useCallback, useEffect, useRef, useState } from 'react';
import { Laptop, Loader2, Plug, ServerOff, ShieldOff } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Button, ConfirmDialog, EmptyState, Modal, Notice, SegmentedControl } from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import {
  agentInstallCommand, fetchAgentManifest, guessAgentOs,
  type AgentManifestState, type AgentOs,
} from '../../lib/agentInstall';
import { CopyCommand } from './AgentCommands';
import type { DesktopDevice, DesktopPairingCode, DeviceAgentUpdate } from '../../types';

// Раздел «Устройства» — модалка из меню аватара (ADR-008, вторая волна): что подключено
// к рукам и как подключить новое.
//
// Здесь ровно одна операция с секретом — выпуск кода сопряжения. Код показывается человеку,
// человек вводит его в окне клиента; API-ключ владельца и его JWT на устройство не уезжают
// никогда, устройство получает СВОЙ токен и только его.
//
// Под флагом local-projects (agent-distribution AD-7) код уходит не в окно клиента, а в
// строку установки агента одной командой; без флага модалка остаётся модалкой клиента рук.

const POLL_MS = 3000;

export function DevicesModal({ onClose }: { onClose: () => void }) {
  const isMobile = useIsMobile();
  const agentMode = useFeature(FLAGS.localProjects);
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
        subtitle={agentMode
          ? 'Компьютеры с агентом AI Home: на них живут локальные проекты и руки десктопного чата.'
          : 'Компьютеры, которым можно отдать руки в десктопном чате: что подключено и как подключить новое.'}
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
          ? (agentMode
            ? <AgentInstallCard code={pairing} onCancel={() => void cancelPairing()} isMobile={isMobile} />
            : <PairingCard code={pairing} onCancel={() => void cancelPairing()} isMobile={isMobile} />)
          : (
            <div style={{ marginBottom: SP.md }}>
              <Button
                variant="primary" size="md" loading={busy} fullWidth={isMobile}
                leftIcon={<Plug size={15} strokeWidth={2.2} />}
                onClick={() => void startPairing()}
              >
                {agentMode ? 'Подключить компьютер' : 'Подключить устройство'}
              </Button>
            </div>
          )}

        {devices !== null && live.length === 0 && !pairing && (
          <EmptyState
            compact
            icon={<Laptop size={20} strokeWidth={2} />}
            title="Устройств пока нет"
            subtitle={agentMode
              ? 'Нажмите «Подключить компьютер» и выполните одну команду в терминале этого компьютера.'
              : 'Поставьте AI Home Desktop на свой компьютер и введите там код подключения.'}
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
                {agentMode && d.agentVersion
                  ? ` · агент ${d.agentVersion}`
                  : d.clientVersion ? ` · клиент ${d.clientVersion}` : ''}
                {d.lastSeenAt
                  ? ` · последний раз на связи ${new Date(d.lastSeenAt).toLocaleString('ru-RU')}`
                  : ' · ещё не выходило на связь'}
              </div>
              {agentMode && <AgentUpdateLine update={d.agentUpdate} />}
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

// Самообновление агента одной строкой под именем: idle и пустое состояние не показываем
function AgentUpdateLine({ update }: { update?: DeviceAgentUpdate | null }) {
  if (!update || update.state === 'idle') return null;
  const [text, color] =
    update.state === 'downloading'
      ? [update.targetVersion ? `Обновляется до ${update.targetVersion}` : 'Обновляется', C.textSecondary]
      : update.state === 'waiting-idle'
        ? [`Обновление ждёт: ${update.reason ?? 'агент занят работой'}`, C.warningText]
        : [`Обновление не удалось: ${update.reason ?? 'причина неизвестна'}`, C.dangerText];
  return (
    <div style={{ fontSize: FS.xs, color, fontFamily: FONT.sans, marginTop: SP.xxs }}>
      {text}
    </div>
  );
}

// Сколько коду жить: тикает раз в секунду, общий для обеих карточек
function useCodeLeft(expiresAt: string): number {
  const [left, setLeft] = useState(() => Math.max(0, new Date(expiresAt).getTime() - Date.now()));
  useEffect(() => {
    const timer = setInterval(
      () => setLeft(Math.max(0, new Date(expiresAt).getTime() - Date.now())), 1000);
    return () => clearInterval(timer);
  }, [expiresAt]);
  return left;
}

function formatLeft(left: number): string {
  const mm = Math.floor(left / 60000);
  const ss = Math.floor((left % 60000) / 1000);
  return `${mm}:${String(ss).padStart(2, '0')}`;
}

const cardStyle = {
  background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
  padding: SP.lg, marginBottom: SP.md,
} as const;

// Карточка выпущенного кода: сам код, сколько ему жить и сколько попыток осталось.
// Код не секрет длительного действия — он живёт 5 минут и сгорает после пятой ошибки,
// поэтому показывается прямо, без «показать/скрыть».
function PairingCard({ code, onCancel, isMobile }: {
  code: DesktopPairingCode; onCancel: () => void; isMobile: boolean;
}) {
  const left = useCodeLeft(code.expiresAt);

  return (
    <div style={cardStyle}>
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
          ? `Годен ещё ${formatLeft(left)} · попыток осталось ${code.attemptsLeft}`
          : 'Код истёк — выпустите новый'}
      </div>
      <div style={{ marginTop: SP.md, display: 'flex', gap: SP.sm }}>
        <Button variant="ghost" size="sm" onClick={onCancel}>Отменить</Button>
      </div>
    </div>
  );
}

// Подключение компьютера одной командой (Р10): код вшит в строку установки, строку собираем
// из адреса, с которого открыта веб-морда. Сервер не раздаёт агента (503 на манифест) —
// команда бессмысленна, вместо неё объяснение.
function AgentInstallCard({ code, onCancel, isMobile }: {
  code: DesktopPairingCode; onCancel: () => void; isMobile: boolean;
}) {
  const left = useCodeLeft(code.expiresAt);
  const [os, setOs] = useState<AgentOs>(() => guessAgentOs());
  const [manifest, setManifest] = useState<AgentManifestState>({ kind: 'loading' });

  useEffect(() => {
    let alive = true;
    void fetchAgentManifest().then(m => { if (alive) setManifest(m); });
    return () => { alive = false; };
  }, []);

  const expired = left <= 0;
  // Без агента на раздаче ждать некого: код бесполезен, остаётся только закрыть карточку
  const unavailable = manifest.kind === 'unavailable';

  return (
    <div style={cardStyle}>
      {manifest.kind === 'unavailable' ? (
        <Notice icon={ServerOff} title="Сервер не раздаёт агента">
          Установить агента одной командой сейчас нельзя: {manifest.reason}. Попросите
          администратора выложить релиз агента на сервер и выпустите код заново.
        </Notice>
      ) : (
        <>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: SP.md }}>
            Выполните команду в терминале компьютера, который подключаете: она поставит агента
            AI Home и подключит его к вашему аккаунту. Права администратора не нужны.
          </div>
          <div style={{ marginBottom: SP.md }}>
            <SegmentedControl<AgentOs>
              value={os}
              onChange={setOs}
              options={[{ value: 'windows', label: 'Windows' }, { value: 'linux', label: 'Linux' }]}
            />
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs }}>
            {os === 'windows' ? 'PowerShell' : 'Терминал'}
            {manifest.kind === 'served' ? ` · агент ${manifest.version}` : ''}
          </div>
          {manifest.kind === 'loading'
            ? <div style={{ minHeight: SP.xxxl }} />
            : <CopyCommand command={agentInstallCommand(os, window.location.origin, code.code)} />}
        </>
      )}

      {!unavailable && <div style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm,
        marginTop: SP.md, fontSize: FS.xs, fontFamily: FONT.sans, lineHeight: 1.45,
        color: expired ? C.dangerText : C.textMuted,
      }}>
        {!expired && <Loader2 size={13} strokeWidth={2.2} style={{ animation: 'spin 1s linear infinite', flexShrink: 0, marginTop: SP.xxs }} />}
        <span style={{ minWidth: 0 }}>
          {expired
            ? 'Код истёк — выпустите новый'
            : `Ждём, пока компьютер подключится · код ${code.code} годен ещё ${formatLeft(left)}`}
        </span>
      </div>}
      <div style={{ marginTop: SP.md, display: 'flex', gap: SP.sm }}>
        <Button variant="ghost" size="sm" fullWidth={isMobile} onClick={onCancel}>
          {expired || unavailable ? 'Закрыть' : 'Отменить'}
        </Button>
      </div>
    </div>
  );
}
