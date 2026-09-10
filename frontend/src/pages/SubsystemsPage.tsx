// Экран админа «Подсистемы» (Этап 5, волна 3): список подсистем инстанса
// в режиме чтения. Экран собран по макету
// `docs/mockups/subsystems-admin.md`: контейнер Modal (`MODAL_W.form`), пять
// состояний строки (работает/выключена/включится/выключится после
// перезапуска/встроенная — последняя сейчас недостижима: бэк не отдаёт
// признак `canToggle`, и в этой итерации все подсистемы с рубильником),
// баннер о необходимости перезапуска при расхождении configured/runtime и
// подсказка про `appsettings` под подзаголовком.
//
// Имя файла — SubsystemsPage по исторической конвенции `pages/`, хотя по
// геометрии это модалка, открываемая из меню аватара. Внутри `pages/` живут
// и модалки тоже — примеры FeatureFlagsModal и UserManagement подтверждают.
//
// Двухфактная модель состояния строки — ключевая:
//   enabled = что записано в конфиге инстанса;
//   active  = что реально поднято в текущем процессе.
// Совпадают → один из «Работает» / «Выключена». Расхождение → один из
// «Включится после перезапуска» / «Выключится после перезапуска», плюс
// баннер сверху списка.
//
// PUT отсутствует намеренно: глобальный рубильник через API — отдельная
// фича (см. задачу 7d261cde). Экран закрывает потребность «видно, что
// включено», тумблер не рисуется.

import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Modal, Badge } from '../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../components/ui/icons';
import { subsystemsApi, type Subsystem } from '../api/subsystems';
import { C, FONT, FS, SP, R, MODAL_W } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { RotateCcw } from 'lucide-react';

interface Props {
  onClose: () => void;
}

// Состояние строки — четыре исхода по макету (без «Встроенная», потому что
// бэк сейчас не отдаёт признак `canToggle`). Один вычислитель на оба контекста
// (тон Badge и текст), чтобы тон и текст всегда шли парой.
type Status = 'running' | 'off' | 'pendingOn' | 'pendingOff';

function computeStatus(s: Subsystem): Status {
  if (s.enabled === s.active) {
    return s.active ? 'running' : 'off';
  }
  return s.enabled ? 'pendingOn' : 'pendingOff';
}

// Тон и текст статуса. `pendingOn` и `pendingOff` делят текст «…после
// перезапуска», но отличаются глаголом — глагол идёт в тело баннера
// («включены» / «выключены»), а сама плашка короткая.
const STATUS_TEXT: Record<Status, { label: string; tone: 'success' | 'neutral' | 'warning' }> = {
  running:   { label: 'Работает',                     tone: 'success' },
  off:       { label: 'Выключена',                    tone: 'neutral' },
  pendingOn: { label: 'Включится после перезапуска',  tone: 'warning' },
  pendingOff:{ label: 'Выключится после перезапуска', tone: 'warning' },
};

export function SubsystemsPage({ onClose }: Props) {
  const [subsystems, setSubsystems] = useState<Subsystem[]>([]);
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'error'>('loading');
  const isMobile = useIsMobile();

  useEffect(() => {
    let cancelled = false;
    subsystemsApi.get()
      .then(list => {
        if (cancelled) return;
        // Форма ответа — голый массив. Проверка `Array.isArray` не косметика:
        // при рассинхроне контракта экран обязан показать «не удалось
        // загрузить», а не падать в общий ErrorBoundary, унося весь интерфейс
        // до перезагрузки страницы.
        if (!Array.isArray(list)) {
          setLoadState('error');
          return;
        }
        setSubsystems(list);
        setLoadState('ok');
      })
      .catch(() => { if (!cancelled) setLoadState('error'); });
    return () => { cancelled = true; };
  }, []);

  // Расхождения — записи, где enabled != active. Их показ в баннере
  // единственный смысл блока: пустой массив — баннер не рисуется.
  const diverged = subsystems.filter(s => s.enabled !== s.active);

  return (
    <Modal
      title="Подсистемы"
      subtitle="Разделы продукта, которые можно отключить. Настройка общая для всего инстанса и применяется при старте сервера."
      width={MODAL_W.form}
      onClose={onClose}
    >
      {/* Подсказка про appsettings — сразу под подзаголовком, чтобы админ без
          открытия баннера знал, ГДЕ задаётся значение. Не дублируем строку
          макета: подсказка — самостоятельная, не часть расхождения. */}
      <div style={{
        fontSize: FS.xs,
        color: C.textMuted,
        lineHeight: 1.45,
        marginTop: `-${SP.xs}px`,
        marginBottom: SP.sm,
      }}>
        Значение задаётся в appsettings ключом <span style={{ fontFamily: FONT.mono }}>Subsystems:{`{Key}`}:Enabled</span>.
        Чтобы изменить состояние, отредактируйте конфиг и перезапустите сервер.
      </div>

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
          Пока нет подсистем с рубильником
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
              />
            ))}
          </div>
        </>
      )}
    </Modal>
  );
}

// Баннер «Перезапустите сервер»: появляется, только если есть хотя бы
// одно расхождение. Тело перечисляет расхождения через запятую в порядке
// списка. Сноска объясняет отсутствие кнопки — рестарт обрывает чужие
// ходы, кнопка рядом с тумблером превратила бы настройку в разрыв сессий
// одним кликом.
function RestartBanner({ diverged }: { diverged: Subsystem[] }) {
  // Что СЕЙЧАС работает (active) — это и есть то, что пользователь
  // продолжает видеть после клика, до перезапуска. Именно это должно
  // стоять в теле баннера («сейчас всё работает по-прежнему: …»).
  const parts = diverged.map(s => {
    const currently = s.active ? 'включены' : 'выключены';
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

// Строка подсистемы — анатомия по макету. Правая колонка (плашка статуса)
// перестраивается под мобильную раскладку: на мобиле уходит под описание
// отдельной строкой.
function SubsystemRow({
  sub, isFirst, isMobile,
}: {
  sub: Subsystem;
  isFirst: boolean;
  isMobile: boolean;
}) {
  const status = computeStatus(sub);
  const text = STATUS_TEXT[status];
  const rowStyle: CSSProperties = {
    display: 'flex',
    flexDirection: isMobile ? 'column' : 'row',
    alignItems: isMobile ? 'stretch' : 'flex-start',
    gap: SP.md,
    padding: `${SP.md}px 0`,
    borderTop: isFirst ? 'none' : `1px solid ${C.borderLight}`,
  };

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
        {sub.description && (
          <div style={{
            fontSize: FS.sm,
            lineHeight: 1.45,
            color: C.textSecondary,
            marginTop: 3,
          }}>
            {sub.description}
          </div>
        )}
        {isMobile && (
          <div style={{ marginTop: SP.sm }}>
            <Badge size="xs" dot tone={text.tone}>{text.label}</Badge>
          </div>
        )}
      </div>
      {!isMobile && (
        <div style={{
          flexShrink: 0,
          paddingTop: SP.xxs,
        }}>
          <Badge size="xs" dot tone={text.tone}>{text.label}</Badge>
        </div>
      )}
    </div>
  );
}
