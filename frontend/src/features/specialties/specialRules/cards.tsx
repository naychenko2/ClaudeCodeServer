import type { CSSProperties, ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { TIERS, TIER_ORDER, type TierKey } from '../../../lib/modelProvidersShared';
import { usePresets } from '../../../lib/presets';
import type { SpecialtySettingsLayer } from '../../../types';
import {
  PERSONA_MANUAL_NOTE, PERSONA_WORKPLACE_LABEL, ROLE_SLICE_EXPLANATION,
  personasWord, rolesWord, sortRolePersonaLines, tripleSummary, type RolePersonaLine,
  type RoleRow, type RuleGroup, type Triple,
} from './model';
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

// === Срез «Кто работает по этой роли» (этап 4) ===
//
// Список персон, приписанных к специальности, с тремя мини-чипами моделей по уровням,
// пометкой T5 (ручная модель) и строкой фолбэка T10 (имя цепочки). Тексты — дословно
// из docs/features/model-presets-and-tiers.md (блок 8.1).
//
// Только на owner-слое (см. getRoleSliceKind). На global/user карточка рисует только
// строку-объяснение T8 вместо списка.

const sliceWrapStyle: CSSProperties = {
  marginTop: SP.md, paddingTop: SP.md, borderTop: `1px dashed ${C.border}`,
};

// === Строка персоны в срезе: «{Имя} — [S: …] [M: …] [W: …] · в чате [+ T5/T10]» ===
function PersonaSliceRow({ line, onOpen, roleBadge }: {
  line: RolePersonaLine;
  onOpen?: (id: string) => void;
  // Подпись роли у строки — только для RuleGroupCard, где у одной строки может быть
  // несколько ролей; в одиночной карточке и в AnySpecialtyCard ровно одна роль
  roleBadge?: string;
}) {
  const hasAnyChip = TIER_ORDER.some(t => line.modelsByTier[t]);
  const open = onOpen ?? (() => undefined);
  return (
    <button type="button" onClick={() => open(line.id)} style={{
      display: 'flex', alignItems: 'flex-start', gap: 8, width: '100%',
      padding: '7px 10px', background: 'transparent',
      border: `1px solid ${C.borderLight}`, borderRadius: R.md,
      fontFamily: FONT.sans, fontSize: FS.xs, color: C.textPrimary,
      cursor: onOpen ? 'pointer' : 'default', textAlign: 'left',
      boxSizing: 'border-box',
    }}>
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
          <span style={{
            fontWeight: 600, color: C.textHeading,
            minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{line.name}</span>
          {roleBadge && (
            <span style={{
              padding: '1px 7px', borderRadius: R.max, background: C.bgSelected,
              color: C.textSecondary, fontWeight: 600, flexShrink: 0,
            }}>{roleBadge}</span>
          )}
          {hasAnyChip && (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, flexWrap: 'wrap', minWidth: 0 }}>
              {TIER_ORDER.map(tier => line.modelsByTier[tier] ? (
                <span key={tier} style={{
                  fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading,
                  background: C.bgSelected, borderRadius: R.sm, padding: '1px 6px',
                  minWidth: 0, maxWidth: '100%', overflowWrap: 'anywhere',
                }}>
                  {TIERS[tier].title[0]}: {line.modelsByTier[tier]}
                </span>
              ) : null)}
            </span>
          )}
          <span style={{ color: C.textMuted, flexShrink: 0 }}>· {PERSONA_WORKPLACE_LABEL}</span>
        </div>
        {line.manual && (
          <div style={{ color: C.textMuted, fontSize: 11, lineHeight: 1.4 }}>
            {PERSONA_MANUAL_NOTE}
          </div>
        )}
        {line.fallbackLine && (
          <div style={{ color: C.textMuted, fontSize: 11, lineHeight: 1.4 }}>
            {line.fallbackLine}
          </div>
        )}
      </div>
    </button>
  );
}

// Заголовок секции среза «Кто работает по этой роли» — дословно T3.
const PERSONA_SLICE_TITLE = 'Кто работает по этой роли';

// Секция среза: список строк или empty-state T6. Не рисуется, если список пуст и
// empty выключен (AnySpecialtyCard без пустых персон сразу ничего не показывает).
function PersonaSliceSection({ lines, onOpen, showEmpty, emptyRoleLabel }: {
  lines: RolePersonaLine[];
  onOpen?: (id: string) => void;
  // Рисовать ли empty-state при пустом срезе (по умолчанию — нет; для «Любой специальности»
  // empty-state T6 уместен, для отдельных карточек — карточка сама ничего не покажет).
  showEmpty?: boolean;
  emptyRoleLabel?: string;
}) {
  if (lines.length === 0) {
    if (!showEmpty) return null;
    return (
      <div style={sliceWrapStyle}>
        <div style={{
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
          textTransform: 'uppercase', letterSpacing: '0.07em', marginBottom: 6,
        }}>{PERSONA_SLICE_TITLE}</div>
        <div style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5 }}>
          По этой роли пока никто не работает. Назначьте специальность персоне в её
          карточке — она получит эти модели и доступы автоматически.
        </div>
      </div>
    );
  }
  const sorted = sortRolePersonaLines(lines);
  return (
    <div style={sliceWrapStyle}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.07em', marginBottom: 8,
      }}>{PERSONA_SLICE_TITLE}{emptyRoleLabel ? ` · ${emptyRoleLabel}` : ''}</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
        {sorted.map(line => (
          <PersonaSliceRow key={line.id} line={line} onOpen={onOpen} />
        ))}
      </div>
    </div>
  );
}

