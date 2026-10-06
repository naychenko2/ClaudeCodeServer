// Полоса «Руки» над композером (реестр composer-strip; макет docs/mockups/local-hands-strip.html).
// Геометрия — как у git-полосы (прототип полос, вариант C): одна строка 51 px (44 на
// телефоне и планшете), свёрнутая — 30 px. Слева переключатель полос от хоста, дальше чип
// статуса — он не сжимается никогда, в узкой полосе берёт короткий текст; сжимается только
// сводка (устройство, провайдер и его зрение), по клику она открывает подробности: карточку
// над полосой, на телефоне — шторку. «Стоп» виден и в свёрнутой строке: остановить ИИ,
// который держит компьютер, нельзя прятать за второй клик. Общие функции агента устройства
// (статус, версия, папки, обновления) сюда не выносим — они живут в настройках проекта.

import { useState, type MouseEvent, type ReactNode } from 'react';
import { ChevronDown, Eye, MonitorSmartphone, Square } from 'lucide-react';
import { interruptSession } from '../../lib/signalr';
import {
  HANDS_ANY_WINDOW_TEXT, handsBadgeStatus, handsProviderLabel, handsProviderVision, handsStripSummary, handsStripView,
  type HandsBadgeTone,
} from '../../lib/localHands';
import { C, FS, SP, COMPOSER_LIP } from '../../lib/design';
import { TABLET_WIDE_MIN, useWindowWidth } from '../../lib/breakpoints';
import { useNarrowContainer } from '../../hooks/useContainerWidth';
import { Button, Dot, Menu, Modal, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ComposerLipShell } from '../../components/chat/ComposerLipShell';
import type { ComposerStripCtx } from '../../lib/subsystems/registryCore';
import { useProviders } from '../../lib/models';
import { getHandsProject, getHandsSession, useHandsStripVersion } from './handsStrip';

const TONE_DOT: Record<HandsBadgeTone, string> = {
  success: C.success,
  warning: C.warning,
  neutral: C.textMuted,
};

// Уже этой ширины полосы чип статуса переходит на короткий текст
const NARROW_STRIP = 520;

export const handsStripIcon = <MonitorSmartphone size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

// Строка меню переключателя «Git ▾»
export function handsStripStatus({ projectId, sessionId }: { projectId: string | null; sessionId: string | null }): ReactNode {
  if (!sessionId) return null;
  const status = handsBadgeStatus(getHandsSession(sessionId).state);
  return handsStripView(status, (projectId ? getHandsProject(projectId)?.deviceName : null) ?? null).text;
}

