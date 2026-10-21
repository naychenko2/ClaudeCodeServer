import { useEffect, useState, type CSSProperties } from 'react';
import { Plug, Search } from 'lucide-react';
import type { PluginInfo } from '../../lib/api';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, IconField, WaitingIndicator } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

// Пикер целей привязки типа «Плагин» (ADR-031): цель — составная строка
// «plugin:{id}/{skill}», где {id} = {publisher}/{slug} установленного плагина,
// {skill} — имя навыка. Каталог целей здесь не каталог бэка, а список
// УСТАНОВЛЕННЫХ плагинов владельца: сначала выбираем плагин, затем вводим имя
// навыка (моно, бэк пока не возвращает вклады плагина в списке — см. 2.2).
// onCommit получает готовую цель и человекочитаемую подпись — родитель ведёт
// степпер на шаг ③ «Правило».

const STATE_PAD = `${SP.md}px ${SP.lg}px`;

// Человекочитаемое имя плагина из id: «local-abc/weekly-report» → «Weekly Report»
function pluginName(id: string): string {
  const slug = id.split('/').pop() ?? id;
  return slug.split(/[-_]+/).filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1)).join(' ');
}

export function PluginTargetPicker({ onCommit }: {
  onCommit: (target: string, label: string) => void;
}) {
  const [plugins, setPlugins] = useState<PluginInfo[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [reload, setReload] = useState(0);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<PluginInfo | null>(null);
  const [skill, setSkill] = useState('');

  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс списка перед загрузкой
    setPlugins(null);
    setLoadError(false);
    api.plugins.list()
      .then(list => { if (alive) setPlugins(list); })
      .catch(() => { if (alive) { setPlugins([]); setLoadError(true); } });
    return () => { alive = false; };
  }, [reload]);

  const q = query.trim().toLowerCase();
  const filtered = (plugins ?? []).filter(p =>
    !q || p.id.toLowerCase().includes(q) || pluginName(p.id).toLowerCase().includes(q));

  // === Стадия 2: плагин выбран — вводим имя навыка ===
  if (selected) {
    const skillTrim = skill.trim();
    return (
      <div style={{ marginTop: SP.md }}>
        <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.sm }}>
          Плагин «{pluginName(selected.id)}» · v{selected.version}
        </div>
        <div style={fLabel}>Навык плагина</div>
        <IconField
          mono
          autoFocus
          value={skill}
          onChange={setSkill}
          placeholder="weekly-report"
          height={38}
          radius={R.lg}
          fontSize={12.5}
          onEnter={() => { if (skillTrim) onCommit(`plugin:${selected.id}/${skillTrim}`, `Навык «${skillTrim}» · ${pluginName(selected.id)}`); }}
          icon={<Plug size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        />
        <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>
          Имя навыка из манифеста плагина (skills.sh-скилл или команда)
        </div>
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: SP.sm, marginTop: 12 }}>
          <Button variant="ghost" size="sm" onClick={() => { setSelected(null); setSkill(''); }}>
            Назад
          </Button>
          <Button variant="primary" size="sm" disabled={!skillTrim}
            onClick={() => onCommit(`plugin:${selected.id}/${skillTrim}`, `Навык «${skillTrim}» · ${pluginName(selected.id)}`)}>
            Далее
          </Button>
        </div>
      </div>
    );
  }

  // === Стадия 1: выбор плагина ===
  return (
    <>
      <div style={{ marginTop: SP.md }}>
        <IconField
          type="search"
          value={query}
          onChange={setQuery}
          placeholder="Найти плагин…"
          height={38}
          radius={R.lg}
          fontSize={FS.base}
          icon={<Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        />
      </div>
      <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, marginTop: SP.sm, overflow: 'hidden' }}>
        {plugins === null && (
          <div style={{ padding: STATE_PAD }}><WaitingIndicator /></div>
        )}
        {plugins !== null && loadError && (
          <div style={{ padding: STATE_PAD, fontSize: FS.sm, color: C.dangerText, display: 'flex', alignItems: 'center', gap: SP.sm }}>
            Не удалось загрузить список.
            <Button variant="ghost" size="sm" onClick={() => setReload(r => r + 1)}>Повторить</Button>
          </div>
        )}
        {plugins !== null && !loadError && filtered.length === 0 && (
          <div style={{ padding: STATE_PAD, fontSize: FS.sm, color: C.textMuted }}>
            {q ? 'Ничего не найдено' : 'Плагинов пока нет — поставьте из папки в «Настройки проекта → Плагины»'}
          </div>
        )}
        {plugins !== null && !loadError && filtered.map(p => (
          <div
            key={p.id}
            role="button"
            tabIndex={0}
            onClick={() => setSelected(p)}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelected(p); } }}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', textAlign: 'left',
              padding: `${SP.sm}px ${SP.lg}px`, minHeight: 52, cursor: 'pointer', fontFamily: FONT.sans,
              boxSizing: 'border-box', borderRadius: R.lg,
            }}
          >
            <span style={{
              width: 32, height: 32, borderRadius: R.full, flexShrink: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: C.infoBg, color: C.info,
            }}>
              <Plug size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </span>
            <span style={{ flex: 1, minWidth: 0 }}>
              <span style={{
                fontSize: FS.base, fontWeight: 600, color: C.textHeading,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', display: 'block',
              }}>{pluginName(p.id)}</span>
              <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>
                {p.id} · v{p.version}
              </span>
            </span>
          </div>
        ))}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5 }}>
        Выберите плагин, затем введите имя навыка, к которому персона получит доступ.
      </div>
    </>
  );
}

// Метка поля (как fLabel в PersonaBindingsPanel)
const fLabel: CSSProperties = {
  fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, marginBottom: SP.xs,
};
