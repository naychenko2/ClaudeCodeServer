// Экран админа «Подсистемы» (Этап 5, волна 3): список подсистем инстанса
// с тумблерами включения/выключения. Экран собран по макету
// `docs/mockups/subsystems-admin.md`: контейнер Modal, пять состояний
// строки (работает/выключена/включится/выключится после перезапуска/
// встроенная), баннер о необходимости перезапуска при расхождении
// configured/runtime и подтверждение выключения через ConfirmDialog.
//
// Имя файла — SubsystemsPage по исторической конвенции `pages/`, хотя по
// геометрии это модалка, открываемая из меню аватара (волна 4). Внутри
// `pages/` живут и модалки тоже — примеры FeatureFlagsModal и UserManagement
// подтверждают.
//
// Двухфактная модель состояния строки — ключевая:
//   enabledConfigured = что записано в конфиге инстанса;
//   enabledRuntime    = что реально поднято в текущем процессе.
// Совпадают → один из «Работает» / «Выключена». Расхождение → один из
// «Включится после перезапуска» / «Выключится после перезапуска», плюс
// баннер сверху списка. Без рубильника (canToggle: false) → «Встроенная»,
// тумблера нет физически.

import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal, Toggle, Badge, ConfirmDialog } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { subsystemsApi, type Subsystem } from '../api/subsystems';
import { setAllSubsystems, getAllSubsystems } from '../lib/subsystems';
import { C, FONT, FS, SP, R, MODAL_W } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { RotateCcw } from 'lucide-react';
import { showToast } from '../lib/toast';

interface Props {
  onClose: () => void;
}

// Состояние строки — пять исходов по макету. Один вычислитель на оба
// контекста (тон Badge и текст), чтобы тон и текст всегда шли парой.
type Status = 'running' | 'off' | 'pendingOn' | 'pendingOff' | 'builtin';

function computeStatus(s: Subsystem): Status {
  if (!s.canToggle) return 'builtin';
  if (s.enabledConfigured === s.enabledRuntime) {
    return s.enabledRuntime ? 'running' : 'off';
  }
  return s.enabledConfigured ? 'pendingOn' : 'pendingOff';
}

// Тон и текст статуса. `pendingOn` и `pendingOff` делят текст «…после
// перезапуска», но отличаются глаголом — глагол идёт в тело баннера
// («включены» / «выключены»), а сама плашка короткая.
const STATUS_TEXT: Record<Status, { label: string; tone: 'success' | 'neutral' | 'warning' }> = {
  running:   { label: 'Работает',                     tone: 'success' },
  off:       { label: 'Выключена',                    tone: 'neutral' },
  pendingOn: { label: 'Включится после перезапуска',  tone: 'warning' },
  pendingOff:{ label: 'Выключится после перезапуска', tone: 'warning' },
  builtin:   { label: 'Встроенная',                   tone: 'neutral' },
};

