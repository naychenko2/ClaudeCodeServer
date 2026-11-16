// Карточка «Ферма» вкладки «Стенды» (макет А): шапка с плашкой худшего уровня,
// строка «горячо» и карточки узлов; касание узла выбирает его (ниже его детали — Ф2/Ф3).
// Чистый компонент на пропсах: данных не грузит, состояния не хранит — выбранный
// узел входит пропом selected, касание выходит onSelect(key). Строку движка даёт
// вызывающий через engineLines[key] (Ф3); строки нет — строки движка в карточке нет.
//
// Макет: docs/mockups/local-model-hardware-cluster-v3.html (farmSum/nodeCard/variantA),
// текст: local-model-hardware-cluster-v3-proposal.md, §«Что общее» и §«А».
// Порядок карточек — порядок farmNodes (он же порядок конфига бэка).
// Нештатный узел (down/stale/incompatible) тревогой не красится: точка-кольцо
// (C.track с обводкой C.textMuted), строка «не отвечает с HH:MM:SS» /
// «версия агента не подходит», цифры приглушены — о нештатности говорят status/since.

import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import { Dot } from '../../components/ui/Dot';
import { PressableArea } from '../../components/ui/PressableArea';
import type { ReactNode } from 'react';
import type { HardwareFarm, HardwareFarmNode, HwLevel } from './hardwareTypes';
import {
  farmHottest, farmHottestWord, farmNodeSilent, farmRolesLabel, farmSilentCount,
  hwLevelLabel, hwTemp,
} from './hardwareFormat';

// «HH:MM:SS» из unix-мс — локальное время браузера (тот же разбор, что в HardwareTab.tsx:20).
function formatHms(ms: number | null): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  const d = new Date(ms);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// Тон карточки узла (макет .nc): фон всегда белый, красится только рамка и значение.
// Молчащий узел и узел без измерений — без окраски вовсе (точка C.track, значение C.textMuted).
function tone(n: HardwareFarmNode): { border: string; dot: string; value: string } {
  if (farmNodeSilent(n)) return { border: C.borderLight, dot: C.track, value: C.textMuted };
  switch (n.level) {
    case 'danger': return { border: C.dangerBorder, dot: C.danger, value: C.dangerText };
    case 'warn': return { border: C.warning, dot: C.warning, value: C.warningText };
    case 'none': return { border: C.borderLight, dot: C.track, value: C.textMuted };
    default: return { border: C.borderLight, dot: C.success, value: C.textHeading };
  }
}

// Статусная строка молчащего узла вместо значения.
function statusLine(n: HardwareFarmNode): string {
  if (n.status === 'incompatible') return 'версия агента не подходит';
  const t = formatHms(n.since);
  return t ? `не отвечает с ${t}` : 'не отвечает';
}

// Плашка уровня фермы: ok → muted, warn → warning, danger → danger (тот же контроль, что LevelPill в HardwareTab.tsx:29).
function LevelPill({ level }: { level: HwLevel }) {
  if (level === 'none') return null;
  const bg = level === 'danger' ? C.dangerBg : level === 'warn' ? C.warningBg : C.bgInset;
  const fg = level === 'danger' ? C.dangerText : level === 'warn' ? C.warningText : C.textSecondary;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', background: bg, color: fg,
      borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
    }}>{hwLevelLabel(level)}</span>
  );
}

// Плашка фермы: молчащие узлы без опасности — «узел молчит», не «всё в норме»
// (живые в норме, ферма — нет: wording farmPill из макета). При опасности — обычная плашка.
function FarmPill({ farm }: { farm: HardwareFarm }) {
  const silent = farmSilentCount(farm);
  if (farm.level === 'danger' || silent === 0) return <LevelPill level={farm.level} />;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', background: C.warningBg, color: C.warningText,
      borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
    }}>{silent === 1 ? 'узел молчит' : 'узлы молчат'}</span>
  );
}

// Строка «горячо» с именем узла жирным (макет farmSum: <b>key</b> · имя плитки).
function hottestLine(farm: HardwareFarm): ReactNode | null {
  const h = farmHottest(farm);
  if (!h) return null;
  return (
    <>
      {farmHottestWord(h.hottest.level)} — <b style={{ fontWeight: 700 }}>{h.node.title}</b>
      {' '}· {h.hottest.label.toLowerCase()}: {hwTemp(h.hottest.tempC)}
    </>
  );
}

