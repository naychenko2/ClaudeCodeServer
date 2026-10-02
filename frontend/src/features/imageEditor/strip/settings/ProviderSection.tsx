// Секции «Поставщик» и «Модель»

import type { ReactNode } from 'react';
import { C, SP } from 'aihome_shell/kit';
import { AUTO_MODEL, type ImageEditCatalog } from '../../api';
import { effectiveProvider, modelBlockReason, providerHint, unavailableMark } from '../../format';
import { Label, Opt, type Launch } from './primitives';

// Подсказка с пометкой лежащего поставщика отдельным span цвета предупреждения
function joinHint(hint: string, warn: string, warnFirst: boolean): ReactNode {
  if (!warn) return hint;
  const w = <span key="w" style={{ color: C.warningText }}>{warn}</span>;
  if (!hint) return w;
  return warnFirst ? <>{w} · {hint}</> : <>{hint} · {w}</>;
}

// Лежащий поставщик выбирается как обычный — пометка лишь предупреждает заранее
export function ProviderOpts({ catalog, choice, onPick }: {
  catalog: ImageEditCatalog; choice: string; onPick: (provider: string | null) => void;
}) {
  const admin = catalog.providers.find(p => p.key === catalog.default.provider);
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
      {admin && (
        <Opt on={choice === 'settings'} name="Как в настройках"
          hint={joinHint(`сейчас ${admin.label}`, unavailableMark(admin), false)}
          onClick={() => onPick(null)} />
      )}
      {catalog.providers.map(p => (
        <Opt key={p.key} on={choice === p.key} name={p.label}
          hint={joinHint(providerHint(p), unavailableMark(p), true)}
          onClick={() => onPick(p.key)} />
      ))}
    </div>
  );
}

export function ProviderSection({ L, catalog }: { L: Launch; catalog: ImageEditCatalog }) {
  return (
    <>
      <Label>Поставщик</Label>
      <ProviderOpts catalog={catalog} choice={L.settings.provider ?? 'settings'} onPick={provider => L.setSettings({ provider, model: null })} />
    </>
  );
}

// Модели выбранного поставщика; недоступные под текущую задачу — с причиной
export function ModelSection({ L, catalog }: { L: Launch; catalog: ImageEditCatalog }) {
  const pv = effectiveProvider(catalog, L.settings.provider ?? 'settings');
  if (!pv) return null;
  return (
    <>
      <Label>Модель</Label>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {pv.models.map(m => {
          // Входы как у блокировки запуска: «По тексту» картинку не берёт, маска — только инпейнту
          const why = modelBlockReason(m, L.op !== 'generate' && L.hasImage, L.op === 'inpaint' && L.hasMask, L.op);
          return (
            <Opt key={m.id} on={m.id === L.model?.id} name={m.label} disabled={!!why} title={why || undefined}
              hint={why || (m.id === AUTO_MODEL ? 'подберём под задачу' : undefined)}
              onClick={() => L.setSettings({ model: m.id })} />
          );
        })}
      </div>
    </>
  );
}
