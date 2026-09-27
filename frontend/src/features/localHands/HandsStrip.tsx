// Полоса «Руки» над композером (реестр composer-strip; макет docs/mockups/local-hands-strip.html).
// Первая строка — переключатель полос от хоста, точка тона и статус, «Стоп» пока руки
// держат ход; вторая — причина остановки, устройство и провайдер чата с пометкой, что он
// видит. Свёрнутая и мобильная — одна строка 30 px. Общие функции агента устройства
// (статус, версия, папки, обновления) сюда не выносим — они живут в настройках проекта.

import { useState, type ReactNode } from 'react';
import { MonitorSmartphone, Square } from 'lucide-react';
import { interruptSession } from '../../lib/signalr';
import { handsProviderLabel, handsStripView, handsBadgeStatus, type HandsBadgeTone } from '../../lib/localHands';
import { C, FS, SP } from '../../lib/design';
import { showToast } from '../../lib/toast';
import { Button, Dot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { ComposerStripCtx } from '../../lib/subsystems/registryCore';
import { useProviders } from '../../lib/models';
import { getHandsProject, getHandsSession, useHandsStripVersion } from './handsStrip';

const TONE_DOT: Record<HandsBadgeTone, string> = {
  success: C.success,
  warning: C.warning,
  neutral: C.textMuted,
};

export const handsStripIcon = <MonitorSmartphone size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

// Строка меню переключателя «Git ▾»
export function handsStripStatus({ projectId, sessionId }: { projectId: string; sessionId: string | null }): ReactNode {
  if (!sessionId) return null;
  const status = handsBadgeStatus(getHandsSession(sessionId).state);
  return handsStripView(status, getHandsProject(projectId)?.deviceName ?? null).text;
}

export function HandsStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { projectId, sessionId, isMobile, collapsed, switcher } = ctx;
  useHandsStripVersion();
  // Зрение провайдера — из каталога /api/models; хук перерисует полосу, когда каталог догрузится
  const providers = useProviders();
  const [busy, setBusy] = useState(false);
  if (!sessionId) return null;

  const session = getHandsSession(sessionId);
  const project = getHandsProject(projectId);
  const view = handsStripView(handsBadgeStatus(session.state), project?.deviceName ?? null);
  const device = project?.deviceName ?? null;
  const provider = handsProviderLabel(session.provider, providers);
  const detail = [view.detail?.replace(/\.$/, ''), device, provider].filter(Boolean).join(' · ');

  const stop = async () => {
    setBusy(true);
    try { await interruptSession(sessionId); }
    catch { /* ход мог закончиться сам — следующее событие покажет */ }
    finally { setBusy(false); }
  };

  const oneLine = collapsed || isMobile;
  const row = (children: ReactNode, h: number) => (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: h, minWidth: 0, boxSizing: 'border-box',
      padding: `${SP.xxs}px ${SP.sm}px`, fontSize: FS.sm, color: C.textSecondary,
    }}>
      {children}
    </div>
  );

  const first = row(
    <>
      {/* Полоса одна — переключателя нет, заголовок ставим сами */}
      {switcher ?? (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, color: C.textSecondary }}>
          {handsStripIcon}Руки
        </span>
      )}
      <Dot color={TONE_DOT[view.tone]} size={8} />
      <span
        title={view.title}
        // На мобиле title не работает — полный текст по тапу
        onClick={isMobile ? () => showToast(detail ? `${view.text}. ${detail}` : view.text, view.title) : undefined}
        style={{
          minWidth: 0, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          color: view.tone === 'neutral' ? C.textMuted : C.textPrimary,
        }}>
        {view.text}
      </span>
      {view.canStop && (
        <Button variant="ghostFilled" size="xs" loading={busy} onClick={() => void stop()}
          leftIcon={<Square size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          title="Прервать ход: окна, открытые ходом, закроет агент устройства"
          // Тач: сама кнопка 32 px (цель касания), а строку раздвигает как 24 px
          style={{ flexShrink: 0, ...(isMobile ? { height: 32, margin: `-${SP.xs}px 0` } : {}) }}>
          Стоп
        </Button>
      )}
    </>, 30,
  );

  return (
    <div data-composer-strip="hands" data-hands-state={handsBadgeStatus(session.state)?.state ?? 'initial'}
      style={{ borderTop: `1px solid ${C.borderLight}`, background: C.bgPanel }}>
      {first}
      {!oneLine && detail && (
        <div style={{
          padding: `0 ${SP.sm}px ${SP.xs}px`, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {detail}
        </div>
      )}
    </div>
  );
}
