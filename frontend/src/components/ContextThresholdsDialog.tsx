import { useState } from 'react';
import { C, FONT, FS, MODAL_W, SP } from '../lib/design';
import { Modal, ModalActions, Field, TextField } from './ui';
import { RATE_COLORS } from '../lib/rateLimit';
import { DEFAULT_CTX_WARN, DEFAULT_CTX_DANGER } from '../lib/context';
import {
  useCtxThresholds, saveCtxThresholds, resetCtxThresholds,
  useCacheThresholds, saveCacheThresholds, resetCacheThresholds,
} from '../lib/contextPrefs';
import { CACHE_WARN_MIN_SEC, cacheWarnMaxSec, expiringThresholdMs } from '../lib/promptCache';

const HOUR_MS = 60 * 60_000;
const FIVE_MIN_MS = 5 * 60_000;
// Дефолты порога кэша — из самой модели (без префа), чтобы не разъехаться с promptCache.ts
const DEFAULT_CACHE_1H_MIN = Math.round(expiringThresholdMs(HOUR_MS) / 60_000);
const DEFAULT_CACHE_5M_SEC = Math.round(expiringThresholdMs(FIVE_MIN_MS) / 1000);
// Часовой порог вводится в минутах, 5-минутный — в секундах; границы — как у бэкенда
const CACHE_1H_MIN_MIN = Math.ceil(CACHE_WARN_MIN_SEC / 60);
const CACHE_1H_MAX_MIN = Math.floor(cacheWarnMaxSec(HOUR_MS) / 60);
const CACHE_5M_MAX_SEC = cacheWarnMaxSec(FIVE_MIN_MS);

// Настройка per-user порогов подсветки индикатора заполнения контекста и порога
// «кэш промпта скоро остынет». Дефолты контекста 65/85; сохраняется на сервере
// (users.json), действует на все сессии юзера.
export function ContextThresholdsDialog({ onClose }: { onClose: () => void }) {
  const current = useCtxThresholds();
  const currentCache = useCacheThresholds();
  const [warn, setWarn] = useState(String(current.warnPct));
  const [danger, setDanger] = useState(String(current.dangerPct));
  const [cache1h, setCache1h] = useState(String(currentCache ? Math.round(currentCache.warn1hSec / 60) : DEFAULT_CACHE_1H_MIN));
  const [cache5m, setCache5m] = useState(String(currentCache?.warn5mSec ?? DEFAULT_CACHE_5M_SEC));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const warnNum = parseInt(warn, 10);
  const dangerNum = parseInt(danger, 10);
  const cache1hNum = parseInt(cache1h, 10);
  const cache5mNum = parseInt(cache5m, 10);

  const validate = (): string | null => {
    if (!Number.isInteger(warnNum) || !Number.isInteger(dangerNum)) return 'Введите целые числа';
    if (warnNum < 1 || warnNum > 99 || dangerNum < 1 || dangerNum > 99) return 'Пороги должны быть в диапазоне 1–99';
    if (warnNum >= dangerNum) return 'Порог предупреждения должен быть меньше порога тревоги';
    if (!Number.isInteger(cache1hNum) || !Number.isInteger(cache5mNum)) return 'Порог кэша — целое число';
    if (cache1hNum < CACHE_1H_MIN_MIN || cache1hNum > CACHE_1H_MAX_MIN)
      return `Порог часового кэша — ${CACHE_1H_MIN_MIN}–${CACHE_1H_MAX_MIN} мин`;
    if (cache5mNum < CACHE_WARN_MIN_SEC || cache5mNum > CACHE_5M_MAX_SEC)
      return `Порог 5-минутного кэша — ${CACHE_WARN_MIN_SEC}–${CACHE_5M_MAX_SEC} с`;
    return null;
  };

  const handleSave = async () => {
    const err = validate();
    if (err) { setError(err); return; }
    setLoading(true);
    setError(null);
    try {
      await saveCtxThresholds(warnNum, dangerNum);
      // Кэш пишем, только если значения отличаются от действующих: иначе нетронутая
      // секция превратила бы дефолт в личную настройку
      const cur1hMin = currentCache ? Math.round(currentCache.warn1hSec / 60) : DEFAULT_CACHE_1H_MIN;
      const cur5mSec = currentCache?.warn5mSec ?? DEFAULT_CACHE_5M_SEC;
      if (cache1hNum !== cur1hMin || cache5mNum !== cur5mSec)
        await saveCacheThresholds(cache1hNum * 60, cache5mNum);
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

  const handleCacheReset = async () => {
    setLoading(true);
    setError(null);
    try {
      await resetCacheThresholds();
      setCache1h(String(DEFAULT_CACHE_1H_MIN));
      setCache5m(String(DEFAULT_CACHE_5M_SEC));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сброса');
    } finally {
      setLoading(false);
    }
  };

  // Превью зон подсветки по текущим (возможно ещё не сохранённым) значениям
  const previewWarn = Number.isInteger(warnNum) ? Math.min(99, Math.max(1, warnNum)) : DEFAULT_CTX_WARN;
  const previewDanger = Number.isInteger(dangerNum) ? Math.min(99, Math.max(previewWarn, dangerNum)) : DEFAULT_CTX_DANGER;

  const textStyle: React.CSSProperties = { fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 };
  const linkStyle: React.CSSProperties = {
    border: 'none', background: 'none', padding: 0, cursor: 'pointer',
    fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted, textDecoration: 'underline',
  };

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
      <div style={textStyle}>
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
        <button type="button" onClick={handleReset} disabled={loading} style={linkStyle}>
          Сбросить к дефолту ({DEFAULT_CTX_WARN}% / {DEFAULT_CTX_DANGER}%)
        </button>
      </div>

      {/* Секция кэша промпта: данные есть только у родного Claude */}
      <div style={{ borderTop: `1px solid ${C.border}`, paddingTop: SP.md, fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, color: C.textPrimary }}>
        Кэш
      </div>
      <div style={textStyle}>
        За сколько до остывания кэша промпта часики в шапке становятся янтарными и пульсируют,
        а чат напоминает об этом. Если порог больше срока, оставшегося после хода, чат
        «остывает» сразу — это нормально.
      </div>

      <Field label="Часовой кэш, мин" hint={`От ${CACHE_1H_MIN_MIN} до ${CACHE_1H_MAX_MIN} минут до остывания.`}>
        <TextField value={cache1h} onChange={setCache1h} onEnter={handleSave} />
      </Field>

      <Field label="5-минутный кэш, с" hint={`От ${CACHE_WARN_MIN_SEC} до ${CACHE_5M_MAX_SEC} секунд до остывания.`}>
        <TextField value={cache5m} onChange={setCache5m} onEnter={handleSave} />
      </Field>

      <div>
        <button type="button" onClick={handleCacheReset} disabled={loading} style={linkStyle}>
          Сбросить к дефолту ({DEFAULT_CACHE_1H_MIN} мин / {DEFAULT_CACHE_5M_SEC} с)
        </button>
      </div>

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}
