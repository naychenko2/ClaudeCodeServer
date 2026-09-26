// «Поставщик ▾ → Модель ▾» (макет image-editor-v1, экран 3). Список поставщиков —
// только доступные сейчас из каталога: ненастроенный сервер не присылает вовсе.
// На телефоне оба выбора живут в одной шторке «Чем рисовать».

import { useState, type MouseEvent, type ReactNode } from 'react';
import { AlertTriangle, Check, ChevronDown, Coins } from 'lucide-react';
import { Button, Menu, MenuItem, Modal, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import type { ImageEditCatalog, ImageEditModel, ImageEditProvider } from './api';
import { effectiveProvider, modelBlockReason, money, providerHint, type ProviderChoice } from './format';

interface Props {
  catalog: ImageEditCatalog;
  provider: ProviderChoice;
  model: string;
  onProvider: (p: ProviderChoice) => void;
  onModel: (m: string) => void;
  hasImage: boolean;
  hasMask: boolean;
  priceLabel: string;
  mobile: boolean;
}

const tick = (on: boolean) => on
  ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.accent} />
  : <span />;

function Row({ name, hint, aside }: { name: string; hint?: string; aside?: ReactNode }) {
  return (
    <span style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, minWidth: 0 }}>
      <span style={{ flexShrink: 0 }}>{name}</span>
      {hint && (
        <span style={{ color: C.textMuted, fontSize: FS.sm, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{hint}</span>
      )}
      {aside && <span style={{ marginLeft: 'auto', color: C.textMuted, fontSize: FS.sm, flexShrink: 0 }}>{aside}</span>}
    </span>
  );
}

function ProviderItems({ catalog, value, onPick }: { catalog: ImageEditCatalog; value: ProviderChoice; onPick: (p: ProviderChoice) => void }) {
  const admin = catalog.providers.find(p => p.key === catalog.default.provider);
  return (
    <>
      {admin && (
        <MenuItem icon={tick(value === 'settings')} onClick={() => onPick('settings')}
          label={<Row name="Как в настройках" hint={`сейчас ${admin.label} — выбрал администратор`} />} />
      )}
      {catalog.providers.map(p => (
        <MenuItem key={p.key} icon={tick(value === p.key)} onClick={() => onPick(p.key)}
          label={<Row name={p.label} hint={providerHint(p)} />} />
      ))}
    </>
  );
}

function ModelItems({ provider, value, hasImage, hasMask, onPick }: {
  provider: ImageEditProvider; value: string; hasImage: boolean; hasMask: boolean; onPick: (m: string) => void;
}) {
  return (
    <>
      {provider.models.map(m => {
        const why = modelBlockReason(m, hasImage, hasMask);
        const price = m.priceHint ? money(m.priceHint.amount, m.priceHint.unit) : undefined;
        return (
          <MenuItem key={m.id} icon={tick(m.id === value)} disabled={!!why}
            wrapper={why ? { title: why } : undefined}
            onClick={() => onPick(m.id)}
            label={<Row name={m.label} hint={why || (m.id === 'auto' ? 'подберём под задачу' : undefined)} aside={price} />} />
        );
      })}
    </>
  );
}

export function currentModel(provider: ImageEditProvider | null, model: string): ImageEditModel | null {
  return provider?.models.find(m => m.id === model) ?? provider?.models[0] ?? null;
}

export function ProviderModelPicker(p: Props) {
  const [menu, setMenu] = useState<{ kind: 'provider' | 'model'; anchor: DOMRect } | null>(null);
  const [sheet, setSheet] = useState(false);
  const pv = effectiveProvider(p.catalog, p.provider);
  const m = currentModel(pv, p.model);
  if (!pv || !m) return null;
  const blocked = modelBlockReason(m, p.hasImage, p.hasMask);
  const provName = p.provider === 'settings' ? `как в настройках (${pv.label})` : pv.label;

  const open = (kind: 'provider' | 'model') => (e: MouseEvent) =>
    setMenu({ kind, anchor: (e.currentTarget as HTMLElement).getBoundingClientRect() });

  const warn = blocked && (
    <div style={{
      display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.xs, padding: `${SP.xs}px ${SP.sm}px`,
      background: C.warningBg, color: C.warningText, borderRadius: R.md, fontSize: FS.sm,
    }}>
      <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
      <span>{m.label}: {blocked.charAt(0).toLowerCase() + blocked.slice(1)}</span>
    </div>
  );

  const chevron = <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

  if (p.mobile) {
    return (
      <div>
        <SectionLabel>Чем рисовать</SectionLabel>
        <Button variant="secondary" size="sm" fullWidth onClick={() => setSheet(true)}
          style={{ justifyContent: 'space-between' }}>
          <span>{pv.label} · <b>{m.label}</b></span>{chevron}
        </Button>
        {warn}
        {sheet && (
          <Modal title="Чем рисовать" onClose={() => setSheet(false)}
            footer={<Button variant="primary" size="lg" fullWidth onClick={() => setSheet(false)}>Готово</Button>}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
              <div>
                <SectionLabel>Поставщик</SectionLabel>
                <ProviderItems catalog={p.catalog} value={p.provider} onPick={p.onProvider} />
              </div>
              <div>
                <SectionLabel>Модель · {pv.label}</SectionLabel>
                <ModelItems provider={pv} value={m.id} hasImage={p.hasImage} hasMask={p.hasMask} onPick={p.onModel} />
              </div>
              <PriceLine text={p.priceLabel} />
            </div>
          </Modal>
        )}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <Button variant="ghost" size="sm" onClick={open('provider')} style={{ justifyContent: 'flex-start' }}>
        <span>Поставщик: <b>{provName}</b></span>{chevron}
      </Button>
      <Button variant="ghost" size="sm" onClick={open('model')} style={{ justifyContent: 'flex-start' }}>
        <span>Модель: <b>{m.label}</b></span>{chevron}
      </Button>
      {warn}
      {menu && (
        <Menu anchor={menu.anchor} minWidth={300} maxWidth={420} onClose={() => setMenu(null)}>
          {menu.kind === 'provider'
            ? <ProviderItems catalog={p.catalog} value={p.provider}
                onPick={v => { p.onProvider(v); setMenu(null); }} />
            : <ModelItems provider={pv} value={m.id} hasImage={p.hasImage} hasMask={p.hasMask}
                onPick={v => { p.onModel(v); setMenu(null); }} />}
        </Menu>
      )}
    </div>
  );
}

export function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: SP.xs }}>
      {children}
    </div>
  );
}

export function PriceLine({ text }: { text: string }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap' }}>
      <Coins size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      <b style={{ color: C.textPrimary }}>{text}</b>
    </span>
  );
}
