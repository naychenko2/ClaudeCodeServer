// Панель «Подставить персонажа» (этап 3 · ADR-028):
//  1. Notices — плашки состояний (отказы, лимит, 409)
//  2. Character picker — только пока есть активное лицо, выше списка назначений
//  3. Assignments list — «Назначения · N из 2», строки «Лицо n — Имя ✕»
//  4. Footer — чип котировки + кнопка «Подставить» / «Подставить (2)»
// На мобиле подвал прилипает к низу, зона касания ≥ 44.

import { useEffect, useRef, type ReactNode } from 'react';
import { AlertTriangle, Cpu, Info, RefreshCw, ScanFace, Clock, X } from 'lucide-react';
import { Badge, Button, IconButton, MetaChip, C, R, SP, FS, ICON_SIZE, ICON_STROKE, useIsMobile } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from './api';
import { FS_TEXT, type FaceAssignments, type NoticeSpec } from './faceSwapView';

const ic = (I: typeof Cpu, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// ── Плашка-уведомление ─────────────────────────────────────────────────────────
function Notice({ n, mobile, onAction, onUnassign }: {
  n: NoticeSpec; mobile: boolean; onAction: (n: NoticeSpec) => void; onUnassign: (face: number) => void;
}) {
  const tone = n.tone;
  const bg = tone === 'danger' ? C.dangerBg : tone === 'warning' ? C.warningBg : C.bgCard;
  const border = tone === 'danger' ? C.dangerBorder : C.borderLight;
  const fg = tone === 'danger' ? C.dangerText : tone === 'warning' ? C.warningText : C.textPrimary;
  const icon = n.icon === 'clock' ? ic(Clock) : n.icon === 'redo' ? ic(RefreshCw) : n.icon === 'cpu' ? ic(Cpu)
    : n.icon === 'alert' ? ic(AlertTriangle) : ic(Info);
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.xs, padding: SP.sm, borderRadius: R.md,
      background: bg, border: `1px solid ${border}`, color: fg, fontSize: FS.sm, lineHeight: 1.45,
    }}>
      <span style={{ color: fg, opacity: 0.8, flexShrink: 0, marginTop: 1 }}>{icon}</span>
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: SP.xs }}>
        <span>{n.text}</span>
        {/* Отказ по лицу: «Снять назначение с лица n» (зона касания ≥ 44 на мобиле) */}
        {n.unassign?.map(f => (
          <Button key={f} size="xs" variant="secondary" style={mobile ? { minHeight: 44 } : undefined}
            onClick={() => onUnassign(f)}>{FS_TEXT.unassignN(f)}</Button>
        ))}
      </span>
      {n.action && (
        <Button size="xs" variant={n.action.kind === 'ghostAccent' ? 'ghostAccent' : 'secondary'}
          onClick={() => onAction(n)} style={{ flexShrink: 0, ...(mobile ? { minHeight: 44 } : null) }}>{n.action.label}</Button>
      )}
    </div>
  );
}