export function HandsStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { projectId, sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  useHandsStripVersion();
  // Зрение провайдера — из каталога /api/models; хук перерисует полосу, когда каталог догрузится
  const providers = useProviders();
  // Телефон и узкий планшет — slim-геометрия git-полосы (44 px)
  const slim = useWindowWidth() < TABLET_WIDE_MIN;
  const [narrowRef, narrow] = useNarrowContainer<HTMLDivElement>(NARROW_STRIP);
  const [busy, setBusy] = useState(false);
  const [card, setCard] = useState<DOMRect | null>(null);
  const [sheet, setSheet] = useState(false);
  if (!sessionId) return null;

  const session = getHandsSession(sessionId);
  const status = handsBadgeStatus(session.state);
  const device = (projectId ? getHandsProject(projectId)?.deviceName : null) ?? status?.deviceName ?? null;
  const view = handsStripView(status, device);
  const vision = handsProviderVision(session.provider, providers);
  const summary = handsStripSummary(status, device, vision);
  const miniSummary = handsStripSummary(status, device, vision, true);
  const hint = view.detail ? `${view.text} — ${view.detail}` : view.text;
  const dot = <Dot color={TONE_DOT[view.tone]} size={8} />;
  const statusColor = view.tone === 'neutral' ? C.textSecondary : C.textPrimary;

  const stop = async (e: MouseEvent) => {
    // В свёрнутой строке клик по «Стоп» не должен её разворачивать
    e.stopPropagation();
    setBusy(true);
    try { await interruptSession(sessionId); }
    catch { /* ход мог закончиться сам — следующее событие покажет */ }
    finally { setBusy(false); }
  };
  const stopBtn = (h: number) => view.canStop && (
    <Button variant="danger" size="xs" loading={busy} onClick={e => void stop(e)}
      leftIcon={<Square size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      title="Прервать ход: окна, открытые ходом, закроет агент устройства"
      style={{ flexShrink: 0, height: h, minHeight: h }}>
      Стоп
    </Button>
  );

  // Своего заголовка полоса не рисует: его место занимает переключатель хоста. Полоса одна —
  // переключателя нет, ставим тот же заголовок без «▾»
  const title = switcher ?? (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, padding: `0 ${SP.xs}px`,
      fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
    }}>
      {handsStripIcon}{!isMobile && 'Руки'}
    </span>
  );

  // Подробности — одни и те же в карточке над полосой и в шторке
  const details = (
    <div data-hands-details="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: FS.base, color: C.textPrimary }}>
      {!isMobile && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontWeight: 600, color: C.textHeading }}>
          {dot}{view.text}
        </div>
      )}
      {view.detail && <div style={{ color: C.textSecondary }}>{view.detail}</div>}
      {device && <div><span style={{ color: C.textMuted }}>Устройство: </span>{device}</div>}
      {vision && <div><span style={{ color: C.textMuted }}>Модель: </span>{handsProviderLabel(session.provider, providers)}</div>}
      <Notice tone="warning" icon={Eye}>{HANDS_ANY_WINDOW_TEXT}</Notice>
    </div>
  );
  const overlays = (
    <>
      {card && (
        <Menu anchor={card} onClose={() => setCard(null)} minWidth={300} maxWidth={380} maxHeight={260}>
          <div style={{ padding: `${SP.sm}px ${SP.md}px` }}>{details}</div>
        </Menu>
      )}
      {sheet && (
        <Modal title={<span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm }}>{dot}{view.text}</span>}
          onClose={() => setSheet(false)}>
          {details}
        </Modal>
      )}
    </>
  );

  const handsAttrs = {
    'data-composer-strip': 'hands',
    'data-hands-state': status?.state ?? 'initial',
  };
  // Inner-стили дочерних слоёв оболочки ComposerLipShell. Здесь только display/gap/align
  // (высоту/поля берёт оболочка).
  const fullInnerStyle = {
    display: 'flex', alignItems: 'center', gap: slim ? 6 : SP.sm,
    color: C.textSecondary, fontSize: FS.sm,
    minWidth: 0, width: '100%', height: '100%',
  };
  const miniInnerStyle = {
    display: 'flex', alignItems: 'center', gap: SP.sm, cursor: 'pointer',
    color: C.textSecondary, fontSize: FS.sm,
    minWidth: 0, width: '100%', height: '100%',
  };

  const miniNode = (
    <div
      role="button" tabIndex={0}
      ref={collapsed ? narrowRef : undefined}
      onClick={() => setCollapsed?.(false)}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setCollapsed?.(false); } }}
      title="Развернуть полосу «Руки»"
    >
      {title}
      {dot}
      <span data-hands-chip="" title={hint} style={{ flexShrink: 0, whiteSpace: 'nowrap', color: statusColor }}>
        {view.short}
      </span>
      {miniSummary && (
        <span style={{ minWidth: 0, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: C.textMuted }}>
          · {miniSummary}
        </span>
      )}
      {!miniSummary && <span style={{ flex: 1 }} />}
      {stopBtn(COMPOSER_LIP.rowMini)}
    </div>
  );

  if (collapsed) {
    return (
      <ComposerLipShell
        ref={narrowRef}
        collapsed
        isMobile={isMobile}
        isSlim={slim}
        miniStyle={miniInnerStyle}
        fullStyle={fullInnerStyle}
        miniNode={miniNode}
        fullNode={null}
        miniAttrs={{ ...handsAttrs, 'data-hands-strip': 'mini' }}
        fullAttrs={{ ...handsAttrs, 'data-hands-strip': 'full' }}
      />
    );
  }

  const chip = isMobile ? (
    // На телефоне title не работает: сам чип открывает шторку подробностей
    <Button variant="ghost" size="sm" onClick={() => setSheet(true)} title={hint}
      style={{ flexShrink: 0, height: 32, minHeight: 32, padding: `0 ${SP.sm}px`, gap: SP.xs + 2, fontSize: FS.sm, color: statusColor }}>
      {dot}<span data-hands-chip="" style={{ whiteSpace: 'nowrap' }}>{view.short}</span>
      <ChevronDown size={12} strokeWidth={ICON_STROKE} color={C.textMuted} />
    </Button>
  ) : (
    <span data-hands-chip="" title={hint} style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs + 2, flexShrink: 0, whiteSpace: 'nowrap',
      fontWeight: 500, color: statusColor,
    }}>
      {dot}{narrow ? view.short : view.chip}
    </span>
  );

  const fullNode = (
    // Обёртка — единственный ребёнок слоя оболочки: ряд раскладывает она (распорка flex: 1
    // уводит «Стоп» вправо)
    <div ref={narrowRef} style={fullInnerStyle}>
      {title}
      {chip}
      {!isMobile && (
        <Button variant="secondary" size="xs"
          onClick={e => setCard((e.currentTarget as HTMLElement).getBoundingClientRect())}
          title="Подробнее: устройство, модель, что видит ИИ"
          style={{ flex: '0 1 auto', minWidth: 0, height: 28, minHeight: 28, fontWeight: 400, gap: SP.xs, color: C.textSecondary }}>
          <span data-hands-summary="" style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {summary || 'Подробнее'}
          </span>
          <ChevronDown size={12} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
        </Button>
      )}
      <span style={{ flex: 1 }} />
      {stopBtn(slim ? 32 : 28)}
      {overlays}
    </div>
  );

  return (
    <ComposerLipShell
      ref={narrowRef}
      collapsed={false}
      isMobile={isMobile}
      isSlim={slim}
      miniStyle={miniInnerStyle}
      fullStyle={fullInnerStyle}
      miniNode={null}
      fullNode={fullNode}
      miniAttrs={{ ...handsAttrs, 'data-hands-strip': 'mini' }}
      fullAttrs={{ ...handsAttrs, 'data-hands-strip': 'full' }}
    />
  );
}
