import { useMemo, useState } from 'react';
import { Plug } from 'lucide-react';
import { C } from '../../lib/design';
import { Button, EmptyState, PillSwitch } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Project } from '../../types';
import type { PluginInfo, PluginPanelView } from '../../lib/api';
import { PluginFrame } from './PluginFrame';
import { panelDisplayName, pluginIconFor } from './panelNaming';

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
//
// Заголовок — ОДИН: при нескольких панелях это активный сегмент переключателя
// (PluginFrame тогда showTitle=false, имя не дублируется); при одной панели —
// строка-имя в toolbar самого PluginFrame.

type PanelTarget = { plugin: PluginInfo; panel: PluginPanelView };
// Ключ цели: id плагина + id панели (id панели уникален в пределах пакета).
// Стабильен при обновлении плагина: выбранная в переключателе панель не
// «срывается» на первую, когда у плагина сменилась версия/права.
const targetKey = (t: PanelTarget) => `${t.plugin.id}::${t.panel.id}`;
// Ключ React-компонента: цель + версия + набор прав (S7). Обновился плагин или
// сменились выданные права — PluginFrame перемонтируется целиком (включая
// frameKey внутри), и панель показывает свежую версию/права.
const frameKey = (t: PanelTarget) =>
  `${targetKey(t)}#${t.plugin.version}#${[...t.plugin.granted].sort().join(',')}`;

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
      <EmptyState
        compact
        icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Здесь будут панели плагинов"
        subtitle="У установленных плагинов панелей нет."
        action={onOpenPlugins ? <Button variant="dashed" size="sm" onClick={onOpenPlugins}>Открыть «Плагины»</Button> : undefined}
      />
    );
  }

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      {targets.length > 1 && (
        // Toolbar хоста (паддинги как в PanelShell): переключатель, какую панель показать.
        // Иконки у сегментов: в compact (узкий экран) неактивные сегменты без подписи —
        // иконка делает их различимыми и нажимаемыми, а не пустыми кнопками ~22px.
        <div style={{
          flexShrink: 0, padding: '8px 10px 9px',
          borderBottom: `1px solid ${C.border}`, background: C.bgPanel,
        }}>
          <PillSwitch
            value={targetKey(active)}
            options={targets.map(t => {
              const Icon = pluginIconFor(t.plugin.id);
              return {
                value: targetKey(t),
                label: panelDisplayName(t.plugin, t.panel),
                title: t.plugin.id,
                icon: <Icon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
              };
            })}
            onChange={(v: string) => setSelectedKey(v)}
            isMobile
            autoCompact
          />
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <PluginFrame
          key={frameKey(active)}
          project={project}
          plugin={active.plugin}
          panel={active.panel}
          onOpenPlugins={onOpenPlugins}
          onClose={onClose}
          showTitle={targets.length <= 1}
        />
      </div>
    </div>
  );
}