// ── Аватар персонажа ───────────────────────────────────────────────────────────
function Avatar({ api, projectId, c, size }: { api: ImageEditorApi; projectId: string; c: ImageEditCharacter; size: number }) {
  const file = c.photos[0]?.file;
  return (
    <span style={{
      width: size, height: size, flex: `0 0 ${size}px`, borderRadius: R.max, overflow: 'hidden',
      background: C.bgInset, border: `1px solid ${C.border}`, display: 'inline-flex',
      alignItems: 'center', justifyContent: 'center', color: C.textMuted,
    }}>
      {file
        ? <img src={api.characterPhotoUrl(projectId, c.slug, file)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
        : ic(ScanFace, size >= 44 ? ICON_SIZE.sm : ICON_SIZE.xs)}
    </span>
  );
}

// ── Строка персонажа (десктоп) ─────────────────────────────────────────────────
function CharacterRow({ api, projectId, c, selected, inContext, taken, onPick }: {
  api: ImageEditorApi; projectId: string; c: ImageEditCharacter;
  selected: boolean; inContext: boolean; taken: number | null; onPick: () => void;
}) {
  const disabled = taken != null;
  return (
    <button
      type="button" role="radio" aria-checked={selected}
      aria-disabled={disabled || undefined}
      title={taken != null ? FS_TEXT.takenOn(taken) : undefined}
      onClick={disabled ? undefined : onPick}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minWidth: 0,
        minHeight: 44, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
        border: `1px solid ${selected ? C.accent : 'transparent'}`,
        background: selected ? C.accentLight : 'none',
        cursor: disabled ? 'not-allowed' : 'pointer', color: C.textPrimary, textAlign: 'left',
        fontFamily: 'inherit',
      }}
    >
      {/* Занятый: приглушены аватар и имя, подпись «Уже на лице N» — читаемая (m6) */}
      <span style={{ display: 'inline-flex', opacity: disabled ? 0.5 : 1 }}><Avatar api={api} projectId={projectId} c={c} size={32} /></span>
      <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', opacity: disabled ? 0.5 : 1 }}>{c.name}</span>
        {taken != null && <span style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.3 }}>{FS_TEXT.takenOn(taken)}</span>}
      </span>
      {inContext && taken == null && <Badge tone="neutral">в контексте</Badge>}
      <span aria-hidden style={{
        width: 16, height: 16, flex: '0 0 16px', borderRadius: R.max,
        border: `1.5px solid ${selected ? C.accent : C.dashed}`,
        background: selected ? C.accent : 'none', display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}>
        {selected && <span style={{ width: 6, height: 6, borderRadius: R.max, background: C.onAccent }} />}
      </span>
    </button>
  );
}

// ── Плитка персонажа (мобила) ───────────────────────────────────────────────────
function CharacterTile({ api, projectId, c, selected, inContext, taken, onPick }: {
  api: ImageEditorApi; projectId: string; c: ImageEditCharacter;
  selected: boolean; inContext: boolean; taken: number | null; onPick: () => void;
}) {
  const disabled = taken != null;
  return (
    <button
      type="button" role="radio" aria-checked={selected}
      aria-disabled={disabled || undefined}
      title={taken != null ? FS_TEXT.takenOn(taken) : undefined}
      onClick={disabled ? undefined : onPick}
      style={{
        flex: '0 0 72px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs,
        padding: `${SP.xs}px ${SP.xxs}px`, borderRadius: R.lg,
        border: `1px solid ${selected ? C.accent : 'transparent'}`,
        background: selected ? C.accentLight : 'none',
        cursor: disabled ? 'not-allowed' : 'pointer', color: C.textPrimary, minWidth: 0, minHeight: 84,
        fontFamily: 'inherit',
      }}
    >
      <span style={{ display: 'inline-flex', opacity: disabled ? 0.5 : 1 }}><Avatar api={api} projectId={projectId} c={c} size={44} /></span>
      <span style={{ fontSize: FS.xs, fontWeight: 600, maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', opacity: disabled ? 0.5 : 1 }}>{c.name}</span>
      {taken != null
        ? <span style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1 }}>{FS_TEXT.busyTile(taken)}</span>
        : inContext && <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1 }}>в контексте</span>}
    </button>
  );
}

// ── Строка списка назначений ───────────────────────────────────────────────────
function AssignmentRow({ api, projectId, faceN, character, active, refusal, mobile, onRemove, onTap }: {
  api: ImageEditorApi; projectId: string; faceN: number;
  // null — лицо активно, персонаж ещё не выбран
  character: ImageEditCharacter | null; active: boolean;
  // Причина отказа по этому лицу (макет v2): строка красная, причина под именем
  refusal: string | null;
  mobile: boolean;
  onRemove: () => void; onTap: () => void;
}) {
  const rowBg = refusal ? C.dangerBg : active ? C.accentLight : C.bgCard;
  const rowBorder = refusal ? C.dangerBorder : active ? C.accent : C.border;
  return (
    <div
      role="listitem"
      data-face-refused={refusal ? '' : undefined}
      onClick={onTap}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        minHeight: mobile ? 48 : 44, padding: `${SP.xxs}px ${SP.xxs}px ${SP.xxs}px ${SP.sm}px`,
        borderRadius: R.md, border: `1px solid ${rowBorder}`, background: rowBg,
        cursor: 'pointer', color: C.textPrimary,
      }}
    >
      <Badge tone={refusal ? 'danger' : 'accent'}>{FS_TEXT.face(faceN)}</Badge>
      {character != null ? (
        <>
          <Avatar api={api} projectId={projectId} c={character} size={24} />
          <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
            <span style={{ fontSize: FS.base, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{character.name}</span>
            {refusal && <span style={{ fontSize: FS.xs, color: C.dangerText, lineHeight: 1.3 }}>{refusal}</span>}
          </span>
          <IconButton size="lg" ariaLabel={FS_TEXT.removeAria(faceN)} title={FS_TEXT.removeAria(faceN)}
            style={mobile ? { width: 44, height: 44 } : undefined}
            onClick={e => { e.stopPropagation(); onRemove(); }}>
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </>
      ) : (
        <span style={{ flex: 1, fontSize: FS.base, fontWeight: 400, color: C.textSecondary }}>{FS_TEXT.asgPicking}</span>
      )}
    </div>
  );
}