// Строка-объяснение T8 (вместо среза на слоях global/user) — единая функция, чтобы
// карточки не дублировали дословный текст.
function PersonaSliceExplanation(): ReactNode {
  return (
    <div style={{
      ...sliceWrapStyle,
      fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.5,
    }}>
      {ROLE_SLICE_EXPLANATION}
    </div>
  );
}

// Для RuleGroupCard: у каждой строки персоны — подпись её роли (ролей в группе несколько).
// roleLabelBy: ключ роли → подпись для бейджа в строке персоны. Сортировка — по (роль, имя),
// чтобы порядок не прыгал при обновлениях.
function PersonaSliceGroup({ lines, onOpen, roleLabelBy }: {
  lines: RolePersonaLine[];
  onOpen?: (id: string) => void;
  roleLabelBy: (personaId: string) => string | null;
}) {
  if (lines.length === 0) {
    return <PersonaSliceSection lines={[]} showEmpty />;
  }
  const sorted = sortRolePersonaLines(lines);
  return (
    <div style={sliceWrapStyle}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.07em', marginBottom: 8,
      }}>{PERSONA_SLICE_TITLE}</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
        {sorted.map(line => (
          <PersonaSliceRow key={line.id} line={line} onOpen={onOpen}
            roleBadge={roleLabelBy(line.id) ?? undefined} />
        ))}
      </div>
    </div>
  );
}

// === «Любая специальность» — закреплена первой, аккордеона нет ===
// Это не одна из специальностей, а ответ на её отсутствие, поэтому карточка всегда
// раскрыта: свернуть её значило бы спрятать самое частое правило слоя.
export function AnySpecialtyCard({ triple, hint, scope, ctx, highlight, innerRef,
  onCell, onClear, onPresetCreated, personaLines, onOpenPersona }: {
  triple: Triple;
  hint: string;
  scope: Scope;
  ctx: PickerCtx;
  highlight: boolean;
  innerRef?: (el: HTMLDivElement | null) => void;
  onCell: (tier: TierKey, route: string) => void;
  onClear: (tier: TierKey) => void;
  onPresetCreated: (tier: TierKey, presetId: string, presetScope: Scope, layer: SpecialtySettingsLayer) => void;
  // Срез «Кто работает по этой роли»: персоны БЕЗ специальности (specialty === 'none'
  // или отсутствует). На owner — список, на global/user — строка-объяснение T8.
  personaLines: RolePersonaLine[];
  onOpenPersona?: (id: string) => void;
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
      {scope === 'owner'
        ? <PersonaSliceSection lines={personaLines} onOpen={onOpenPersona} showEmpty />
        : <PersonaSliceExplanation />}
    </div>
  );
}

