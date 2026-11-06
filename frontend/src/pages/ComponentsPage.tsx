// Панель админа «Компоненты» (S2c-3, шаг 2c фичи «Внутренняя оптимизация»):
// прежний экран «Подсистемы», достроенный по макету docs/mockups/components-admin-v1.html
// и его предложению (решения 1–11). Тот же экран с тумблером и использованием —
// поэтому и пункт меню переименован, а не добавлен новый (решение 1).
//
// Данные — хук useComponentsAdmin (S2c-2): источник правды — сервер, оптимистичного
// применения нет; строка меняется целым ответом PUT или следующим GET.
//
// Границы экрана:
// • «ждёт перезапуска» читается из расхождения enabled/active в ОТВЕТЕ сервера,
//   а не из «только что кликнули» — видно и через час (решение 4);
// • подтверждение спрашиваем только при выключении работающего компонента, к которому
//   обращались за сутки (решение 8); порог — CONFIRM_USED_LAST24H_THRESHOLD, одно место;
// • ядро (canDisable=false) — отдельной группой под списком, без тумблера вовсе
//   (решение 7); использование ядра не показываем (открытый вопрос 4 макета);
// • «Отменить все» нет до решения Андрея (открытый вопрос 2): хватает «Вернуть»
//   в каждой расходящейся строке;
// • кнопки «Перезапустить» нет: рестарт обрывает ходы всех пользователей (решение 6).

import { useState } from 'react';
import { C, FONT, FS, R, SP, MODAL_W } from '../lib/design';
import { Badge, Button, ConfirmDialog, EmptyState, Modal, Notice } from '../components/ui';
import { AlertCircle, Blocks, Lock, RotateCcw } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { useNow } from '../lib/useNow';
import { useComponentsAdmin } from '../features/components/useComponentsAdmin';
import { ComponentRow, formatCount, formatUsedAgo, pluralForms, pluralRequests } from '../features/components/ComponentRow';
import type { Subsystem } from '../api/subsystems';

interface Props {
  onClose: () => void;
}

// Порог подтверждения (решение 8 макета): диалог — только при выключении
// работающего компонента, к которому обращались хотя бы раз за сутки. Открытый
// вопрос 1 («поднять ли порог») решает Андрей; значение по макету живёт здесь,
// в одном месте.
export const CONFIRM_USED_LAST24H_THRESHOLD = 1;

// Пульс скелета загрузки (1,4 с); при prefers-reduced-motion — без анимации
// (паттерн — FeaturePage).
const SKELETON_CLASS = 'cc-components-skeleton';
if (typeof document !== 'undefined' && !document.getElementById('cc-components-skeleton-style')) {
  const el = document.createElement('style');
  el.id = 'cc-components-skeleton-style';
  el.textContent =
    `@keyframes cc-components-sk-pulse{0%,100%{opacity:1}50%{opacity:0.45}}` +
    `.${SKELETON_CLASS}{animation:cc-components-sk-pulse 1.4s ease-in-out infinite;}` +
    `@media (prefers-reduced-motion: reduce){.${SKELETON_CLASS}{animation:none;opacity:0.55}}`;
  document.head.appendChild(el);
}