// ── Подвал: чип котировки + кнопка ─────────────────────────────────────────────
function Footer({ quote, facePasses, canSubmit, submitting, mobile, onSubmit }: {
  quote: string | null; facePasses: number | null; canSubmit: boolean; submitting: boolean; mobile: boolean; onSubmit: () => void;
}) {
  const label = facePasses == null || facePasses <= 1 ? FS_TEXT.go : FS_TEXT.go2;
  const chip = quote ? <MetaChip>{quote}</MetaChip> : null;
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      flexWrap: mobile ? 'wrap' : 'wrap', justifyContent: 'flex-end',
      ...(mobile ? { boxShadow: 'var(--shadow-sheet)' } : null),
    }}>
      {chip}
      <Button size={mobile ? 'md' : 'sm'} variant="primary" leftIcon={ic(ScanFace)}
        disabled={!canSubmit} loading={submitting}
        style={mobile ? { flex: '1 1 140px' } : { flex: '1 1 140px', minWidth: 140 }}
        onClick={onSubmit}>
        {label}
      </Button>
    </div>
  );
}

// ── Панель целиком ─────────────────────────────────────────────────────────────
export function FaceSwapPanel({ api, projectId, characters, assignments, refusals, facePasses, canSubmit, quote, localOnce, submitting, notices, onPick, onRemove, onTapFace, onNoticeAction, onSubmit }: {
  api: ImageEditorApi;
  projectId: string;
  characters: readonly ImageEditCharacter[];
  // Назначения «лицо → персонаж» (ADR-028, этап 3)
  assignments: FaceAssignments;
  // Отказы по назначенным лицам: номер лица → причина (assignmentRefusals)
  refusals: ReadonlyMap<number, string>;
  // Число проходов из котировки
  facePasses: number | null;
  canSubmit: boolean;
  quote: string | null;
  localOnce: boolean;
  submitting: boolean;
  notices: readonly NoticeSpec[];
  // Выбрать персонажа для активного лица
  onPick: (slug: string) => void;
  // Снять назначение с лица
  onRemove: (face: number) => void;
  // Тап по строке списка (активировать/переназначить)
  onTapFace: (face: number) => void;
  onNoticeAction: (n: NoticeSpec) => void;
  onSubmit: () => void;
}) {
  const mobile = useIsMobile();
  const { active, assignments: asgMap } = assignments;
  const asgCount = asgMap.size;
  // Мобила 320: выбор персонажа после тапа по лицу уходил за нижний край — подкручиваем к нему (M2)
  const pickerRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (active != null) pickerRef.current?.scrollIntoView?.({ block: 'nearest' });
  }, [active]);
  // Отступы содержимого — те же токены, что у подвала (M1)
  const padX = mobile ? SP.md : SP.lg;

  // Персонажи, занятые на другом лице: slug → номер лица
  const takenBy = new Map<string, number>();
  for (const [face, slug] of asgMap) {
    if (face !== active) takenBy.set(slug, face);
  }

  // Строки списка: назначенные + активное неназначенное, по номеру лица
  const faceNums = new Set<number>([...asgMap.keys()]);
  if (active != null) faceNums.add(active);
  const sortedNums = [...faceNums].sort((a, b) => a - b);

  const bySlug = new Map<string, ImageEditCharacter>();
  for (const c of characters) bySlug.set(c.slug, c);

  // ── Пикер персонажей: только пока есть активное лицо ──
  const picker: ReactNode = active != null ? (
    <div ref={pickerRef} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{FS_TEXT.pickFor(active)}</div>
      {mobile ? (
        <div role="radiogroup" aria-label={FS_TEXT.pickFor(active)} style={{
          display: 'flex', gap: SP.sm, overflowX: 'auto', padding: `${SP.xxs}px ${SP.xxs}px ${SP.xs}px`,
        }}>
          {characters.map(c => (
            <CharacterTile key={c.slug} api={api} projectId={projectId} c={c}
              selected={asgMap.get(active) === c.slug}
              inContext={false}
              taken={takenBy.get(c.slug) ?? null}
              onPick={() => onPick(c.slug)} />
          ))}
        </div>
      ) : (
        <div role="radiogroup" aria-label={FS_TEXT.pickFor(active)} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          {characters.map(c => (
            <CharacterRow key={c.slug} api={api} projectId={projectId} c={c}
              selected={asgMap.get(active) === c.slug}
              inContext={false}
              taken={takenBy.get(c.slug) ?? null}
              onPick={() => onPick(c.slug)} />
          ))}
        </div>
      )}
      <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{FS_TEXT.pickHint}</div>
    </div>
  ) : null;

  // ── Список назначений ──
  const list: ReactNode = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{FS_TEXT.asgTitle}</span>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{FS_TEXT.asgMeta(asgCount)}</span>
      </div>
      <div role="list" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        {sortedNums.map(n => {
          const slug = asgMap.get(n) ?? null;
          // Персонаж удалён из проекта — строка остаётся, имя = slug
          const character = slug == null ? null
            : bySlug.get(slug) ?? ({ slug, name: slug, photos: [] } as unknown as ImageEditCharacter);
          return (
            <AssignmentRow key={n} api={api} projectId={projectId}
              faceN={n} character={character} mobile={mobile}
              active={active === n}
              refusal={refusals.get(n) ?? null}
              onRemove={() => onRemove(n)}
              onTap={() => onTapFace(n)} />
          );
        })}
        {/* Пунктирный слот */}
        {asgCount === 0 && active == null && (
          <div style={{
            border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, color: C.textMuted, lineHeight: 1.4,
          }}>{FS_TEXT.asgEmpty}</div>
        )}
        {asgCount === 1 && active == null && (
          <div style={{
            border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, color: C.textMuted, lineHeight: 1.4,
          }}>{FS_TEXT.asgSlot}</div>
        )}
      </div>
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minHeight: 0, flex: 1 }}>
      {notices.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: mobile ? `${SP.sm}px ${padX}px 0` : `${SP.lg}px ${padX}px 0` }}>
          {notices.map((n, i) => <Notice key={i} n={n} mobile={mobile} onAction={onNoticeAction} onUnassign={onRemove} />)}
        </div>
      )}
      <div style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm, flex: 1, minHeight: 0, overflowY: mobile ? undefined : 'auto',
        // Над плашками отступ уже есть (gap), без них — свой верхний
        padding: `${notices.length > 0 ? 0 : mobile ? SP.sm : SP.lg}px ${padX}px 0`,
      }}>
        {picker}
        {list}
      </div>
      <div style={{
        flex: '0 0 auto', borderTop: `1px solid ${C.borderLight}`,
        padding: mobile ? `${SP.sm}px ${SP.md}px` : `${SP.md}px ${SP.lg}px`, background: C.bgPanel,
        // Мобила: общего подвала редактора в режиме нет (M2) — нижний safe-area берёт подвал панели
        ...(mobile ? { paddingBottom: `calc(${SP.sm}px + env(safe-area-inset-bottom))` } : null),
        // Мобила: блок прокручивается вместе с телом окна — подвал прилипает к низу (дефект Майи)
        ...(mobile ? { position: 'sticky' as const, bottom: 0, zIndex: 1 } : null),
      }}>
        {localOnce && (
          <div style={{ marginBottom: SP.xs }}>
            <Badge tone="info" icon={ic(Cpu)}>{FS_TEXT.localOnce}</Badge>
          </div>
        )}
        <Footer quote={quote} facePasses={facePasses} canSubmit={canSubmit} submitting={submitting} mobile={mobile} onSubmit={onSubmit} />
      </div>
    </div>
  );
}
