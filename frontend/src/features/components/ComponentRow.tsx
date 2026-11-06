// Строка и карточка панели «Компоненты» (S2c-3, макет components-admin-v1):
// десктопная строка таблицы и мобильная карточка — одна анатомия на оба контекста.
// Состояние строки держится на двух фактах сервера: `enabled` (что записано
// в конфиге) и `active` (что поднято в процессе). Совпали — «Работает» / «Выключен»
// с точкой; расходятся — «ждёт перезапуска»: подсветка C.warningBg, полоса C.warning,
// бейдж с ↻ и строка «Вернуть» (решение 4 макета). «Вернуть» — обратный тумблеру
// запрос, он гасит расхождение бесплатно.
//
// Локального состояния здесь нет: строка ровно та, что пришла с сервера
// (useComponentsAdmin), запись — только через onToggle. «Записываю…» и ошибка
// записи живут в writeState строки (решение 10): оптимистичного применения нет,
// отказ привязан к своей строке, а не всплывает тостом.

import type { CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { AlertTriangle, RotateCcw } from 'lucide-react';
import { Badge, Button, Notice, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Subsystem } from '../../api/subsystems';
import type { RowWriteState } from './useComponentsAdmin';

// Русские падежи счётных слов: 1 обращение / 2 обращения / 5 обращений;
// 11–14 и 21 в русскоязычных списках читаются как «много».
export function pluralForms(n: number, one: string, few: string, many: string): string {
  const d10 = n % 10, d100 = n % 100;
  if (d10 === 1 && d100 !== 11) return one;
  if (d10 >= 2 && d10 <= 4 && (d100 < 12 || d100 > 14)) return few;
  return many;
}

export function pluralRequests(n: number): string {
  return pluralForms(n, 'обращение', 'обращения', 'обращений');
}

// Группировщик разрядов ru-RU: «1 240» читается в списке, «1240» — нет.
export function formatCount(n: number): string {
  return n.toLocaleString('ru-RU');
}

// «сейчас» / «12 мин назад» / «3 ч назад» / «22 дня назад»; null — данных нет.
// Math.round, а не floor: «12 мин назад», записанное ровно 12 минуты назад,
// не должно становиться «11» от миллисекунд задержки между записью и показом.
// `now` — обязательный и живой: часы панели из useNow вызывающего, а не Date.now()
// в фазе рендера (дефолта здесь больше нет: он и был причиной замирающей метки —
// хвост ревью R-S2c-3).
export function formatUsedAgo(lastUsedAt: string | null, now: number): string | null {
  if (!lastUsedAt) return null;
  const t = Date.parse(lastUsedAt);
  if (!Number.isFinite(t)) return null;
  const minRaw = (now - t) / 60000;
  // Отрицательное (часы сервера впереди наших) — «сейчас»: future-метка
  // правдоподобнее, чем «-3 мин назад».
  if (minRaw < 1) return 'сейчас';
  if (minRaw < 60) return `${Math.round(minRaw)} мин назад`;
  if (minRaw < 1440) return `${Math.round(minRaw / 60)} ч назад`;
  const d = Math.round(minRaw / 1440);
  return `${d} ${pluralForms(d, 'день', 'дня', 'дней')} назад`;
}

// Одно состояние на оба контекста (тон бейджа и текст), чтобы тон и текст
// всегда шли парой — как в прежнем экране «Подсистемы».
type RowStatus = 'running' | 'off' | 'pendingOn' | 'pendingOff';

export function rowStatus(s: Subsystem): RowStatus {
  if (s.enabled === s.active) return s.active ? 'running' : 'off';
  return s.enabled ? 'pendingOn' : 'pendingOff';
}

// Короткие глаголы (решение 4): «Включится»/«Выключится» — про что станется
// после перезапуска; пояснение «Вступит в силу после перезапуска» — отдельной
// строкой строки, не в бейдже.
const STATUS_META: Record<RowStatus, { label: string; tone: 'success' | 'neutral' | 'warning' }> = {
  running:    { label: 'Работает',   tone: 'success' },
  off:        { label: 'Выключен',   tone: 'neutral' },
  pendingOn:  { label: 'Включится',  tone: 'warning' },
  pendingOff: { label: 'Выключится', tone: 'warning' },
};

function StatusBadge({ status }: { status: RowStatus }) {
  const m = STATUS_META[status];
  const pending = status === 'pendingOn' || status === 'pendingOff';
  return (
    <Badge
      size="xs"
      tone={m.tone}
      dot={!pending}
      icon={pending ? <RotateCcw size={12} strokeWidth={ICON_STROKE} aria-hidden /> : undefined}
    >
      {m.label}
    </Badge>
  );
}

// Использование в две строки (решение 9): «12 мин назад» / «37 за сутки».
// Пустое значение отличается от нуля: без lastUsedAt — «—» и «нет данных с
// запуска», обе строки приглушены; с нулём обращений — время обычное, счётчик
// «0 за сутки».
function UsageCell({ sub, now }: { sub: Subsystem; now: number }) {
  const ago = formatUsedAgo(sub.lastUsedAt, now);
  const day = sub.usedLast24h ?? 0;
  if (ago === null) {
    return (
      <div>
        <div style={{ fontSize: FS.base, color: C.textSecondary }}>—</div>
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>нет данных с запуска</div>
      </div>
    );
  }
  return (
    <div>
      <div style={{ fontSize: FS.base, color: day > 0 ? C.textPrimary : C.textSecondary }}>{ago}</div>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{formatCount(day)} за сутки</div>
    </div>
  );
}

// Мобильная строка использования — одной строкой (решение 3): «12 мин назад · 37 за сутки».
function usageLineText(sub: Subsystem, now: number): string {
  const ago = formatUsedAgo(sub.lastUsedAt, now);
  if (ago === null) return 'нет данных с запуска';
  return `${ago} · ${formatCount(sub.usedLast24h ?? 0)} за сутки`;
}

function SavingSpinner() {
  return (
    <span
      aria-hidden
      style={{
        width: 12, height: 12, borderRadius: '50%', display: 'inline-block', flexShrink: 0,
        border: `2px solid ${C.track}`, borderTopColor: C.accent,
        animation: 'cc-spin 0.9s linear infinite',
      }}
    />
  );
}

// Вертикальная полоса состояния у кромки строки: warning — «ждёт перезапуска»,
// danger — ошибка записи. Псевдоэлемент макета здесь — обычный слой: inline-стили
// не умеют ::before.
function Stripe({ color }: { color: string }) {
  return (
    <div
      aria-hidden
      style={{
        position: 'absolute', left: 0, top: SP.sm, bottom: SP.sm, width: 3,
        borderRadius: 2, background: color,
      }}
    />
  );
}

// Текстовая ссылка внутри строки (макет .lnk): Button с рамкой сделал бы второй
// контрол в строке, где уже есть тумблер; «Вернуть» — действие строки, не кнопка.
const LINK_STYLE: CSSProperties = {
  background: 'none', border: 'none', padding: 0, cursor: 'pointer',
  color: C.accent, fontSize: FS.sm, fontWeight: 600, fontFamily: 'inherit',
};

function PendingHint({ sub, onRevert }: { sub: Subsystem; onRevert: () => void }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm, color: C.warningText }}>
      <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0 }} />
      <span>
        Вступит в силу после перезапуска ·{' '}
        {/* Что СЕЙЧАС делает компонент — то и говорим: «ещё работает» или
            «выключен» — человек сверяет бейдж с тем, что видит в продукте. */}
        <span style={{ color: C.textPrimary }}>{sub.active ? 'сейчас ещё работает' : 'сейчас выключен'}</span>
        {' · '}
        <button type="button" onClick={onRevert} style={LINK_STYLE}>Вернуть</button>
      </span>
    </div>
  );
}

