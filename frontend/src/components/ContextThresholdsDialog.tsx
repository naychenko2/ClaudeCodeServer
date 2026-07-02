import { useState } from 'react';
import { C, FONT, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Field, TextField } from './ui';
import { RATE_COLORS } from '../lib/rateLimit';
import { DEFAULT_CTX_WARN, DEFAULT_CTX_DANGER } from '../lib/context';
import { useCtxThresholds, saveCtxThresholds, resetCtxThresholds } from '../lib/contextPrefs';

// Настройка per-user порогов подсветки индикатора заполнения контекста.
// Дефолты 65/85; сохраняется на сервере (users.json), действует на все сессии юзера.
export function ContextThresholdsDialog({ onClose }: { onClose: () => void }) {
  const current = useCtxThresholds();
  const [warn, setWarn] = useState(String(current.warnPct));
  const [danger, setDanger] = useState(String(current.dangerPct));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const warnNum = parseInt(warn, 10);
  const dangerNum = parseInt(danger, 10);

  const validate = (): string | null => {
    if (!Number.isInteger(warnNum) || !Number.isInteger(dangerNum)) return 'Введите целые числа';
    if (warnNum < 1 || warnNum > 99 || dangerNum < 1 || dangerNum > 99) return 'Пороги должны быть в диапазоне 1–99';
    if (warnNum >= dangerNum) return 'Порог предупреждения должен быть меньше порога тревоги';
    return null;
  };

  const handleSave = async () => {
    const err = validate();
    if (err) { setError(err); return; }
    setLoading(true);
    setError(null);
    try {
      await saveCtxThresholds(warnNum, dangerNum);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения');
    } finally {
      setLoading(false);
    }
  };

  const handleReset = async () => {
    setLoading(true);
    setError(null);
    try {
      await resetCtxThresholds();
      setWarn(String(DEFAULT_CTX_WARN));
      setDanger(String(DEFAULT_CTX_DANGER));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сброса');
    } finally {
      setLoading(false);
    }
  };

  // Превью зон подсветки по текущим (возможно ещё не сохранённым) значениям
  const previewWarn = Number.isInteger(warnNum) ? Math.min(99, Math.max(1, warnNum)) : DEFAULT_CTX_WARN;
  const previewDanger = Number.isInteger(dangerNum) ? Math.min(99, Math.max(previewWarn, dangerNum)) : DEFAULT_CTX_DANGER;

  return (
    <Modal
      title="Пороги индикатора контекста"
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
        Когда заполнение контекста достигает порога, индикатор в шапке чата меняет цвет:
        предупреждение — янтарный, тревога — красный. Настройка личная и действует во всех чатах.
      </div>

      <Field label="Предупреждение, %" hint="Индикатор становится янтарным.">
        <TextField value={warn} onChange={setWarn} onEnter={handleSave} />
      </Field>

      <Field label="Тревога, %" hint="Индикатор становится красным.">
        <TextField value={danger} onChange={setDanger} onEnter={handleSave} />
      </Field>

      {/* Превью зон: нейтральная → янтарная → красная */}
      <div style={{ display: 'flex', height: 8, borderRadius: 4, overflow: 'hidden', border: `1px solid ${C.border}` }}>
        <div style={{ width: `${previewWarn}%`, background: RATE_COLORS.normal.fill, opacity: 0.45 }} />
        <div style={{ width: `${previewDanger - previewWarn}%`, background: RATE_COLORS.warn.fill }} />
        <div style={{ flex: 1, background: RATE_COLORS.danger.fill }} />
      </div>

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
          Сбросить к дефолту ({DEFAULT_CTX_WARN}% / {DEFAULT_CTX_DANGER}%)
        </button>
      </div>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
