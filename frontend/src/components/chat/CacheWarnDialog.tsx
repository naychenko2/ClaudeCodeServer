// Диалог личных порогов «когда предупреждать об остывании кэша» (фича «Индикатор
// prompt cache и перенос в новый чат», шаг F-3). Открывается из секции «Кэш промпта»
// поповера кольцевой пилюли. Два поля — запас минут до предупреждения для часового
// и 5-минутного срока. Шкала — бэковая (1..59 и 1..4): за неё бэк отвечает 400, поэтому
// диалог проверяет сам, а текст 400 показывает как ошибку ввода, не как сбой.
// Сброс — пустое тело: индикатор возвращается к дефолтам (5 мин и 1 мин).
import { useEffect, useState } from 'react';
import { C, FONT, MODAL_W } from '../../lib/design';
import { Modal, ModalActions, Field, TextField } from '../ui';
import { EXPIRING_THRESHOLD_MS } from '../../lib/promptCache';
import {
  getCacheWarnThresholds, saveCacheWarnThresholds, ensureCacheWarnLoaded,
  WARN1H_MAX, WARN5M_MAX,
} from '../../lib/cacheWarnPrefs';

// Дефолты показаны в минутах — те же секунды, что у индикатора (EXPIRING_THRESHOLD_MS)
const DEF_1H = EXPIRING_THRESHOLD_MS['1h'] / 60_000;
const DEF_5M = EXPIRING_THRESHOLD_MS['5m'] / 60_000;

// 400 бэка — текст об ошибке ввода (status прикреплён в offline.request), всё прочее — общий сбой
const inputError = (e: unknown, fallback: string): string =>
  e instanceof Error && (e as Error & { status?: number }).status === 400 ? e.message : fallback;

export function CacheWarnDialog({ onClose }: { onClose: () => void }) {
  // Репаранс на случай, пока /me не дошёл (обычно сидировано ещё монтированием пилюли)
  useEffect(() => { void ensureCacheWarnLoaded(); }, []);
  // Текущие значения — из стора, сидированного ответом /auth/me; не настроено — дефолты
  const current = getCacheWarnThresholds();
  const [warn1h, setWarn1h] = useState(String(current?.warn1hMin ?? DEF_1H));
  const [warn5m, setWarn5m] = useState(String(current?.warn5mMin ?? DEF_5M));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const n1h = parseInt(warn1h, 10);
  const n5m = parseInt(warn5m, 10);

  const validate = (): string | null => {
    if (!Number.isInteger(n1h) || !Number.isInteger(n5m)) return 'Введите целые числа в минутах';
    if (n1h < 1 || n1h > WARN1H_MAX || n5m < 1 || n5m > WARN5M_MAX) {
      return `Порог часового кэша — 1–${WARN1H_MAX} мин, 5-минутного — 1–${WARN5M_MAX} мин`;
    }
    return null;
  };

  const handleSave = async () => {
    const err = validate();
    if (err) { setError(err); return; }
    setLoading(true);
    setError(null);
    try {
      await saveCacheWarnThresholds({ warn1hMin: n1h, warn5mMin: n5m });
      onClose();
    } catch (e) {
      // 400 — бэк не принял значения: это текст об ошибке ввода, а не сетевой сбой
      setError(inputError(e, 'Не удалось сохранить'));
    } finally {
      setLoading(false);
    }
  };

  const handleReset = async () => {
    setLoading(true);
    setError(null);
    try {
      await saveCacheWarnThresholds(null);
      setWarn1h(String(DEF_1H));
      setWarn5m(String(DEF_5M));
    } catch (e) {
      setError(inputError(e, 'Не удалось сбросить'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Когда предупреждать об остывании кэша"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={loading ? 'Сохраняем…' : 'Сохранить'}
          onConfirm={handleSave}
          loading={loading}
          onCancel={onClose}
        />
      }
    >
      <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
        Циферблат кэша становится янтарным, когда остаток меньше порога. Ответьте до него —
        следующий ход продлит кэш, не переписывая его. Настройка личная и действует во всех ваших чатах.
      </div>

      <Field label="Часовой кэш, мин" hint={`1–${WARN1H_MAX} мин. По умолчанию ${DEF_1H} мин.`}>
        <TextField value={warn1h} onChange={setWarn1h} onEnter={handleSave} />
      </Field>

      <Field label="5-минутный кэш, мин" hint={`1–${WARN5M_MAX} мин. По умолчанию ${DEF_5M} мин.`}>
        <TextField value={warn5m} onChange={setWarn5m} onEnter={handleSave} />
      </Field>

      <div>
        <button
          type="button"
          onClick={handleReset}
          disabled={loading}
          style={{
            border: 'none', background: 'none', padding: 0, cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textDecoration: 'underline',
          }}
        >
          Сбросить к дефолту ({DEF_1H} мин / {DEF_5M} мин)
        </button>
      </div>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