export function ComponentsPage({ onClose }: Props) {
  const { items, loading, loadError, reload, toggle, writeState } = useComponentsAdmin();
  const isMobile = useIsMobile();
  // Один «сейчас» на всю панель: строки и диалог подтверждения идут по одним часам
  // (паттерн FeatureTimelineTab, GC-F8R2), а не по Date.now() каждой фазы рендера:
  // открытая панель не должна держать замороженное «12 мин назад» (хвост ревью R-S2c-3).
  const now = useNow(60_000);
  // Диалог держим ключом, а не копией строки: если список перечитался,
  // подтверждение относится к нынешней строке или исчезает вместе с ней.
  const [confirmingKey, setConfirmingKey] = useState<string | null>(null);

  const disableRows = items.filter(s => s.canDisable);
  const coreRows = items.filter(s => !s.canDisable);
  // Расхождения — записи, где записанное не равно поднятому. Тот же признак,
  // по которому строка красит «ждёт перезапуска»: плашка и строки всегда согласны.
  const diverged = items.filter(s => s.enabled !== s.active);
  const confirming = confirmingKey === null ? null : items.find(s => s.key === confirmingKey) ?? null;

  const requestToggle = (s: Subsystem, next: boolean) => {
    // Подтверждение — только выключение того, что сейчас реально работает
    // (active) и к чему обращались за сутки. Включение и выключение
    // неиспользуемого проходят без вопросов (решение 8).
    if (!next && s.enabled && s.active && (s.usedLast24h ?? 0) >= CONFIRM_USED_LAST24H_THRESHOLD) {
      setConfirmingKey(s.key);
      return;
    }
    void toggle(s.key, next);
  };

  return (
    <Modal
      title="Компоненты"
      subtitle="Разделы продукта для всего инстанса. Изменение записывается в настройки сервера и вступает в силу после его перезапуска."
      width={MODAL_W.wide}
      onClose={onClose}
    >
      {loading && <SkeletonTable isMobile={isMobile} />}

      {/* Первая загрузка упала: списка нет — EmptyState с «Повторить» (решение 11). */}
      {!loading && loadError && items.length === 0 && (
        <div data-testid="components-load-error">
          <EmptyState
            inline
            icon={<AlertCircle size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} style={{ color: C.dangerText }} />}
            title="Не удалось загрузить список"
            subtitle="Сервер не ответил. Состояние компонентов это не меняет — попробуйте ещё раз."
            action={<Button variant="ghost" size="sm" onClick={reload}>Повторить</Button>}
          />
        </div>
      )}
      {/* Перечитывание упало при уже известном списке: на экране только Notice с причиной
          и «Повторить» — строки закрыты общим !loadError ниже и не рендерятся. Хук items
          при этом не чистит (useComponentsAdmin), так что при автоперечитывании прежние
          строки остались бы последним известным состоянием — если до этого дойдёт,
          решать здесь. В нынешнем потоке состояние недостижимо: второй GET возможен
          только по «Повторить», а он требует loadError, который после успешного GET
          сброшен (хвост ревью R-S2c-3: комментарий раньше описывал поведение, которого
          нет). */}
      {!loading && loadError && items.length > 0 && (
        <Notice
          tone="danger"
          icon={AlertCircle}
          role="alert"
          title="Не удалось перечитать список"
          action={<Button variant="ghost" size="sm" onClick={reload}>Повторить</Button>}
        >
          {loadError}
        </Notice>
      )}

      {!loading && !loadError && (
        <>
          {diverged.length > 0 && <PendingBanner diverged={diverged} isMobile={isMobile} />}

          {disableRows.length === 0 ? (
            /* Пусто: отключаемых нет — EmptyState, и группа ядра ниже всё равно видна. */
            <div data-testid="components-empty">
              <EmptyState
                inline
                icon={<Blocks size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                title="Отключаемых компонентов нет"
                subtitle="Сервер не сообщил ни одного компонента, который можно выключить. Ядро работает всегда — его список ниже."
              />
            </div>
          ) : isMobile ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              {disableRows.map(s => (
                <ComponentRow
                  key={s.key}
                  sub={s}
                  write={writeState[s.key] ?? 'idle'}
                  isMobile
                  now={now}
                  onToggle={(next) => requestToggle(s, next)}
                  onRevert={() => void toggle(s.key, s.active)}
                  onRetry={() => void toggle(s.key, !s.enabled)}
                />
              ))}
            </div>
          ) : (
            <div>
              <TableHeader />
              {disableRows.map(s => (
                <ComponentRow
                  key={s.key}
                  sub={s}
                  write={writeState[s.key] ?? 'idle'}
                  isMobile={false}
                  now={now}
                  onToggle={(next) => requestToggle(s, next)}
                  onRevert={() => void toggle(s.key, s.active)}
                  onRetry={() => void toggle(s.key, !s.enabled)}
                />
              ))}
            </div>
          )}

          {coreRows.length > 0 && <CoreGroup rows={coreRows} isMobile={isMobile} />}

          {disableRows.length > 0 && (
            <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
              Значение пишется ключом <span style={{ fontFamily: FONT.mono }}>Subsystems:{'{Key}'}:Enabled</span>{' '}
              в машинный конфиг сервера. Использование — с последнего запуска процесса.
            </div>
          )}
        </>
      )}

      {confirming && (
        <ConfirmDialog
          title={`Выключить «${confirming.title}»?`}
          subtitle={<ConfirmBody sub={confirming} now={now} />}
          confirmLabel="Выключить"
          confirmVariant="danger"
          onConfirm={async () => {
            await toggle(confirming.key, false);
            setConfirmingKey(null);
          }}
          onCancel={() => setConfirmingKey(null)}
        />
      )}
    </Modal>
  );
}

