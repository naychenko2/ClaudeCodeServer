import { useEffect, useState, type ReactNode } from 'react';
import { RotateCcw, X } from 'lucide-react';
import { C, FONT, GROUP_COLORS, R } from '../../../lib/design';
import { ICON_SIZE } from '../../../components/ui/icons';
import { Toggle } from '../../../components/ui';
import { CollapseGroup, SourceDot } from '../shared';
import { FLAGS, useFeature } from '../../../lib/featureFlags';
import type { GraphSettings } from './graphSettings';
import { GRAPH_DEFAULTS } from './graphSettings';

// Тело настроек графа (секции Фильтры / Группы / Отображение / Силы). Общий
// контент для левого сайдбара раздела (глобальный граф) и плавающей панели
// (локальный граф в карточке заметки). Ширину/отступы/скролл задаёт контейнер.
export function GraphSettingsBody({ settings, onChange, sources, tags, localMode }: {
  settings: GraphSettings;
  onChange: (updater: (s: GraphSettings) => GraphSettings) => void;
  sources: { key: string; label: string }[];
  tags: string[];
  localMode: boolean;    // локальный граф: добавляет слайдер глубины
}) {
  const docAnnotationsOn = useFeature(FLAGS.docAnnotations);
  // Поиск с дебаунсом — не дёргать фильтрацию/симуляцию на каждый символ
  const [search, setSearch] = useState(settings.filters.search);
  useEffect(() => { setSearch(settings.filters.search); }, [settings.filters.search]);
  useEffect(() => {
    const t = setTimeout(() => {
      if (search !== settings.filters.search)
        onChange(s => ({ ...s, filters: { ...s.filters, search } }));
    }, 200);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search]);

  const patchFilters = (p: Partial<GraphSettings['filters']>) =>
    onChange(s => ({ ...s, filters: { ...s.filters, ...p } }));
  const patchDisplay = (p: Partial<GraphSettings['display']>) =>
    onChange(s => ({ ...s, display: { ...s.display, ...p } }));
  const patchForces = (p: Partial<GraphSettings['forces']>) =>
    onChange(s => ({ ...s, forces: { ...s.forces, ...p } }));

  return (
    <>
      {/* --- Фильтры --- */}
      <CollapseGroup title={<SectionTitle>Фильтры</SectionTitle>}>
        <input
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Поиск…"
          title="Операторы: tag:идея source:Личный «фраза» -исключить"
          style={textInput}
        />
        <ToggleRow label="Только существующие" hint="Скрыть заметки-призраки (ссылки без файла)"
          checked={settings.filters.existingOnly}
          onChange={v => patchFilters({ existingOnly: v })} />
        <ToggleRow label="Одиночные заметки" hint="Показывать узлы без единой связи"
          checked={settings.filters.showOrphans}
          onChange={v => patchFilters({ showOrphans: v })} />
        {docAnnotationsOn && (
          <ToggleRow label="Комментарии к документам" hint="Узлы-комментарии со связями к документам и тредам (охра — открыт, зелёный — решён)"
            checked={settings.filters.showComments}
            onChange={v => patchFilters({ showComments: v })} />
        )}
        {localMode && (
          <SettingSlider label="Глубина" min={1} max={3} step={1}
            value={settings.filters.depth}
            onChange={v => patchFilters({ depth: v })} />
        )}
        {sources.length > 1 && (
          <div style={{ marginTop: 6 }}>
            <div style={subCap}>Источники</div>
            {sources.map(s => {
              const on = !settings.filters.hiddenSources.includes(s.key);
              return (
                <button key={s.key}
                  onClick={() => patchFilters({
                    hiddenSources: on
                      ? [...settings.filters.hiddenSources, s.key]
                      : settings.filters.hiddenSources.filter(k => k !== s.key),
                  })}
                  style={{
                    width: '100%', display: 'flex', alignItems: 'center', gap: 7, padding: '4px 2px',
                    background: 'none', border: 'none', cursor: 'pointer', fontFamily: FONT.sans,
                    fontSize: 12.5, color: on ? C.textPrimary : C.textMuted, opacity: on ? 1 : 0.55,
                  }}>
                  <SourceDot source={s.key} size={8} />
                  <span style={{ flex: 1, textAlign: 'left', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.label}</span>
                </button>
              );
            })}
          </div>
        )}
        {tags.length > 0 && (
          <div style={{ marginTop: 6 }}>
            <div style={subCap}>Теги</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }}>
              {tags.map(t => {
                const on = settings.filters.selectedTags.includes(t);
                return (
                  <button key={t}
                    onClick={() => patchFilters({
                      selectedTags: on
                        ? settings.filters.selectedTags.filter(x => x !== t)
                        : [...settings.filters.selectedTags, t],
                    })}
                    style={{
                      fontSize: 11, fontWeight: 500, borderRadius: R.sm, padding: '2px 7px', cursor: 'pointer',
                      fontFamily: FONT.sans, border: 'none',
                      background: on ? C.accent : C.bgSelected,
                      color: on ? C.onAccent : C.textSecondary,
                    }}>#{t}</button>
                );
              })}
            </div>
          </div>
        )}
      </CollapseGroup>

      {/* --- Группы: раскраска по запросу, приоритет — первая подошедшая --- */}
      <CollapseGroup title={<SectionTitle>Группы</SectionTitle>} defaultOpen={settings.groups.length > 0}>
        {settings.groups.map((g, i) => (
          <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 5 }}>
            <input type="color" value={g.color}
              onChange={e => onChange(s => ({ ...s, groups: s.groups.map((x, j) => j === i ? { ...x, color: e.target.value } : x) }))}
              style={{ width: 22, height: 22, padding: 0, border: `1px solid ${C.border}`, borderRadius: 5, background: 'none', cursor: 'pointer', flex: 'none' }} />
            <input value={g.query} placeholder="tag:идея / слово"
              onChange={e => onChange(s => ({ ...s, groups: s.groups.map((x, j) => j === i ? { ...x, query: e.target.value } : x) }))}
              style={{ ...textInput, marginBottom: 0, flex: 1, minWidth: 0 }} />
            <button title="Удалить группу"
              onClick={() => onChange(s => ({ ...s, groups: s.groups.filter((_, j) => j !== i) }))}
              style={miniBtn}>
              <X size={ICON_SIZE.xs} strokeWidth={2} />
            </button>
          </div>
        ))}
        <button
          onClick={() => onChange(s => ({ ...s, groups: [...s.groups, { query: '', color: GROUP_COLORS[s.groups.length % GROUP_COLORS.length] }] }))}
          style={{
            width: '100%', padding: '5px 0', fontSize: 12, fontFamily: FONT.sans, cursor: 'pointer',
            background: 'none', border: `1px dashed ${C.dashed}`, borderRadius: R.md, color: C.textSecondary,
          }}>+ Группа</button>
      </CollapseGroup>

      {/* --- Отображение --- */}
      <CollapseGroup title={<SectionTitle>Отображение</SectionTitle>} defaultOpen={false}>
        <ToggleRow label="Стрелки" hint="Направление ссылок"
          checked={settings.display.arrows}
          onChange={v => patchDisplay({ arrows: v })} />
        <SettingSlider label="Затухание текста" min={0.1} max={2.5} step={0.05}
          value={settings.display.textFade}
          onChange={v => patchDisplay({ textFade: v })} />
        <SettingSlider label="Размер узлов" min={0.5} max={2} step={0.05}
          value={settings.display.nodeSize}
          onChange={v => patchDisplay({ nodeSize: v })} />
        <SettingSlider label="Толщина связей" min={0.3} max={5} step={0.1}
          value={settings.display.lineWidth}
          onChange={v => patchDisplay({ lineWidth: v })} />
      </CollapseGroup>

      {/* --- Силы (маппинг слайдеров Obsidian на d3-force) --- */}
      <CollapseGroup
        defaultOpen={false}
        title={<SectionTitle>Силы</SectionTitle>}
        tail={
          // span, а не button: CollapseGroup рендерит заголовок как <button>, вложенная кнопка невалидна
          <span role="button" tabIndex={0} title="Сбросить силы"
            onClick={e => { e.stopPropagation(); onChange(s => ({ ...s, forces: { ...GRAPH_DEFAULTS.forces } })); }}
            style={miniBtn}>
            <RotateCcw size={ICON_SIZE.xs} strokeWidth={2} />
          </span>
        }>
        <SettingSlider label="Притяжение к центру" min={0} max={1} step={0.02}
          value={settings.forces.center}
          onChange={v => patchForces({ center: v })} />
        <SettingSlider label="Отталкивание" min={0} max={20} step={0.5}
          value={settings.forces.repel}
          onChange={v => patchForces({ repel: v })} />
        <SettingSlider label="Сила связей" min={0} max={1} step={0.02}
          value={settings.forces.link}
          onChange={v => patchForces({ link: v })} />
        <SettingSlider label="Длина связей" min={30} max={500} step={5}
          value={settings.forces.linkDistance}
          onChange={v => patchForces({ linkDistance: v })} />
      </CollapseGroup>
    </>
  );
}

