import { useMemo, useState } from 'react';
import { C, FS, SP } from '../../lib/design';
import { PillSwitch } from '../../components/ui';
import type { Project } from '../../types';
import type { PluginInfo, PluginPanelView } from '../../lib/api';
import { PluginFrame } from './PluginFrame';

// === Панели плагинов на МОБИЛЕ (S5) ===
// На телефоне вкладка «Плагины» одна (не вкладка на каждую панель — на узком экране
// это каша), внутри — переключатель (PillSwitch), какую панель плагина показать.
// На десктопе у каждой панели плагина своя карточка в рельсе, переключатель не нужен
// (см. PluginFrame: inShell=true → только тело + контролы в шапке PanelShell).
//
// Панели — у ВКЛЮЧЕННЫХ плагинов, у которых панель ОБЪЯВЛЕНА (contributes.panels):
// выключенный не монтирует панели (бэк — 409), панель может не быть (404) — тогда
// пустое состояние. Плоский список (плагин, панель): у плагина может быть несколько
// панелей, и переключатель выбирает панель, а не только плагин.

type PanelTarget = { plugin: PluginInfo; panel: PluginPanelView };
// Ключ цели: id плагина + id панели (id панели уникален в пределах пакета)
const targetKey = (t: PanelTarget) => `${t.plugin.id}::${t.panel.id}`;
// Короткое имя панели для подписи переключателя: title из манифеста, иначе slug плагина
function panelLabel(t: PanelTarget): string {
  if (t.panel.title) return t.panel.title;
  const slug = t.plugin.id.split('/').pop() ?? t.plugin.id;
  return slug;
}

export function PluginPanelsSwitcher({ project, plugins, onOpenPlugins, onClose }: {
  project: Project;
  // Установленные плагины (список уже загружен в WorkspacePage — не дублируем GET)
  plugins: PluginInfo[];
  onOpenPlugins?: () => void;
  onClose?: () => void;
}) {
  const targets = useMemo<PanelTarget[]>(() => {
    const out: PanelTarget[] = [];
    for (const p of plugins) {
      if (!p.enabled) continue;
      for (const panel of p.contributes?.panels ?? []) out.push({ plugin: p, panel });
    }
    return out;
  }, [plugins]);

  // Выбранная панель (переключатель, когда панелей несколько); null → первая
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const active = targets.find(t => targetKey(t) === selectedKey) ?? targets[0] ?? null;

  if (!active) {
    return (
      <div style={{
        flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center',
        justifyContent: 'center', gap: SP.sm, padding: SP.lg, textAlign: 'center',
      }}>
        <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>Здесь будут панели плагинов</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>У установленных плагинов панелей нет.</div>
        {onOpenPlugins && (
          <button
            onClick={onOpenPlugins}
            style={{
              marginTop: SP.xs, background: 'none', border: `1px dashed ${C.dashed}`, borderRadius: 8,
              padding: '6px 14px', color: C.accent, fontSize: FS.sm, fontWeight: 600, cursor: 'pointer',
            }}
          >
            Открыть «Плагины»
          </button>
        )}
      </div>
    );
  }

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      {targets.length > 1 && (
        <div style={{ flexShrink: 0, padding: '8px 12px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel }}>
          <PillSwitch
            value={targetKey(active)}
            options={targets.map(t => ({ value: targetKey(t), label: panelLabel(t), title: t.plugin.id }))}
            onChange={(v: string) => setSelectedKey(v)}
            isMobile
            autoCompact
          />
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <PluginFrame
          key={targetKey(active)}
          project={project}
          plugin={active.plugin}
          panel={active.panel}
          onOpenPlugins={onOpenPlugins}
          onClose={onClose}
        />
      </div>
    </div>
  );
}