export function SubsystemsPage({ onClose }: Props) {
  const [subsystems, setSubsystems] = useState<Subsystem[]>([]);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'error'>('loading');
  // Ключи, по которым сейчас летит PUT — блокируем их тумблер до ответа.
  // Та же модель, что в FeatureFlagsModal: тумблер визуально занят, на
  // повторный клик не реагирует.
  const [busy, setBusy] = useState<Set<string>>(new Set());
  // Подтверждение выключения: показываем ConfirmDialog рядом с основной
  // модалкой, пока пользователь не решит.
  const [confirmDisable, setConfirmDisable] = useState<Subsystem | null>(null);
  const isMobile = useIsMobile();

  useEffect(() => {
    let cancelled = false;
    subsystemsApi.get()
      .then(({ subsystems }) => {
        if (cancelled) return;
        setSubsystems(subsystems);
        setLoadState('ok');
      })
      .catch(() => { if (!cancelled) setLoadState('error'); });
    return () => { cancelled = true; };
  }, []);

  // Переключение тумблера. По макету НЕ оптимистично: перерисовываем по
  // ответу сервера. Выключение идёт через ConfirmDialog — здесь только
  // открываем его, реальный PUT делает onConfirmDisable ниже.
  const toggle = async (sub: Subsystem, next: boolean) => {
    if (!sub.canToggle) return;
    if (!next) {
      setConfirmDisable(sub);
      return;
    }
    await applyToggle(sub, next);
  };

  const applyToggle = async (sub: Subsystem, next: boolean) => {
    setBusy(s => new Set(s).add(sub.key));
    try {
      const updated = await subsystemsApi.set(sub.key, next);
      // Сервер сохранил значение — подставляем его в список по ключу.
      // Остальные записи не трогаем: PUT касается одной подсистемы.
      setSubsystems(arr => arr.map(s => s.key === updated.key ? updated : s));
      // Глобальный стор подсистем (источник правды для useSubsystem в
      // гейтах раздела) обновляем параллельно — иначе NotesPage и
      // HubTabs продолжат видеть устаревшее значение до следующей
      // перезагрузки вкладки / перезапуска сервера.
      const cur = getAllSubsystems();
      setAllSubsystems({ ...cur, [updated.key]: updated.enabledConfigured });
    } catch (e) {
      // Мая: «нет свидетельств = не готово». На ошибке ничего не откатываем —
      // мы и не меняли UI до ответа, откатывать нечего; показываем toast
      // с текстом от сервера (он бросает человеческий 4xx/5xx).
      showToast('Подсистемы', e instanceof Error ? e.message : 'Не удалось изменить состояние подсистемы');
    } finally {
      setBusy(b => { const n = new Set(b); n.delete(sub.key); return n; });
    }
  };

  const onConfirmDisable = async () => {
    if (!confirmDisable) return;
    const sub = confirmDisable;
    setConfirmDisable(null);
    await applyToggle(sub, false);
  };

  // Расхождения — записи, где configured != runtime. Их показ в баннере
  // единственный смысл блока: пустой массив — баннер не рисуется.
  const diverged = subsystems.filter(s => s.enabledConfigured !== s.enabledRuntime);

  return (
    <>
      <Modal
        title="Подсистемы"
        subtitle="Разделы продукта, которые можно отключить. Настройка общая для всего инстанса и применяется при старте сервера."
        width={MODAL_W.form}
        onClose={onClose}
      >
        {loadState === 'loading' && (
          <div style={{ color: C.textMuted, fontSize: FS.md, padding: `${SP.xs}px 0` }}>Загрузка…</div>
        )}
        {loadState === 'error' && (
          <div style={{ color: C.danger, fontSize: FS.md, padding: `${SP.xs}px 0` }}>
            Не удалось загрузить список
          </div>
        )}
        {loadState === 'ok' && subsystems.length === 0 && (
          <div style={{ color: C.textMuted, fontSize: FS.md, padding: `${SP.xs}px 0` }}>
            Пока нет подсистем с переключателем
          </div>
        )}
        {loadState === 'ok' && subsystems.length > 0 && (
          <>
            {diverged.length > 0 && <RestartBanner diverged={diverged} />}
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              {subsystems.map((sub, i) => (
                <SubsystemRow
                  key={sub.key}
                  sub={sub}
                  isFirst={i === 0}
                  isMobile={isMobile}
                  busy={busy.has(sub.key)}
                  onToggle={(next) => toggle(sub, next)}
                />
              ))}
            </div>
          </>
        )}
      </Modal>
      {confirmDisable && (
        <ConfirmDialog
          title={`Выключить «${confirmDisable.title}»?`}
          subtitle="После перезапуска раздел, его панели и инструменты исчезнут из интерфейса. Файлы подсистемы останутся на диске: включите подсистему обратно — всё вернётся на место."
          confirmLabel="Выключить"
          confirmVariant="danger"
          onConfirm={onConfirmDisable}
          onCancel={() => setConfirmDisable(null)}
        />
      )}
    </>
  );
}