function SectionTitle({ children }: { children: ReactNode }) {
  return <span style={{ fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '.04em' }}>{children}</span>;
}

function ToggleRow({ label, hint, checked, onChange }: {
  label: string;
  hint?: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div title={hint} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '4px 2px' }}>
      <span style={{ flex: 1, fontSize: 12.5, color: C.textPrimary }}>{label}</span>
      <Toggle checked={checked} onChange={onChange} width={30} height={18} />
    </div>
  );
}

function SettingSlider({ label, min, max, step, value, onChange }: {
  label: string;
  min: number;
  max: number;
  step: number;
  value: number;
  onChange: (v: number) => void;
}) {
  return (
    <div style={{ padding: '4px 2px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11.5, color: C.textSecondary, marginBottom: 2 }}>
        <span>{label}</span>
        <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted }}>{step >= 1 ? value : value.toFixed(2).replace(/\.?0+$/, '')}</span>
      </div>
      <input
        type="range" min={min} max={max} step={step} value={value}
        onChange={e => onChange(Number(e.target.value))}
        style={{ width: '100%', accentColor: C.accent, cursor: 'pointer', margin: 0 }}
      />
    </div>
  );
}

const textInput: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.md, padding: '5px 8px', fontSize: 12, fontFamily: FONT.sans,
  color: C.textHeading, outline: 'none', marginBottom: 6,
};

const subCap: React.CSSProperties = {
  fontSize: 10, letterSpacing: '.05em', textTransform: 'uppercase',
  color: C.textMuted, fontWeight: 600, margin: '4px 0 4px',
};

const miniBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 'none',
  width: 20, height: 20, background: 'none', border: 'none',
  color: C.textMuted, cursor: 'pointer', padding: 0,
};
