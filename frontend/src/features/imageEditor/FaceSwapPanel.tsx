// Панель выбора персонажа «Подставить персонажа» (этап 2 · шаг 10b, вариант A):
// заголовок «Чьё лицо подставить?», список персонажей проекта (десктоп — строки, мобила —
// горизонтальная лента плиток), подсказка, подвал (чип котировки + Button «Подставить»).
// По умолчанию никто не выбран (персонаж контекста сам не подставляется). На мобиле подвал
// прилипает к низу (решение команды №2).

import type { ReactNode } from 'react';
import { AlertTriangle, Cpu, Info, RefreshCw, ScanFace, Clock } from 'lucide-react';
import { Badge, Button, MetaChip, C, R, SP, ICON_SIZE, ICON_STROKE, useIsMobile } from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from './api';
import { FS_TEXT, type NoticeSpec } from './faceSwapView';

const ic = (I: typeof Cpu, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Плашка-уведомление (состояния 2/5/6/7/8/9/10): тон (info/warning/danger), иконка, текст,
// опциональное действие. Фон/рамка/текст — по тону, токены design.ts.
function Notice({ n, onAction }: { n: NoticeSpec; onAction: (n: NoticeSpec) => void }) {
  const tone = n.tone;
  const bg = tone === 'danger' ? C.dangerBg : tone === 'warning' ? C.warningBg : C.bgCard;
  const border = tone === 'danger' ? C.dangerBorder : C.borderLight;
  const fg = tone === 'danger' ? C.dangerText : tone === 'warning' ? C.warningText : C.textPrimary;
  const icon = n.icon === 'clock' ? ic(Clock) : n.icon === 'redo' ? ic(RefreshCw) : n.icon === 'cpu' ? ic(Cpu)
    : n.icon === 'alert' ? ic(AlertTriangle) : ic(Info);
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: SP.xs, padding: SP.sm, borderRadius: R.md,
      background: bg, border: `1px solid ${border}`, color: fg, fontSize: 12, lineHeight: 1.45,
    }}>
      <span style={{ color: fg, opacity: 0.8, flexShrink: 0, marginTop: 1 }}>{icon}</span>
      <span style={{ flex: 1, minWidth: 0 }}>{n.text}</span>
      {n.action && (
        <Button size="xs" variant={n.action.kind === 'ghostAccent' ? 'ghostAccent' : 'secondary'}
          onClick={() => onAction(n)} style={{ flexShrink: 0 }}>{n.action.label}</Button>
      )}
    </div>
  );
}

// Аватар персонажа: первое фото; без фото — нейтральный кружок
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

// Строка персонажа (десктоп): аватар 32, имя, «в контексте», радио. role=radio в группе
function CharacterRow({ api, projectId, c, selected, inContext, disabled, onPick }: {
  api: ImageEditorApi; projectId: string; c: ImageEditCharacter;
  selected: boolean; inContext: boolean; disabled: boolean; onPick: () => void;
}) {
  return (
    <button
      type="button" role="radio" aria-checked={selected} disabled={disabled}
      onClick={onPick}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minWidth: 0,
        minHeight: 44, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
        border: `1px solid ${selected ? C.accent : 'transparent'}`,
        background: selected ? C.accentLight : 'none',
        cursor: disabled ? 'default' : 'pointer', color: C.textPrimary, textAlign: 'left',
        opacity: disabled ? 0.5 : 1, fontFamily: 'inherit',
      }}
    >
      <Avatar api={api} projectId={projectId} c={c} size={32} />
      <span style={{ flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.name}</span>
      {inContext && <Badge tone="neutral">в контексте</Badge>}
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

// Плитка персонажа (мобила): аватар 44, имя, «в контексте» второй строкой. role=radio
function CharacterTile({ api, projectId, c, selected, inContext, disabled, onPick }: {
  api: ImageEditorApi; projectId: string; c: ImageEditCharacter;
  selected: boolean; inContext: boolean; disabled: boolean; onPick: () => void;
}) {
  return (
    <button
      type="button" role="radio" aria-checked={selected} disabled={disabled}
      onClick={onPick}
      style={{
        flex: '0 0 72px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs,
        padding: `${SP.xs}px ${SP.xxs}px`, borderRadius: R.lg,
        border: `1px solid ${selected ? C.accent : 'transparent'}`,
        background: selected ? C.accentLight : 'none',
        cursor: disabled ? 'default' : 'pointer', color: C.textPrimary, minWidth: 0, minHeight: 84,
        opacity: disabled ? 0.5 : 1, fontFamily: 'inherit',
      }}
    >
      <Avatar api={api} projectId={projectId} c={c} size={44} />
      <span style={{ fontSize: 11, fontWeight: 600, maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.name}</span>
      {inContext && <span style={{ fontSize: 11, color: C.textMuted, lineHeight: 1 }}>в контексте</span>}
    </button>
  );
}

// Подвал: чип котировки + Button «Подставить»; на мобиле кнопка во всю ширину, подвал прилипает
function Footer({ quote, localOnce, canSubmit, submitting, mobile, onSubmit }: {
  quote: string | null; localOnce: boolean; canSubmit: boolean; submitting: boolean; mobile: boolean; onSubmit: () => void;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: mobile ? 'nowrap' : 'wrap',
      ...(mobile ? { boxShadow: 'var(--shadow-sheet)' } : null),
    }}>
      {localOnce && (
        <Badge tone="info" icon={ic(Cpu)}>
          {FS_TEXT.localOnce}
        </Badge>
      )}
      {!mobile && <span style={{ flex: 1 }} />}
      {quote && <MetaChip>{quote}</MetaChip>}
      <Button size={mobile ? 'md' : 'sm'} variant="primary" leftIcon={ic(ScanFace)}
        disabled={!canSubmit} loading={submitting} fullWidth={mobile} onClick={onSubmit}>
        {FS_TEXT.go}
      </Button>
    </div>
  );
}

