import { useEffect, useState } from 'react';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, SP } from '../../lib/design';
import { Badge, Checkbox, Modal, ModalActions } from '../../components/ui';
import type { HandsProvidersView } from '../../types';

// Настройка владельца «Руки на устройствах» (ADR-016 §7): каким провайдерам можно доверить
// руки локальных проектов. Список per-owner, а не админский «Поставщики моделей»: провайдер
// видит снимки и текст окон, решать, кому это отдать, может только владелец данных.
// По умолчанию не отмечен никто — разрешение явное.
export function HandsProvidersModal({ onClose, onSaved }: {
  onClose: () => void;
  onSaved?: (view: HandsProvidersView) => void;
}) {
  const [view, setView] = useState<HandsProvidersView | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => {
    let alive = true;
    api.meHandsProviders.get()
      .then(v => {
        if (!alive) return;
        setView(v);
        setPicked(new Set(v.providers.map(k => k.toLowerCase())));
      })
      .catch((e: unknown) => { if (alive) setErr(e instanceof Error && e.message ? e.message : 'Не удалось загрузить список'); });
    return () => { alive = false; };
  }, []);

  const toggle = (key: string, on: boolean) => {
    setPicked(prev => {
      const next = new Set(prev);
      if (on) next.add(key.toLowerCase()); else next.delete(key.toLowerCase());
      return next;
    });
  };

  const save = async () => {
    if (!view) return;
    setBusy(true);
    setErr('');
    try {
      const saved = await api.meHandsProviders.save(
        view.available.map(o => o.key).filter(k => picked.has(k.toLowerCase())));
      onSaved?.(saved);
      onClose();
    } catch (e: unknown) {
      setErr(e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title="Руки на устройствах"
      width={MODAL_W.form}
      onClose={onClose}
      footer={<ModalActions confirmLabel="Сохранить" onConfirm={() => void save()} loading={busy}
        confirmDisabled={!view} onCancel={onClose} />}
    >
      <div style={{ fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textPrimary }}>
          Каким провайдерам можно доверить руки
        </div>
        <div data-hands-providers-lead style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
          Провайдер видит снимки окон, в которых работают руки, и текст из них — всё, что в этих
          окнах открыто. Разрешайте только тем, кому доверяете такие данные. Никто не отмечен —
          руки выключены во всех проектах.
        </div>

        {err && <div style={{ fontSize: FS.xs, color: C.dangerText }}>{err}</div>}

        {view === null && !err && (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>Загружаем провайдеров…</div>
        )}

        {view && (
          <div role="list" style={{ display: 'flex', flexDirection: 'column' }}>
            {view.available.map(o => {
              const checked = picked.has(o.key.toLowerCase());
              return (
                <div key={o.key} role="listitem" data-hands-provider={o.key} style={{
                  display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0,
                  borderBottom: `1px solid ${C.borderLight}`,
                }}>
                  <Checkbox checked={checked} onChange={v => toggle(o.key, v)} disabled={busy}
                    ariaLabel={`Доверить руки провайдеру ${o.displayName}`} />
                  <span style={{ fontSize: FS.sm, color: C.textPrimary, minWidth: 0, flex: 1 }}>
                    {o.displayName}
                  </span>
                  <Badge size="xs" tone={o.supportsImages ? 'neutral' : 'warning'}>
                    {o.supportsImages ? 'видит снимки окон' : 'видит только текст окон'}
                  </Badge>
                </div>
              );
            })}
          </div>
        )}

        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
          Если основной провайдер чата недоступен, ход с руками перейдёт только к отмеченным. Нет
          отмеченных в цепочке — ход завершится ошибкой, а не уйдёт другому провайдеру.
        </div>
      </div>
    </Modal>
  );
}