// Баннер «Перезапустите сервер»: появляется, только если есть хотя бы
// одно расхождение. Тело перечисляет расхождения через запятую в порядке
// списка. Сноска объясняет отсутствие кнопки — рестарт обрывает чужие
// ходы, кнопка рядом с тумблером превратила бы настройку в разрыв сессий
// одним кликом.
function RestartBanner({ diverged }: { diverged: Subsystem[] }) {
  // Что СЕЙЧАС работает (runtime) — это и есть то, что пользователь
  // продолжает видеть после клика, до перезапуска. Именно это должно
  // стоять в теле баннера («сейчас всё работает по-прежнему: …»).
  const parts = diverged.map(s => {
    const currently = s.enabledRuntime ? 'включены' : 'выключены';
    return `«${s.title}» ${currently}`;
  });
  const isSingle = diverged.length === 1;
  const body = isSingle
    ? `Настройка сохранена. Подсистемы поднимаются при старте, поэтому сейчас всё работает по-прежнему: ${parts[0]}.`
    : `Настройки сохранены. Подсистемы поднимаются при старте, поэтому сейчас всё работает по-прежнему: ${parts.join(', ')}.`;

  return (
    <div role="status" style={{
      display: 'flex',
      gap: SP.md,
      background: C.warningBg,
      border: `1px solid ${C.warning}`,
      borderRadius: R.xl,
      padding: SP.md,
      marginBottom: SP.sm,
    }}>
      <RotateCcw
        size={ICON_SIZE.sm}
        strokeWidth={ICON_STROKE}
        color={C.warningText}
        style={{ flexShrink: 0, marginTop: 1 }}
      />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontSize: FS.base,
          fontWeight: 600,
          color: C.warningText,
          lineHeight: 1.3,
        }}>
          Перезапустите сервер, чтобы изменения вступили в силу
        </div>
        <div style={{
          fontSize: FS.sm,
          lineHeight: 1.45,
          color: C.textPrimary,
          marginTop: SP.xs,
        }}>
          {body}
        </div>
        <div style={{
          fontSize: FS.xs,
          color: C.textPrimary,
          opacity: 0.75,
          marginTop: SP.xs,
        }}>
          Кнопки перезапуска здесь нет: рестарт обрывает идущие ходы и чужие сессии — выберите момент сами.
        </div>
      </div>
    </div>
  );
}

// Строка подсистемы — анатомия по макету. Правая колонка (тумблер +
// плашка) перестраивается под мобильную раскладку: тумблер остаётся
// справа, плашка статуса спускается под описание отдельной строкой.
function SubsystemRow({
  sub, isFirst, isMobile, busy, onToggle,
}: {
  sub: Subsystem;
  isFirst: boolean;
  isMobile: boolean;
  busy: boolean;
  onToggle: (next: boolean) => void;
}) {
  const status = computeStatus(sub);
  const text = STATUS_TEXT[status];
  // На десктопе плашка статуса прижата к правому краю под тумблером —
  // глаз идёт по вертикали одной колонки. На мобиле ушла под описание,
  // и без неё строка выглядит «голой» снизу.
  const rowStyle: CSSProperties = {
    display: 'flex',
    flexDirection: isMobile ? 'column' : 'row',
    alignItems: isMobile ? 'stretch' : 'flex-start',
    gap: SP.md,
    padding: `${SP.md}px 0`,
    borderTop: isFirst ? 'none' : `1px solid ${C.borderLight}`,
  };
  // На мобиле tap-цель тумблера визуально 42×25, но палец промахивается
  // по высоте — дотягиваем зону нажатия до 40 по вертикали отрицательным
  // маргином, как у соседних кнопок тулбара.
  const toggleWrapStyle: CSSProperties | undefined = isMobile
    ? { padding: '8px 0', margin: '-8px 0', alignSelf: 'flex-end' }
    : undefined;

  return (
    <div style={rowStyle}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>
            {sub.title}
          </span>
          <span style={{
            fontFamily: FONT.mono,
            fontSize: FS.xs,
            color: C.textMuted,
          }}>
            {sub.key}
          </span>
        </div>
        <div style={{
          fontSize: FS.sm,
          lineHeight: 1.45,
          color: C.textSecondary,
          marginTop: 3,
        }}>
          {sub.description}
        </div>
        {isMobile && (
          <div style={{ marginTop: SP.sm }}>
            <Badge size="xs" dot tone={text.tone}>{text.label}</Badge>
          </div>
        )}
      </div>
      <div style={{
        flexShrink: 0,
        paddingTop: isMobile ? 0 : SP.xxs,
        display: 'flex',
        flexDirection: isMobile ? 'row' : 'column',
        alignItems: isMobile ? 'center' : 'flex-end',
        gap: SP.xs,
      }}>
        {sub.canToggle ? (
          <div style={toggleWrapStyle}>
            <Toggle
              checked={sub.enabledConfigured}
              onChange={onToggle}
              disabled={busy}
              focusable
              ariaLabel={`Подсистема ${sub.title}`}
            />
          </div>
        ) : (
          // Без рубильника: тумблера нет физически (disabled-тумблер
          // маскировал бы ложь интерфейса). Подпись — встроенная с
          // подсказкой по наведению.
          <Badge
            size="xs"
            tone="neutral"
            title="Часть ядра — отключить нельзя."
          >
            Встроенная
          </Badge>
        )}
        {!isMobile && sub.canToggle && (
          <Badge size="xs" dot tone={text.tone}>{text.label}</Badge>
        )}
      </div>
    </div>
  );
}
