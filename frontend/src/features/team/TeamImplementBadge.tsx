import { useState } from 'react';
import { Gauge, ListChecks, Pause, Power, Users, Zap } from 'lucide-react';
import type { SessionTeamImplement } from '../../types';
import { C, FS, FONT, R, MODAL_W } from '../../lib/design';
import { Button, ConfirmDialog, Menu, Modal } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import {
  teamImplementTone, teamImplementBadgeText, teamImplementStageShort, teamBudgetLine, teamBudgetTight,
  teamImplementModeHeld,
  TEAM_IMPLEMENT_DESCRIPTION, TEAM_IMPLEMENT_AUTO_TITLE,
  TEAM_IMPLEMENT_DISABLE_TITLE, TEAM_IMPLEMENT_DISABLE_TEXT,
  TEAM_IMPLEMENT_NO_CODE_ON, TEAM_IMPLEMENT_NO_CODE_OFF, TEAM_IMPLEMENT_MODE_HELD,
  TEAM_IMPLEMENT_STOP_TITLE, TEAM_IMPLEMENT_STOP_TEXT, TEAM_IMPLEMENT_STOPPED_HINT,
} from '../../lib/teamImplement';
import { teamMechanic } from './teamMechanics';
import type { Mode } from '../../lib/modes';

// Бейдж режима «Командная реализация» в композере.
// По образцу loopBadge цикла «до готово»: pill 24/26, FS.xs, weight 600 — но с иконкой
// Users и пульс-точкой, плюс рядом переключаемый чип «Авто». Три тона — по тому, кто
// должен действовать (макет docs/mockups/team-implement-mode.html, секция 1):
//   work  (accent)  — планирование / волна N из M / проверка — команда работает
//   wait  (warning) — ждёт подтверждения / нужно решение — практика стоит и ждёт человека
//   idle  (muted)   — ждёт задачу — итерация закрыта, режим жив
// Клик по бейджу — поповер (десктоп) / шторка (мобила) с описанием и выключением режима;
// выключение — с подтверждением. Чип «Авто» переключается одним кликом без подтверждения.
export function TeamImplementBadge({ state, chatMode, isMobile, onToggleAuto, onDisable, onStop }: {
  state: SessionTeamImplement;
  // Текущий режим прав чата: под правилом «координатор не пишет код» он удерживается
  // на «Авто» — иначе гард обходится через терминал. Показываем сноской, чтобы
  // подтверждения действий не выглядели необъяснимыми
  chatMode?: Mode;
  isMobile?: boolean;
  onToggleAuto: () => void | Promise<void>;
  onDisable: () => void | Promise<void>;
  // «Остановить» прогон (режим при этом остаётся включённым). Без обработчика строки нет
  onStop?: () => void | Promise<void>;
}) {
  // Поповер держим на rect кнопки (anchor-режим Menu = fixed), а не на absolute внутри
  // бейджа: полоса бейджей композера живёт под overflow:hidden (схлопывание контролов),
  // и absolute-карточка обрезалась целиком — в DOM была, на экране нет
  const [infoAnchor, setInfoAnchor] = useState<DOMRect | null>(null);
  const [disableConfirm, setDisableConfirm] = useState(false);
  const [stopConfirm, setStopConfirm] = useState(false);
  const infoOpen = infoAnchor !== null;
  const closeInfo = () => setInfoAnchor(null);

  const tone = teamImplementTone(state.stage);
  // Короткая форма чипа («КР · волна 1») — полная («Командная реализация · волна 1 из 2»)
  // вываливается за границы полосы бейджей на узкой ширине; она уходит в title кнопки
  const fullText = teamImplementBadgeText(state.stage, state.waveNumber, state.plannedWaves);
  const text = `${teamMechanic('implementMode').shortName} · ${teamImplementStageShort(state.stage, state.waveNumber, state.plannedWaves)}`;
  const height = isMobile ? 26 : 24;

  const toneStyle = tone === 'work'
    ? { background: C.accentLight, color: C.accent }
    : tone === 'wait'
      ? { background: C.warningBg, color: C.warningText }
      : { background: C.bgSelected, color: C.textSecondary };
  const dotColor = tone === 'work' ? C.accent : tone === 'wait' ? C.warning : C.textMuted;

  const budgetLine = teamBudgetLine(state.budget);
  const budgetTight = teamBudgetTight(state.budget);

  const disableBody = (
    <>
      <p style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, margin: 0 }}>
        {TEAM_IMPLEMENT_DESCRIPTION}
      </p>
      {/* Правило «любая работа — через задачу»: настройка режима, менять её на ходу нельзя —
          показываем как строку состояния, чтобы поведение штаба не выглядело сюрпризом */}
      <div style={{
        display: 'flex', alignItems: 'flex-start', gap: 6, marginTop: 8,
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4,
      }}>
        <ListChecks size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
        {state.coordinatorNoCode ? TEAM_IMPLEMENT_NO_CODE_ON : TEAM_IMPLEMENT_NO_CODE_OFF}
      </div>
      {/* Сноска к правилу: режим прав чата держится на «Авто» не сам по себе — его
          удерживает правило. Без этого подтверждения действий, которых вчера не было,
          выглядят как поломка. Отступ слева = иконка + gap строки правила */}
      {state.coordinatorNoCode && chatMode && teamImplementModeHeld(chatMode) && (
        <div style={{
          marginTop: 4, paddingLeft: 18,
          fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4,
        }}>
          {TEAM_IMPLEMENT_MODE_HELD}
        </div>
      )}
      {/* Расход бюджета итерации: человек должен видеть, что остановка близко, ДО того
          как она случится (иначе исчерпание бюджета выглядит внезапным) */}
      {budgetLine && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: 6, marginTop: 6,
          fontSize: FS.xs, lineHeight: 1.4,
          color: budgetTight ? C.warningText : C.textMuted,
          fontWeight: budgetTight ? 600 : 400,
        }}>
          <Gauge size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
          {budgetLine}
        </div>
      )}
      {/* Остановка прогона: режим остаётся включённым, поэтому она отдельно от «Выключить» */}
      {onStop && (
        <div style={{ marginTop: 10, paddingTop: 10, borderTop: `1px dashed ${C.divider}` }}>
          {state.stopped ? (
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 6, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
              <Pause size={12} strokeWidth={ICON_STROKE} fill="currentColor" style={{ flexShrink: 0, marginTop: 1 }} />
              {TEAM_IMPLEMENT_STOPPED_HINT}
            </div>
          ) : isMobile ? (
            <Button variant="ghostFilled" size="md" fullWidth
              onClick={() => { closeInfo(); setStopConfirm(true); }}>
              <Pause size={13} strokeWidth={ICON_STROKE} fill="currentColor" />
              Остановить
            </Button>
          ) : (
            <PopoverRow
              icon={<Pause size={13} strokeWidth={ICON_STROKE} fill="currentColor" style={{ flexShrink: 0 }} />}
              label="Остановить"
              onClick={() => { closeInfo(); setStopConfirm(true); }}
            />
          )}
        </div>
      )}
      <div style={{ marginTop: 10, paddingTop: 10, borderTop: `1px dashed ${C.divider}` }}>
        <DisableRow isMobile={isMobile} onClick={() => { closeInfo(); setDisableConfirm(true); }} />
      </div>
    </>
  );

  return (
    <span style={{ position: 'relative', display: 'inline-flex', alignItems: 'center', gap: isMobile ? 6 : 4, flexShrink: 0 }}>
      <button
        onClick={e => {
          const rect = e.currentTarget.getBoundingClientRect();
          setInfoAnchor(prev => (prev ? null : rect));
        }}
        title={`${fullText} — ${TEAM_IMPLEMENT_DESCRIPTION}`}
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

      {/* Поповер: десктоп — карточка у бейджа (fixed по якорю, иначе её срезает
          overflow полосы бейджей), мобила — нижняя шторка (Modal) */}
      {infoOpen && !isMobile && (
        <Menu anchor={infoAnchor!} minWidth={300} maxHeight={320} onClose={closeInfo}>
          <div style={{ padding: '7px 9px', fontFamily: FONT.sans, textAlign: 'left' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 7, marginBottom: 4,
              fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading,
            }}>
              <Users size={14} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
              Командная реализация
            </div>
            {disableBody}
          </div>
        </Menu>
      )}
      {infoOpen && isMobile && (
        <Modal title="Командная реализация" width={MODAL_W.confirm} onClose={closeInfo}>
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

      {stopConfirm && onStop && (
        <ConfirmDialog
          title={TEAM_IMPLEMENT_STOP_TITLE}
          subtitle={TEAM_IMPLEMENT_STOP_TEXT}
          confirmLabel="Остановить"
          cancelLabel="Пусть работает"
          onConfirm={async () => { await onStop(); setStopConfirm(false); }}
          onCancel={() => setStopConfirm(false)}
        />
      )}
    </span>
  );
}

// Строка-действие поповера (десктоп): иконка + подпись с hover-подложкой.
// danger — красная («Выключить режим»), обычная — нейтральная («Остановить»)
function PopoverRow({ icon, label, onClick, danger }: {
  icon: React.ReactNode;
  label: string;
  onClick: () => void;
  danger?: boolean;
}) {
  const [hover, setHover] = useState(false);
  return (
    <button
      type="button"
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 7, width: '100%', textAlign: 'left',
        border: 'none', cursor: 'pointer', padding: '7px 8px', borderRadius: R.md,
        background: hover ? (danger ? C.dangerBg : C.bgSelected) : 'none',
        color: danger ? C.dangerText : C.textHeading,
        fontSize: FS.base, fontWeight: 600, fontFamily: FONT.sans,
      }}
    >
      {icon}
      {label}
    </button>
  );
}

// «Выключить режим» — danger-действие поповера/шторки. На мобиле — полноразмерная
// кнопка (тач-цель), на десктопе — строка с hover-подложкой
function DisableRow({ isMobile, onClick }: { isMobile?: boolean; onClick: () => void }) {
  if (isMobile) {
    return (
      <Button variant="danger" size="md" fullWidth onClick={onClick}>
        <Power size={13} strokeWidth={ICON_STROKE} />
        Выключить режим
      </Button>
    );
  }
  return (
    <PopoverRow
      danger
      icon={<Power size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
      label="Выключить режим"
      onClick={onClick}
    />
  );
}
