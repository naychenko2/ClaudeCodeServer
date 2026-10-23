// Диалог личных порогов «когда предупреждать об остывании кэша» (фича «Индикатор
// prompt cache и перенос в новый чат», шаг F-3). Открывается из секции «Кэш промпта»
// поповера кольцевой пилюли. Два поля — запас минут до предупреждения для часового
// и 5-минутного срока. Шкала — бэковая (1..59 и 1..4): за неё бэк отвечает 400, поэтому
// диалог проверяет сам, а текст 400 показывает как ошибку ввода, не как сбой.
// Сброс — пустое тело: индикатор возвращается к дефолтам (5 мин и 1 мин).
import { useEffect, useRef, useState } from 'react';
import { C, FONT, MODAL_W } from '../../lib/design';
import { Modal, ModalActions, Field, TextField } from '../ui';
import { EXPIRING_THRESHOLD_MS } from '../../lib/promptCache';
import {
  saveCacheWarnThresholds, useCacheWarnThresholds,
  WARN1H_MAX, WARN5M_MAX,
} from '../../lib/cacheWarnPrefs';

// Дефолты показаны в минутах — те же секунды, что у индикатора (EXPIRING_THRESHOLD_MS)
const DEF_1H = EXPIRING_THRESHOLD_MS['1h'] / 60_000;
const DEF_5M = EXPIRING_THRESHOLD_MS['5m'] / 60_000;

// 400 бэка — текст об ошибке ввода (status прикреплён в offline.request), всё прочее — общий сбой
const inputError = (e: unknown, fallback: string): string =>
  e instanceof Error && (e as Error & { status?: number }).status === 400 ? e.message : fallback;

export function CacheWarnDialog({ onClose }: { onClose: () => void }) {
  // Подписка на стор (M2): если /auth/me дошёл после открытия диалога — поля
  // обновляются; как только пользователь правил их руками, пересев прекращается,
  // чтобы поздний засев не затёр его ввод.
  const server = useCacheWarnThresholds();
  const [warn1h, setWarn1h] = useState(String(server?.warn1hMin ?? DEF_1H));
  const [warn5m, setWarn5m] = useState(String(server?.warn5mMin ?? DEF_5M));
  const edited = useRef(false); // «правил сам» — с первой ручной правки поля не пересевать
  useEffect(() => {
    if (edited.current) return;
    setWarn1h(String(server?.warn1hMin ?? DEF_1H));
    setWarn5m(String(server?.warn5mMin ?? DEF_5M));
  }, [server]);
  const setManual1h = (v: string) => { edited.current = true; setWarn1h(v); };
  const setManual5m = (v: string) => { edited.current = true; setWarn5m(v); };
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Строгий разбор (T1): только цифры. parseInt('5abc') — 5, и такая строка прошла бы
  // локальную проверку, а бэк ответил бы 400; локально «5abc» невалидна.
  const parseMin = (s: string): number | null => /^\d+$/.test(s.trim()) ? Number(s) : null;

  const validate = (): string | null => {
    const n1h = parseMin(warn1h);
    const n5m = parseMin(warn5m);
    if (n1h === null || n5m === null) return 'Введите целые числа в минутах';
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
      await saveCacheWarnThresholds({ warn1hMin: parseMin(warn1h)!, warn5mMin: parseMin(warn5m)! });
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
        <TextField value={warn1h} onChange={setManual1h} onEnter={handleSave} />
      </Field>

      <Field label="5-минутный кэш, мин" hint={`1–${WARN5M_MAX} мин. По умолчанию ${DEF_5M} мин.`}>
        <TextField value={warn5m} onChange={setManual5m} onEnter={handleSave} />
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
