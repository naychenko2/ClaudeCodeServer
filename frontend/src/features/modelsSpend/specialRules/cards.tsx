import type { CSSProperties, ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { TIER_ORDER, type TierKey } from '../../../lib/modelProvidersShared';
import { usePresets } from '../../../lib/presets';
import type { SpecialtySettingsLayer } from '../../../types';
import { rolesWord, tripleSummary, type RoleRow, type RuleGroup, type Triple } from './model';
import { TierFieldRow, WillGoLine, type PickerCtx, type Scope } from './parts';

// Карточки вкладки «Особые правила» (макет v4): «Любая специальность» (закреплена
// первой), группа одинаковых наборов и отдельная роль. Все три — один и тот же
// корпус: белая карточка со скруглением, раскрытая подсвечивается кромкой accent,
// выбранная сегментом полосы — кольцом.
//
// Вложенность ровно два уровня: карточка → панель выбора RoutePicker. Ничего третьего
// внутри карточек нет сознательно (ограничение постановки).

const HOVER_CLASS = 'cc-sr-head';
if (typeof document !== 'undefined' && !document.getElementById('cc-sr-head-style')) {
  const el = document.createElement('style');
  el.id = 'cc-sr-head-style';
  el.textContent = `.${HOVER_CLASS}:hover,.${HOVER_CLASS}:active{background:${C.bgSelected};}`;
  document.head.appendChild(el);
}

function shellStyle(opts: { open?: boolean; highlight?: boolean }): CSSProperties {
  return {
    background: C.bgWhite,
    border: `1px solid ${opts.open ? C.accentMuted : C.border}`,
    borderRadius: R.xl, marginBottom: SP.sm, overflow: 'hidden',
    boxShadow: opts.highlight ? `0 0 0 2px ${C.accent}` : 'none',
    transition: 'box-shadow 0.2s, border-color 0.15s',
  };
}

const bodyStyle: CSSProperties = {
  borderTop: `1px dashed ${C.border}`, padding: `4px 14px ${SP.md}px`, background: C.bgCard,
};

const headStyle: CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, width: '100%', minWidth: 0,
  padding: '11px 14px', border: 'none', background: 'transparent',
  fontFamily: FONT.sans, cursor: 'pointer', textAlign: 'left',
};

function Chevron({ open }: { open: boolean }) {
  return (
    <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{
      color: C.textMuted, flexShrink: 0, marginLeft: 'auto',
      transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
    }} />
  );
}

// Ссылка-действие внутри карточки («выделить», «Вернуть наследование»)
function LinkAction({ onClick, disabled, children }: {
  onClick: () => void; disabled?: boolean; children: ReactNode;
}) {
  return (
    <button type="button" onClick={onClick} disabled={disabled} style={{
      font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
      color: disabled ? C.textMuted : C.accent, background: 'none', border: 'none',
      padding: 0, cursor: disabled ? 'default' : 'pointer', textDecoration: 'underline',
      textUnderlineOffset: 2,
    }}>{children}</button>
  );
}

