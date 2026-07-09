import { useEffect, useState } from 'react';
import { Modal, Toggle } from './ui';
import { api } from '../lib/api';
import { setAllFlags, setFlagLocal, getAllFlags } from '../lib/featureFlags';
import { disablePush, enablePush, isPushEnabled, isPushSupported } from '../lib/push';
import { C, MODAL_W } from '../lib/design';
import type { FeatureFlagDefinition } from '../types';

interface Props {
  onClose: () => void;
}

// Цвет бейджа по стадии зрелости флага
const STAGE_COLOR: Record<FeatureFlagDefinition['stage'], string> = {
  dev: C.textMuted,
  beta: C.accent,
  stable: C.success,
};

export function FeatureFlagsModal({ onClose }: Props) {
  const [defs, setDefs] = useState<FeatureFlagDefinition[]>([]);
  const [values, setValues] = useState<Record<string, boolean>>(() => getAllFlags());
  const [loadState, setLoadState] = useState<'loading' | 'ok' | 'error'>('loading');
  // Ключи, по которым сейчас летит PUT — чтобы заблокировать тумблер до ответа
  const [busy, setBusy] = useState<Set<string>>(new Set());

  useEffect(() => {
    let cancelled = false;
    api.featureFlags.get()
      .then(({ definitions, values }) => {
        if (cancelled) return;
        setDefs(definitions);
        setValues(values);
        setAllFlags(values); // освежаем глобальный стор актуальными значениями
        setLoadState('ok');
      })
      .catch(() => { if (!cancelled) setLoadState('error'); });
    return () => { cancelled = true; };
  }, []);

  // Push-подписка текущего устройства (настройка per-device, не per-user)
  const [pushOn, setPushOn] = useState(false);
  const [pushBusy, setPushBusy] = useState(false);
  const [pushError, setPushError] = useState<string | null>(null);

  useEffect(() => {
    if (isPushSupported()) void isPushEnabled().then(setPushOn);
  }, []);

  const togglePush = async (next: boolean) => {
    setPushBusy(true);
    setPushError(null);
    try {
      if (next) await enablePush();
      else await disablePush();
      setPushOn(next);
    } catch (e) {
      setPushError(e instanceof Error ? e.message : 'Не удалось изменить push-подписку');
      setPushOn(await isPushEnabled().catch(() => false));
    } finally {
      setPushBusy(false);
    }
  };

  const toggle = async (key: string, next: boolean) => {
    // Запоминаем прежнее значение для точного отката (не полагаемся на !next)
    const prev = values[key] ?? defs.find(d => d.key === key)?.default ?? false;
    // Оптимистично: и локально в модалке, и в глобальном сторе (фича реагирует сразу)
    setValues(v => ({ ...v, [key]: next }));
    setFlagLocal(key, next);
    setBusy(s => new Set(s).add(key));
    try {
      const { values: fresh } = await api.featureFlags.set(key, next);
      setValues(fresh);
      setAllFlags(fresh);
    } catch {
      // Откат при ошибке — на запомненное прежнее значение
      setValues(v => ({ ...v, [key]: prev }));
      setFlagLocal(key, prev);
    } finally {
      setBusy(s => { const n = new Set(s); n.delete(key); return n; });
    }
  };

  return (
    <Modal
      title="Экспериментальные функции"
      subtitle="Включай новые фичи у себя. Настройка действует только для твоего аккаунта."
      width={MODAL_W.form}
      onClose={onClose}
    >
      {loadState === 'loading' && (
        <div style={{ color: C.textMuted, fontSize: 14, padding: '8px 0' }}>Загрузка…</div>
      )}
      {loadState === 'error' && (
        <div style={{ color: C.danger, fontSize: 14, padding: '8px 0' }}>Не удалось загрузить список</div>
      )}
      {loadState === 'ok' && defs.length === 0 && (
        <div style={{ color: C.textMuted, fontSize: 14, padding: '8px 0' }}>
          Пока нет экспериментальных функций
        </div>
      )}
      {loadState === 'ok' && defs.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          {defs.map((d, i) => (
            <div
              key={d.key}
              style={{
                display: 'flex', alignItems: 'flex-start', gap: 14, padding: '12px 0',
                borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
              }}
            >
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 3 }}>
                  <span style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>{d.title}</span>
                  <span style={{
                    fontSize: 10, fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase',
                    color: STAGE_COLOR[d.stage], border: `1px solid ${STAGE_COLOR[d.stage]}`,
                    borderRadius: 4, padding: '1px 5px',
                  }}>
                    {d.stage}
                  </span>
                </div>
                <div style={{ fontSize: 12.5, lineHeight: 1.45, color: C.textSecondary }}>{d.description}</div>
              </div>
              <div style={{ flexShrink: 0, paddingTop: 2 }}>
                <Toggle
                  checked={values[d.key] ?? d.default}
                  onChange={v => toggle(d.key, v)}
                  disabled={busy.has(d.key)}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Push — настройка этого устройства (браузера), не аккаунта */}
      {isPushSupported() && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: 14, padding: '12px 0 2px',
          borderTop: `1px solid ${C.border}`, marginTop: 10,
        }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 14, fontWeight: 600, color: C.textHeading, marginBottom: 3 }}>
              Push-уведомления на этом устройстве
            </div>
            <div style={{ fontSize: 12.5, lineHeight: 1.45, color: C.textSecondary }}>
              Напоминания о задачах и события ассистента приходят, даже когда вкладка закрыта.
              Настройка действует только для этого браузера.
            </div>
            {pushError && (
              <div style={{ fontSize: 12, color: C.danger, marginTop: 4 }}>{pushError}</div>
            )}
          </div>
          <div style={{ flexShrink: 0, paddingTop: 2 }}>
            <Toggle checked={pushOn} onChange={togglePush} disabled={pushBusy} />
          </div>
        </div>
      )}
    </Modal>
  );
}
