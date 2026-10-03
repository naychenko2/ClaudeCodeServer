// Пилюля «Руки» в губе поля ввода (слот composer-chip) при включённой строке контекста:
// строка контекста заменяет хост полос, а вместе с ним и полосу «Руки», поэтому статус и
// «Стоп» переезжают сюда. Без флага вклад молчит — рисуется старая полоса.

import { useState, type MouseEvent } from 'react';
import { Eye, Square } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { interruptSession } from '../../lib/signalr';
import {
  HANDS_ANY_WINDOW_TEXT, handsBadgeStatus, handsProviderLabel, handsProviderVision, handsStripView, type HandsBadgeTone,
} from '../../lib/localHands';
import { useProviders } from '../../lib/models';
import type { ComposerChipCtx } from '../../lib/subsystems/registryCore';
import { Dot, Menu, Modal, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { getHandsProject, getHandsSession, handsStripAvailable, useHandsStripVersion } from './handsStrip';

const TONE_DOT: Record<HandsBadgeTone, string> = { success: C.success, warning: C.warning, neutral: C.textMuted };

export function HandsChip({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.composerContextRow);
  useHandsStripVersion();
  const providers = useProviders();
  const [busy, setBusy] = useState(false);
  const [card, setCard] = useState<DOMRect | null>(null);
  const { projectId, sessionId, isMobile } = ctx;
  if (!on || !sessionId || !handsStripAvailable({ projectId, sessionId })) return null;

  const session = getHandsSession(sessionId);
  const status = handsBadgeStatus(session.state);
  const device = (projectId ? getHandsProject(projectId)?.deviceName : null) ?? status?.deviceName ?? null;
  const view = handsStripView(status, device);
  const vision = handsProviderVision(session.provider, providers);
  const hint = view.detail ? `${view.text} — ${view.detail}` : view.text;

  const stop = async (e: MouseEvent) => {
    e.stopPropagation();
    setBusy(true);
    try { await interruptSession(sessionId); }
    catch { /* ход мог закончиться сам — следующее событие покажет */ }
    finally { setBusy(false); }
  };

  const details = (
    <div data-hands-details="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: FS.base, color: C.textPrimary }}>
      {view.detail && <div style={{ color: C.textSecondary }}>{view.detail}</div>}
      {device && <div><span style={{ color: C.textMuted }}>Устройство: </span>{device}</div>}
      {vision && <div><span style={{ color: C.textMuted }}>Модель: </span>{handsProviderLabel(session.provider, providers)}</div>}
      <Notice tone="warning" icon={Eye}>{HANDS_ANY_WINDOW_TEXT}</Notice>
    </div>
  );

  return (
    <span data-hands-pill="" data-hands-state={status?.state ?? 'initial'} style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs + 2, height: 24, padding: `0 ${SP.sm}px`,
      border: `1px solid ${C.border}`, borderRadius: R.max, background: C.bgPanel, fontSize: FS.sm, whiteSpace: 'nowrap',
    }}>
      <button type="button" title={hint} onClick={e => setCard((e.currentTarget as HTMLElement).getBoundingClientRect())}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: SP.xs + 2, border: 'none', background: 'transparent',
          padding: 0, cursor: 'pointer', fontFamily: 'inherit', fontSize: 'inherit',
          color: view.tone === 'neutral' ? C.textSecondary : C.textPrimary,
        }}>
        <Dot color={TONE_DOT[view.tone]} size={8} />
        <span data-hands-chip="">Руки · {view.short}</span>
      </button>
      {view.canStop && (
        <button type="button" disabled={busy} onClick={e => void stop(e)}
          title="Прервать ход: окна, открытые ходом, закроет агент устройства"
          style={{
            display: 'inline-flex', alignItems: 'center', gap: SP.xs, border: 'none', background: 'transparent',
            padding: 0, cursor: 'pointer', fontFamily: 'inherit', fontSize: 'inherit', color: C.dangerText,
          }}>
          <Square size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />Стоп
        </button>
      )}
      {card && !isMobile && (
        <Menu anchor={card} onClose={() => setCard(null)} minWidth={300} maxWidth={380} maxHeight={260} preferUp>
          <div style={{ padding: `${SP.sm}px ${SP.md}px` }}>{details}</div>
        </Menu>
      )}
      {card && isMobile && <Modal title={`Руки · ${view.text}`} onClose={() => setCard(null)}>{details}</Modal>}
    </span>
  );
}