// === «Любая специальность» — закреплена первой, аккордеона нет ===
// Это не одна из специальностей, а ответ на её отсутствие, поэтому карточка всегда
// раскрыта: свернуть её значило бы спрятать самое частое правило слоя.
export function AnySpecialtyCard({ triple, hint, scope, ctx, highlight, innerRef,
  onCell, onClear, onPresetCreated }: {
  triple: Triple;
  hint: string;
  scope: Scope;
  ctx: PickerCtx;
  highlight: boolean;
  innerRef?: (el: HTMLDivElement | null) => void;
  onCell: (tier: TierKey, route: string) => void;
  onClear: (tier: TierKey) => void;
  onPresetCreated: (tier: TierKey, presetId: string, presetScope: Scope, layer: SpecialtySettingsLayer) => void;
}) {
  // Пустое поле «Любой специальности» падает уже в «Модели по умолчанию» — ниже
  // наследовать некуда. В чужом слое — к настройкам самого пользователя.
  const placeholder = scope === 'user' ? 'Как у владельца' : 'Как «Модели по умолчанию»';
  return (
    <div ref={innerRef} style={{ ...shellStyle({ highlight }), marginBottom: SP.md, padding: `${SP.md}px 14px` }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>
          Любая специальность
        </span>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span>
      </div>
      {TIER_ORDER.map((t, i) => (
        <TierFieldRow
          key={t}
          tier={t}
          route={triple[i]}
          placeholder={placeholder}
          ctx={ctx}
          onChange={v => onCell(t, v)}
          onClear={() => onClear(t)}
          onPresetCreated={(id, s, l) => onPresetCreated(t, id, s, l)}
        />
      ))}
      <WillGoLine specialtyKey="any" scope={scope} />
    </div>
  );
}

// === Группа одинаковых наборов ===
// Группа — ВИД, а не сущность: у каждой роли своя запись в слое, просто тройки совпали.
// Поэтому правка пишет одно и то же значение всем ролям группы одним PUT (о чём
// предупреждает жёлтая плашка), а «выделить» ничего не пишет вовсе — роль просто
// переезжает в «Отдельные наборы» и дальше правится сама по себе.
export function RuleGroupCard({ group, open, onToggle, scope, ctx, highlight, innerRef,
  matchesAny, onCell, onClear, onPresetCreated, onSplit }: {
  group: RuleGroup;
  open: boolean;
  onToggle: () => void;
  scope: Scope;
  ctx: PickerCtx;
  highlight: boolean;
  innerRef?: (el: HTMLDivElement | null) => void;
  // Тройка группы совпала с «Любой специальностью» — эти роли можно было бы не настраивать
  matchesAny: boolean;
  onCell: (tier: TierKey, route: string) => void;
  onClear: (tier: TierKey) => void;
  onPresetCreated: (tier: TierKey, presetId: string, presetScope: Scope, layer: SpecialtySettingsLayer) => void;
  onSplit: (roleKey: string) => void;
}) {
  const presets = usePresets();
  const summary = tripleSummary(group.triple, presets, {
    tierModels: ctx.tierModels, ollamaModel: ctx.ollamaModel,
  });
  const names = group.roles.map(r => r.label).join(', ');

  return (
    <div ref={innerRef} style={shellStyle({ open, highlight })}>
      <button type="button" className={HOVER_CLASS} onClick={onToggle} aria-expanded={open}
        style={{ ...headStyle, flexWrap: 'wrap', padding: '11px 14px 8px' }}>
        {group.roles.map(r => (
          <span key={r.key} style={{
            fontSize: FS.xs, fontWeight: 600, padding: '2px 8px', borderRadius: R.max,
            background: C.bgSelected, color: C.textSecondary,
          }}>{r.label}</span>
        ))}
        <Chevron open={open} />
      </button>
      <div title={summary.full} style={{
        display: 'block', padding: '0 14px 10px', fontSize: FS.xs, color: C.textMuted,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>{summary.short}</div>

      {matchesAny && (
        <div style={{
          fontSize: FS.xs, color: C.info, background: C.infoBg, borderRadius: R.md,
          padding: '5px 10px', margin: '0 14px 10px', lineHeight: 1.45,
        }}>
          Такой же набор задан у «Любой специальности» — эти роли можно было бы вообще не настраивать.
        </div>
      )}

      {open && (
        <div style={bodyStyle}>
          {TIER_ORDER.map((t, i) => (
            <TierFieldRow
              key={t}
              tier={t}
              route={group.triple[i]}
              placeholder="Как «Любая специальность»"
              ctx={ctx}
              onChange={v => onCell(t, v)}
              onClear={() => onClear(t)}
              onPresetCreated={(id, s, l) => onPresetCreated(t, id, s, l)}
            />
          ))}
          <div style={{
            fontSize: FS.xs, color: C.warningText, background: C.warningBg, borderRadius: R.md,
            padding: '6px 10px', marginTop: 10, lineHeight: 1.45,
          }}>
            Изменение применится к {rolesWord(group.roles.length)}: {names}.
          </div>
          <div style={{
            display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 10, alignItems: 'center',
          }}>
            {group.roles.map(r => (
              <span key={r.key} style={{
                display: 'inline-flex', alignItems: 'center', gap: 6, whiteSpace: 'nowrap',
                fontSize: FS.xs, padding: '3px 8px 3px 10px', borderRadius: R.max,
                background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textPrimary,
              }}>
                {r.label}
                {!ctx.readOnly && (
                  <LinkAction disabled={ctx.busy} onClick={() => onSplit(r.key)}>выделить</LinkAction>
                )}
              </span>
            ))}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 6, lineHeight: 1.45 }}>
            «Выделить» — у роли появится свой набор, скопированный из группы.
          </div>
          <WillGoLine specialtyKey={group.roles[0].key} scope={scope} />
        </div>
      )}
    </div>
  );
}

// === Отдельная роль (синглтон) ===
export function RuleSpecCard({ role, open, onToggle, scope, ctx, highlight, innerRef,
  onCell, onClear, onResetRole, onPresetCreated }: {
  role: RoleRow;
  open: boolean;
  onToggle: () => void;
  scope: Scope;
  ctx: PickerCtx;
  highlight: boolean;
  innerRef?: (el: HTMLDivElement | null) => void;
  onCell: (tier: TierKey, route: string) => void;
  onClear: (tier: TierKey) => void;
  // Сброс всей роли — серверный (возврат к наследованию = удаление записи слоя,
  // а не обнуление полей: запись без полей продолжала бы перекрывать нижний слой)
  onResetRole: () => void;
  onPresetCreated: (tier: TierKey, presetId: string, presetScope: Scope, layer: SpecialtySettingsLayer) => void;
}) {
  const presets = usePresets();
  const summary = tripleSummary(role.triple, presets, {
    tierModels: ctx.tierModels, ollamaModel: ctx.ollamaModel,
  });
  return (
    <div ref={innerRef} style={shellStyle({ open, highlight })}>
      <button type="button" className={HOVER_CLASS} onClick={onToggle} aria-expanded={open}
        style={{ ...headStyle, gap: SP.sm }}>
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading, flexShrink: 0 }}>
          {role.label}
        </span>
        <span title={summary.full} style={{
          fontSize: FS.xs, color: C.textMuted, minWidth: 0,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{summary.short}</span>
        <Chevron open={open} />
      </button>
      {open && (
        <div style={bodyStyle}>
          {TIER_ORDER.map((t, i) => (
            <TierFieldRow
              key={t}
              tier={t}
              route={role.triple[i]}
              placeholder="Как «Любая специальность»"
              ctx={ctx}
              onChange={v => onCell(t, v)}
              onClear={() => onClear(t)}
              onPresetCreated={(id, s, l) => onPresetCreated(t, id, s, l)}
            />
          ))}
          <WillGoLine specialtyKey={role.key} scope={scope} />
          {!ctx.readOnly && (
            <div style={{ marginTop: SP.sm, display: 'flex', gap: SP.sm, alignItems: 'center', flexWrap: 'wrap' }}>
              <LinkAction disabled={ctx.busy} onClick={onResetRole}>Вернуть наследование</LinkAction>
              <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
                — все три поля снова пойдут за «Любой специальностью»
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