// === Группа одинаковых наборов ===
// Группа — ВИД, а не сущность: у каждой роли своя запись в слое, просто тройки совпали.
// Поэтому правка пишет одно и то же значение всем ролям группы одним PUT (о чём
// предупреждает жёлтая плашка), а «выделить» ничего не пишет вовсе — роль просто
// переезжает в «Отдельные наборы» и дальше правится сама по себе.
export function RuleGroupCard({ group, open, onToggle, scope, ctx, highlight, innerRef,
  matchesAny, onCell, onClear, onPresetCreated, onSplit, personaLines, personaRoleById,
  onOpenPersona }: {
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
  // Объединённый срез персон всех ролей группы: у каждой строки — подпись её роли.
  // На owner — список с пометкой роли у каждой персоны, на global/user — T8.
  personaLines: RolePersonaLine[];
  // Подпись роли для каждой персоны среза (по id персоны). Нужна группе, потому что
  // у одной строки среза может быть несколько ролей, а у каждой персоны — одна.
  personaRoleById?: Record<string, string>;
  onOpenPersona?: (id: string) => void;
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
          {/* Срез персон группы: каждая строка несёт подпись своей роли (ролей-то несколько).
              На global/user — строка-объяснение T8 (общий срез за другого был бы враньём). */}
          {scope === 'owner'
            ? <PersonaSliceGroup lines={personaLines} onOpen={onOpenPersona}
                roleLabelBy={id => personaRoleById?.[id] ?? null} />
            : <PersonaSliceExplanation />}
        </div>
      )}
    </div>
  );
}

// === Отдельная роль (синглтон) ===
export function RuleSpecCard({ role, open, onToggle, scope, ctx, highlight, innerRef,
  onCell, onClear, onResetRole, onPresetCreated, personaLines, onOpenPersona }: {
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
  // Срез персон этой специальности. На owner — список, на global/user — T8.
  personaLines: RolePersonaLine[];
  onOpenPersona?: (id: string) => void;
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
          {scope === 'owner'
            ? <PersonaSliceSection lines={personaLines} onOpen={onOpenPersona} showEmpty />
            : <PersonaSliceExplanation />}
        </div>
      )}
    </div>
  );
}

// === Карточка роли без правил (этап 5) ===
//
// У роли пустая тройка, но есть хотя бы одна персона — значит, она работает по
// «общим настройкам» (по «Любой специальности»). Свёрнутая по умолчанию: править
// нечего, но человек должен видеть, что такие роли есть и кто по ним работает.
// При раскрытии — заголовок T7, срез персон этой роли, и ничего больше.
export function UnruledRoleCard({ role, open, onToggle, personaLines, onOpenPersona }: {
  role: RoleRow;
  open: boolean;
  onToggle: () => void;
  personaLines: RolePersonaLine[];
  onOpenPersona?: (id: string) => void;
}) {
  return (
    <div style={shellStyle({ open })}>
      <button type="button" className={HOVER_CLASS} onClick={onToggle} aria-expanded={open}
        style={{ ...headStyle, gap: SP.sm }}>
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading, flexShrink: 0 }}>
          {role.label}
        </span>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>
          Правил нет — {personasWord(personaLines.length)} работают по общим настройкам
        </span>
        <Chevron open={open} />
      </button>
      {open && (
        <div style={bodyStyle}>
          <PersonaSliceSection lines={personaLines} onOpen={onOpenPersona} showEmpty />
        </div>
      )}
    </div>
  );
}

// (PersonaSliceGroup определён выше — для RuleGroupCard)