// Тело подтверждения (решение 8): факт использования + что будет с данными.
// «Файлы на диске останутся» — то, ради чего диалог честный: выключение не удаляет
// ничего, и передумать можно тем же тумблером до перезапуска.
function ConfirmBody({ sub, now }: { sub: Subsystem; now: number }) {
  const used = sub.usedLast24h ?? 0;
  const ago = formatUsedAgo(sub.lastUsedAt, now);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        background: C.bgInset, borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`,
        fontSize: FS.sm, lineHeight: 1.45, color: C.textPrimary,
      }}>
        <AlertCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: 1 }} />
        <span>
          Им пользовались: <b style={{ fontWeight: 600 }}>{formatCount(used)} {pluralRequests(used)} за сутки</b>
          {/* «последнее — …» только когда время есть: без него фраза обрывалась бы на «—». */}
          {ago !== null && <> , последнее — {ago}</>}
          .
        </span>
      </div>
      <div style={{ fontSize: FS.base, lineHeight: 1.5, color: C.textPrimary }}>
        После перезапуска сервера раздел, его панели и инструменты агента пропадут у всех
        пользователей. Файлы на диске останутся: включите компонент обратно — всё вернётся.
      </div>
      <div style={{ color: C.textSecondary, fontSize: FS.sm }}>
        До перезапуска всё работает как сейчас, и передумать можно тем же тумблером.
      </div>
    </div>
  );
}

// Плашка расхождений (решения 5–6): прилипает к верху прокрутки, считает изменения
// и перечисляет их глаголом будущего («выключится» — что станется после рестарта).
// Кнопки «Перезапустить» нет и кнопки «Отменить все» нет (открытый вопрос 2):
// каждое изменение гасится «Вернуть» в своей строке.
function PendingBanner({ diverged, isMobile }: { diverged: Subsystem[]; isMobile: boolean }) {
  const n = diverged.length;
  const word = pluralForms(n, 'изменение ждёт', 'изменения ждут', 'изменений ждут');
  const list = diverged.map(s => `«${s.title}» ${s.enabled ? 'включится' : 'выключится'}`).join(', ');
  return (
    <div data-testid="components-banner" style={{ position: 'sticky', top: 0, zIndex: 2, background: C.bgMain, paddingBottom: SP.sm }}>
      <Notice tone="warning" icon={RotateCcw} title={`${n} ${word} перезапуска сервера`}>
        <div>Записано в настройки. До перезапуска всё работает по-прежнему: {list}.</div>
        {/* Сноска — только на десктопе: на мобиле плашка и так занимает треть шторки. */}
        {!isMobile && (
          <div style={{ fontSize: FS.xs, color: C.textPrimary, opacity: 0.75, marginTop: SP.xs }}>
            Кнопки перезапуска здесь нет: рестарт обрывает идущие ходы всех пользователей — выберите момент сами.
          </div>
        )}
      </Notice>
    </div>
  );
}

function TableHeader() {
  return (
    <div style={{
      display: 'grid',
      gridTemplateColumns: 'minmax(0,1fr) 150px 140px 52px',
      gap: SP.md,
      fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: '0.05em',
      color: C.textMuted,
      padding: `0 ${SP.md}px ${SP.sm}px`,
      borderBottom: `1px solid ${C.borderLight}`,
    }}>
      <span>Компонент</span>
      <span>Использование</span>
      <span>Состояние</span>
      <span style={{ textAlign: 'right' }}>Вкл.</span>
    </div>
  );
}

// Группа ядра (решение 7): замок, счётчик и бейдж «Часть ядра»; тумблера нет совсем —
// тумблер, который ничего не делает, это ложь интерфейса. Использование ядра не
// показываем (открытый вопрос 4): выключить ядро нельзя, цифры только шумят.
function CoreGroup({ rows, isMobile }: { rows: Subsystem[]; isMobile: boolean }) {
  return (
    <div data-testid="components-core">
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        margin: `${SP.lg}px 0 ${SP.sm}px`,
        fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: '0.05em', color: C.textMuted,
      }}>
        <Lock size={12} strokeWidth={ICON_STROKE} aria-hidden />
        <span>Ядро · {rows.length} · работает всегда</span>
        <span style={{ flex: 1, height: 1, background: C.borderLight }} />
      </div>
      {isMobile ? (
        <div style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl }}>
          {rows.map((s, i) => (
            <div key={s.key} style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              padding: `${SP.sm}px ${SP.md}px`, minHeight: 40,
              borderBottom: i < rows.length - 1 ? `1px solid ${C.borderLight}` : 'none',
            }}>
              <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{s.title}</span>
              <Badge size="xs" tone="neutral" icon={<Lock size={12} strokeWidth={ICON_STROKE} aria-hidden />}>Часть ядра</Badge>
            </div>
          ))}
        </div>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0,1fr))', columnGap: SP.lg }}>
          {rows.map(s => (
            <div key={s.key} style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              padding: `${SP.sm}px ${SP.md}px`, minHeight: 40,
              borderBottom: `1px solid ${C.borderLight}`,
            }}>
              <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{s.title}</span>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{s.key}</span>
              <Badge size="xs" tone="neutral" icon={<Lock size={12} strokeWidth={ICON_STROKE} aria-hidden />} style={{ marginLeft: 'auto' }}>Часть ядра</Badge>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// Скелетон строк при загрузке (решение 11): та же сетка, что таблица, но вместо
// содержимого — пульсирующие полосы C.bgInset. Мобильный вариант — карточки.
function SkeletonTable({ isMobile }: { isMobile: boolean }) {
  const bar = (width: string, height = 12) => (
    <div className={SKELETON_CLASS} style={{ background: C.bgInset, borderRadius: R.sm, height, width }} />
  );
  if (isMobile) {
    return (
      <div data-testid="components-skeleton" aria-busy="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {[0, 1, 2, 3].map(i => (
          <div key={i} style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.md }}>
              {bar(`${55 + i * 8}%`, 14)}
              <div className={SKELETON_CLASS} style={{ background: C.bgInset, borderRadius: R.max, width: 42, height: 25, marginLeft: 'auto' }} />
            </div>
            <div style={{ marginTop: SP.md }}>{bar('40%')}{bar('60%', 12)}</div>
          </div>
        ))}
      </div>
    );
  }
  return (
    <div data-testid="components-skeleton" aria-busy="true">
      <div style={{
        display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 90px 52px', gap: SP.md,
        fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: '0.05em', color: C.textMuted,
        padding: `0 ${SP.md}px ${SP.sm}px`, borderBottom: `1px solid ${C.borderLight}`,
      }}>
        <span>Компонент</span>
        <span>Использование</span>
        <span>Вкл.</span>
      </div>
      {[70, 55, 80, 60].map((w, i) => (
        <div key={i} data-testid="components-skeleton-row" style={{
          display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 90px 52px', gap: SP.md,
          padding: `${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
        }}>
          <div>{bar(`${w}%`, 14)}<div style={{ height: SP.xs }} />{bar(`${w + 15}%`)}</div>
          <div>{bar('70%')}<div style={{ height: SP.xs }} />{bar('50%')}</div>
          <div className={SKELETON_CLASS} style={{ background: C.bgInset, borderRadius: R.max, width: 42, height: 25 }} />
        </div>
      ))}
    </div>
  );
}
