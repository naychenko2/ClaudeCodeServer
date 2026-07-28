import { useState } from 'react';
import { Power, Users, Zap } from 'lucide-react';
import type { SessionTeamImplement } from '../../types';
import { C, FS, FONT, R, SHADOW, Z, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Modal } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import {
  teamImplementTone, teamImplementBadgeText,
  TEAM_IMPLEMENT_DESCRIPTION, TEAM_IMPLEMENT_AUTO_TITLE,
  TEAM_IMPLEMENT_DISABLE_TITLE, TEAM_IMPLEMENT_DISABLE_TEXT,
} from '../../lib/teamImplement';

// Бейдж режима «Командная реализация» в композере (флаг team-implement-mode).
// По образцу loopBadge цикла «до готово»: pill 24/26, FS.xs, weight 600 — но с иконкой
// Users и пульс-точкой, плюс рядом переключаемый чип «Авто». Три тона — по тому, кто
// должен действовать (макет docs/mockups/team-implement-mode.html, секция 1):
//   work  (accent)  — планирование / волна N из M / проверка — команда работает
//   wait  (warning) — ждёт подтверждения / нужно решение — практика стоит и ждёт человека
//   idle  (muted)   — ждёт задачу — итерация закрыта, режим жив
// Клик по бейджу — поповер (десктоп) / шторка (мобила) с описанием и выключением режима;
// выключение — с подтверждением. Чип «Авто» переключается одним кликом без подтверждения.
export function TeamImplementBadge({ state, isMobile, onToggleAuto, onDisable }: {
  state: SessionTeamImplement;
  isMobile?: boolean;
  onToggleAuto: () => void | Promise<void>;
  onDisable: () => void | Promise<void>;
}) {
  const [infoOpen, setInfoOpen] = useState(false);
  const [disableConfirm, setDisableConfirm] = useState(false);

  const tone = teamImplementTone(state.stage);
  const text = teamImplementBadgeText(state.stage, state.waveNumber, state.budget?.maxWaves);
  const height = isMobile ? 26 : 24;

  const toneStyle = tone === 'work'
    ? { background: C.accentLight, color: C.accent }
    : tone === 'wait'
      ? { background: C.warningBg, color: C.warningText }
      : { background: C.bgSelected, color: C.textSecondary };
  const dotColor = tone === 'work' ? C.accent : tone === 'wait' ? C.warning : C.textMuted;

  const disableBody = (
    <>
      <p style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, margin: 0 }}>
        {TEAM_IMPLEMENT_DESCRIPTION}
      </p>
      <div style={{ marginTop: 10, paddingTop: 10, borderTop: `1px dashed ${C.divider}` }}>
        <DisableRow isMobile={isMobile} onClick={() => { setInfoOpen(false); setDisableConfirm(true); }} />
      </div>
    </>
  );

  return (
    <span style={{ position: 'relative', display: 'inline-flex', alignItems: 'center', gap: isMobile ? 6 : 4, flexShrink: 0 }}>
      <button
        onClick={() => setInfoOpen(v => !v)}
        title={TEAM_IMPLEMENT_DESCRIPTION}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 6, height,
          padding: '0 9px', borderRadius: R.pill, border: 'none', cursor: 'pointer',
          fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
          fontFamily: FONT.sans, ...toneStyle,
        }}
      >
        <Users size={11} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        {text}
        <span style={{
          width: 6, height: 6, borderRadius: '50%', background: dotColor, flexShrink: 0,
          // Пульс — только у «живых» стадий; у «ждёт задачу» точка статичная
          ...(tone !== 'idle' ? { animation: `pulsedot ${tone === 'wait' ? '1.2s' : '1.6s'} ease-in-out infinite` } : {}),
        }} />
      </button>

      <button
        onClick={() => { void onToggleAuto(); }}
        title={TEAM_IMPLEMENT_AUTO_TITLE}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, height,
          padding: '0 9px', borderRadius: R.pill, cursor: 'pointer',
          fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
          fontFamily: FONT.sans,
          transition: 'color 0.15s, background 0.15s, border-color 0.15s',
          ...(state.autoWaves
            ? { background: C.accentLight, color: C.accent, border: `1px solid ${C.accentMuted}` }
            : { background: 'transparent', color: C.textMuted, border: `1px solid ${C.border}` }),
        }}
      >
        <Zap size={10} strokeWidth={ICON_STROKE} fill={state.autoWaves ? 'currentColor' : 'none'} style={{ flexShrink: 0 }} />
        Авто
      </button>

      {/* Поповер: десктоп — карточка над бейджем, мобила — нижняя шторка (Modal) */}
      {infoOpen && !isMobile && (
        <>
          <div onClick={() => setInfoOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown }} />
          <div style={{
            position: 'absolute', bottom: '100%', left: 0, marginBottom: 8, zIndex: Z.dropdown + 1,
            width: 300, background: C.bgCard, border: `1px solid ${C.border}`,
            borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: '12px 14px',
            fontFamily: FONT.sans, textAlign: 'left',
          }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7, marginBottom: 4,
              fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading,
            }}>
              <Users size={14} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
              Командная реализация
            </div>
            {disableBody}
          </div>
        </>
      )}
      {infoOpen && isMobile && (
        <Modal title="Командная реализация" width={MODAL_W.confirm} onClose={() => setInfoOpen(false)}>
          {disableBody}
        </Modal>
      )}

      {disableConfirm && (
        <ConfirmDialog
          title={TEAM_IMPLEMENT_DISABLE_TITLE}
          subtitle={TEAM_IMPLEMENT_DISABLE_TEXT}
          confirmLabel="Выключить"
          cancelLabel="Оставить"
          onConfirm={async () => { await onDisable(); setDisableConfirm(false); }}
          onCancel={() => setDisableConfirm(false)}
        />
      )}
    </span>
  );
}

// Строка «Выключить режим» — danger-действие поповера/шторки. На мобиле — полноразмерная
// кнопка (тач-цель), на десктопе — текстовая строка с hover-подложкой
function DisableRow({ isMobile, onClick }: { isMobile?: boolean; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  if (isMobile) {
    return (
      <Button variant="danger" size="md" fullWidth onClick={onClick}>
        <Power size={13} strokeWidth={ICON_STROKE} />
        Выключить режим
      </Button>
    );
  }
  return (
    <button
      type="button"
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 7, width: '100%', textAlign: 'left',
        border: 'none', cursor: 'pointer', padding: '7px 8px', borderRadius: R.md,
        background: hover ? C.dangerBg : 'none',
        color: C.dangerText, fontSize: FS.base, fontWeight: 600, fontFamily: FONT.sans,
      }}
    >
      <Power size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      Выключить режим
    </button>
  );
}