// Ошибка записи — внутри своей строки (решение 10): тон danger, причина сервера
// как есть, «Положение не изменилось» — потому что items хук не трогал при отказе.
function WriteErrorNotice({ error, onRetry }: { error: string; onRetry: () => void }) {
  return (
    <Notice
      tone="danger"
      icon={AlertTriangle}
      role="alert"
      title="Не удалось записать настройку"
      action={<Button variant="ghost" size="sm" onClick={onRetry}>Повторить</Button>}
    >
      {error}. Положение не изменилось.
    </Notice>
  );
}

interface Props {
  sub: Subsystem;
  write: RowWriteState;
  isMobile: boolean;
  // «Сейчас» панели — из useNow вызывающего (один тик на все строки и диалог),
  // а не Date.now() каждой фазы рендера: метка использования не замирает (хвост R-S2c-3).
  now: number;
  onToggle: (next: boolean) => void;
  // «Вернуть» = записать enabled обратно в active (обратный toggle).
  onRevert: () => void;
  // «Повторить» = повторить последний запрос: при отказе items не менялись,
  // значит желаемое значение строки по-прежнему !enabled — доп. состояния не нужно.
  onRetry: () => void;
}

export function ComponentRow({ sub, write, isMobile, now, onToggle, onRevert, onRetry }: Props) {
  const status = rowStatus(sub);
  const pending = status === 'pendingOn' || status === 'pendingOff';
  const saving = write === 'saving';
  const error = typeof write === 'object' ? write.error : null;

  const toggleControl = (
    <Toggle
      checked={sub.enabled}
      disabled={saving}
      focusable
      ariaLabel={`Компонент ${sub.title}`}
      onChange={onToggle}
    />
  );

  if (!isMobile) {
    return (
      <div
        data-testid={`components-row-${sub.key}`}
        style={{
          position: 'relative',
          display: 'grid',
          gridTemplateColumns: 'minmax(0,1fr) 150px 140px 52px',
          gap: SP.md,
          alignItems: 'start',
          padding: `${SP.md}px`,
          borderBottom: `1px solid ${C.borderLight}`,
          background: pending ? C.warningBg : undefined,
        }}
      >
        {pending && <Stripe color={C.warning} />}
        {error && <Stripe color={C.danger} />}

        <div>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
            <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{sub.title}</span>
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{sub.key}</span>
          </div>
          {sub.description && (
            <div style={{ fontSize: FS.sm, lineHeight: 1.45, color: C.textSecondary, marginTop: 2 }}>
              {sub.description}
            </div>
          )}
        </div>

        <UsageCell sub={sub} now={now} />

        <div>
          <StatusBadge status={status} />
          {saving && (
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted }}>
              <SavingSpinner />
              Записываю…
            </div>
          )}
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', paddingTop: 1 }}>{toggleControl}</div>

        {pending && <div style={{ gridColumn: '1 / -1' }}><PendingHint sub={sub} onRevert={onRevert} /></div>}
        {error && <div style={{ gridColumn: '1 / -1' }}><WriteErrorNotice error={error} onRetry={onRetry} /></div>}
      </div>
    );
  }

  return (
    <div
      data-testid={`components-row-${sub.key}`}
      style={{
        background: pending ? C.warningBg : C.bgWhite,
        border: `1px solid ${pending ? C.warning : error ? C.dangerBorder : C.borderLight}`,
        borderRadius: R.xl,
        padding: SP.md,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{sub.title}</span>
        {saving && <SavingSpinner />}
        {/* Зона нажатия тумблера добита до 41 px (25 + 8 + 8) — тач-порог макета;
            margin компенсирует padding, строка не разъезжается. */}
        <div style={{ padding: `${SP.sm}px 0`, margin: `-${SP.sm}px 0`, display: 'flex' }}>{toggleControl}</div>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{sub.key}</span>
        <StatusBadge status={status} />
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.sm }}>{usageLineText(sub, now)}</div>
      {pending && <div style={{ marginTop: SP.md }}><PendingHint sub={sub} onRevert={onRevert} /></div>}
      {error && <div style={{ marginTop: SP.md }}><WriteErrorNotice error={error} onRetry={onRetry} /></div>}
    </div>
  );
}