// Панель целиком. listEnabled — доступен ли список (лицо выбрано/закрашено); canSubmit — по матрице.
// notices — плашки состояний (2/5/6/7/8/9/10); onNoticeAction — действие из плашки (кисть/локальные).
export function FaceSwapPanel({ api, projectId, characters, selectedCharacter, inContextSlug, listEnabled, canSubmit, quote, localOnce, submitting, notices, onPick, onNoticeAction, onSubmit }: {
  api: ImageEditorApi;
  projectId: string;
  characters: readonly ImageEditCharacter[];
  selectedCharacter: string | null;
  inContextSlug: string | null;
  listEnabled: boolean;
  canSubmit: boolean;
  quote: string | null;
  localOnce: boolean;
  submitting: boolean;
  notices: readonly NoticeSpec[];
  onPick: (slug: string) => void;
  onNoticeAction: (n: NoticeSpec) => void;
  onSubmit: () => void;
}) {
  const mobile = useIsMobile();
  const disabled = !listEnabled;

  const list: ReactNode = mobile ? (
    <div role="radiogroup" aria-label={FS_TEXT.pickTitle} style={{
      display: 'flex', gap: SP.sm, overflowX: 'auto', padding: `${SP.xxs}px ${SP.xxs}px ${SP.xs}px`,
      opacity: disabled ? 0.5 : 1,
    }}>
      {characters.map(c => (
        <CharacterTile key={c.slug} api={api} projectId={projectId} c={c}
          selected={selectedCharacter === c.slug} inContext={inContextSlug === c.slug}
          disabled={disabled} onPick={() => onPick(c.slug)} />
      ))}
    </div>
  ) : (
    <div role="radiogroup" aria-label={FS_TEXT.pickTitle} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, opacity: disabled ? 0.5 : 1 }}>
      {characters.map(c => (
        <CharacterRow key={c.slug} api={api} projectId={projectId} c={c}
          selected={selectedCharacter === c.slug} inContext={inContextSlug === c.slug}
          disabled={disabled} onPick={() => onPick(c.slug)} />
      ))}
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minHeight: 0, flex: 1 }}>
      {notices.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {notices.map((n, i) => <Notice key={i} n={n} onAction={onNoticeAction} />)}
        </div>
      )}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, flex: 1, minHeight: 0, overflowY: mobile ? undefined : 'auto' }}>
        <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>{FS_TEXT.pickTitle}</div>
        {list}
        <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.45 }}>{FS_TEXT.pickHint}</div>
      </div>
      <div style={{
        flex: '0 0 auto', borderTop: `1px solid ${C.borderLight}`,
        padding: mobile ? `${SP.sm}px ${SP.md}px` : `${SP.md}px ${SP.lg}px`, background: C.bgPanel,
      }}>
        <Footer quote={quote} localOnce={localOnce} canSubmit={canSubmit} submitting={submitting} mobile={mobile} onSubmit={onSubmit} />
      </div>
    </div>
  );
}