const NAME_S = { fontSize: FS.sm, fontWeight: 700, fontFamily: FONT.mono, color: C.textHeading, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;
const ROLES_S = { fontSize: FS.xxs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;
const WHAT_S = { fontSize: FS.xxs, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;
const ENG_S = { fontSize: FS.xxs, color: C.textMuted, marginTop: SP.xs, paddingTop: SP.xs, borderTop: `1px solid ${C.borderLight}`, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;

// Карточка узла (макет .nc): точка-светофор, имя, роли словами, значение (hottest)
// или статусная строка, строка движка. Выбранная — C.accentLight с рамкой C.accentMuted.
// На мобили — строка списка: имя и «роли · что» слева, значение справа, движок внизу
// (макет .farm.list); на десктопе — блок в сетке карточек.
function NodeCard({ node, selected, onSelect, engineLine, isMobile }: {
  node: HardwareFarmNode;
  selected: boolean;
  onSelect: (key: string) => void;
  engineLine: string | undefined;
  isMobile: boolean;
}) {
  const silent = farmNodeSilent(node);
  const t = tone(node);
  const bg = selected ? C.accentLight : C.bgWhite;
  const border = selected ? C.accentMuted : t.border;
  const roles = farmRolesLabel(node.roles);
  const hot = silent ? null : node.hottest;
  const value = silent ? statusLine(node) : hot?.tempC != null ? hwTemp(hot.tempC) : 'нет данных';
  const what = hot ? hot.label.toLowerCase() : '';
  // Значение — крупная mono-цифра (макет .big 18px, на мобиле 16px). Статусная строка
  // молчащего узла и «нет данных» — не значение: обычный размер и приглушены (.big .na, 12px).
  const hasValue = !silent && hot?.tempC != null;
  const valueFs = hasValue ? (isMobile ? FS.lg : FS.xl) : FS.sm;
  const valueColor = hasValue ? t.value : C.textMuted;
  const aria = silent ? `Узел ${node.title}: ${statusLine(node)}` : `Узел ${node.title}: ${roles}`;
  // aria-expanded: выбор узла раскрывает ниже его детали (макет А) — у PressableArea
  // из штатных aria-свойств есть только это; aria-pressed в контроле нет.
  const head = (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
      <Dot color={silent ? C.track : t.dot} stroke={silent ? C.textMuted : undefined} />
      <span style={NAME_S}>{node.title}</span>
    </span>
  );
  const eng = engineLine ? <div style={ENG_S}>{engineLine}</div> : null;
  if (isMobile) {
    return (
      <PressableArea
        onPress={() => onSelect(node.key)}
        aria-label={aria}
        aria-expanded={selected}
        style={{
          display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: 0,
          alignItems: 'center', background: bg, border: `1px solid ${border}`,
          borderRadius: R.lg, padding: `${SP.xs}px ${SP.sm}px`, minWidth: 0,
        }}
      >
        <span style={{ minWidth: 0 }}>{head}<div style={ROLES_S}>{roles}</div>{what ? <div style={WHAT_S}>{what}</div> : null}</span>
        <span style={{ justifySelf: 'end', textAlign: 'right', fontSize: valueFs, fontWeight: 600, fontFamily: FONT.mono, color: valueColor, whiteSpace: 'nowrap' }}>{value}</span>
        {eng ? <div style={{ ...ENG_S, gridColumn: '1 / span 2' }}>{engineLine}</div> : null}
      </PressableArea>
    );
  }
  return (
    <PressableArea
      onPress={() => onSelect(node.key)}
      aria-label={aria}
      aria-expanded={selected}
      style={{
        display: 'flex', flexDirection: 'column', background: bg, border: `1px solid ${border}`,
        borderRadius: R.lg, padding: SP.sm, minWidth: 0, minHeight: 44,
      }}
    >
      {head}
      <div style={ROLES_S}>{roles}</div>
      <div style={{ fontSize: valueFs, fontWeight: 600, fontFamily: FONT.mono, color: valueColor, lineHeight: 1.2, marginTop: SP.xs, whiteSpace: 'nowrap' }}>{value}</div>
      {what ? <div style={WHAT_S}>{what}</div> : null}
      {eng}
    </PressableArea>
  );
}

// === Карточка «Ферма» ===
export function FarmSummaryCard({ farm, selected, onSelect, isMobile, engineLines }: {
  farm: HardwareFarm;
  // key выбранного узла; null — никто не выбран (выбор и его хранение — за вызывающим, Ф3)
  selected: string | null;
  onSelect: (key: string) => void;
  isMobile: boolean;
  // строка движка узла по key (её составляет Ф3); нет строки — строки движка нет
  engineLines?: Record<string, string>;
}) {
  const line = hottestLine(farm);
  // Десктоп: карточки в ряд, по числу узлов (макет .farm); мобила (320): список строк (.farm.list).
  const cols = isMobile ? '1fr' : `repeat(${Math.max(1, farm.farmNodes.length)}, minmax(0, 1fr))`;
  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{ margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Ферма
        </h3>
        <FarmPill farm={farm} />
      </div>
      {line && <div style={{ fontSize: FS.sm, color: C.textSecondary, margin: `-2px 0 ${SP.sm}px` }}>{line}</div>}
      <div style={{ display: 'grid', gridTemplateColumns: cols, gap: isMobile ? SP.xs : SP.sm }} role="group" aria-label="Узлы фермы">
        {farm.farmNodes.map(n => (
          <NodeCard
            key={n.key}
            node={n}
            selected={n.key === selected}
            onSelect={onSelect}
            engineLine={engineLines?.[n.key]}
            isMobile={isMobile}
          />
        ))}
      </div>
    </section>
  );
}
