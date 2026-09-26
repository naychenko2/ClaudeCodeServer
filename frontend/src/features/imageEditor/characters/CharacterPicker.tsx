// «Персонажи проекта» в панели запроса (макет image-editor-v1, экраны 9–10): список с
// подключением одним кликом, «⋯» открывает карточку, пустой список зовёт создать.
// Подключённый персонаж висит чипом над полем запроса и уходит в генерацию.

import { useCallback, useEffect, useState } from 'react';
import { MoreHorizontal, Plus, User, UserPlus, X } from 'lucide-react';
import { Button, IconButton } from '../../ui';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { C, FS, R, SP } from '../../../lib/design';
import { showToast } from '../../../lib/toast';
import type { ImageEditCharacter, ImageEditorApi } from '../../../api/imageEditor';
import { CharacterDialog } from './CharacterDialog';

const ic = (I: typeof User, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export interface CharactersState {
  list: ImageEditCharacter[];
  error: string | null;
  // Подключённый к генерации персонаж
  active: ImageEditCharacter | null;
  setActive: (slug: string | null) => void;
  upsert: (c: ImageEditCharacter) => void;
  remove: (slug: string) => void;
}

export function useCharacters(api: ImageEditorApi, projectId: string): CharactersState {
  const [list, setList] = useState<ImageEditCharacter[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [activeSlug, setActiveSlug] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.listCharacters(projectId)
      .then(l => { if (alive) setList(l); })
      .catch((e: Error) => { if (alive) setError(e.message); });
    return () => { alive = false; };
  }, [api, projectId]);

  const upsert = useCallback((c: ImageEditCharacter) =>
    setList(l => (l.some(x => x.slug === c.slug) ? l.map(x => (x.slug === c.slug ? c : x)) : [...l, c])), []);
  const remove = useCallback((slug: string) => {
    setList(l => l.filter(x => x.slug !== slug));
    setActiveSlug(s => (s === slug ? null : s));
  }, []);

  return { list, error, active: list.find(c => c.slug === activeSlug) ?? null, setActive: setActiveSlug, upsert, remove };
}

function Avatar({ url, size }: { url: string | null; size: number }) {
  return (
    <span style={{
      width: size, height: size, flex: `0 0 ${size}px`, borderRadius: R.full, overflow: 'hidden',
      border: `1px solid ${C.border}`, background: C.bgInset, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      color: C.textMuted,
    }}>
      {url ? <img src={url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} /> : ic(User, ICON_SIZE.xs)}
    </span>
  );
}

const primaryPhoto = (c: ImageEditCharacter) => (c.photos.find(p => p.primary) ?? c.photos[0])?.file ?? null;

// Диалоги персонажа держит один владелец — и список, и чип открывают одну и ту же карточку
export function useCharacterDialogs(api: ImageEditorApi, projectId: string, chars: CharactersState) {
  const [open, setOpen] = useState<{ slug: string | null } | null>(null);
  const editing = open?.slug ? chars.list.find(c => c.slug === open.slug) ?? null : null;
  const dialog = open && (open.slug === null || editing) ? (
    <CharacterDialog key={open.slug ?? 'new'} api={api} projectId={projectId} character={editing}
      connected={!!editing && chars.active?.slug === editing.slug}
      onToggle={editing ? () => { chars.setActive(chars.active?.slug === editing.slug ? null : editing.slug); setOpen(null); } : undefined}
      onSaved={(c, created) => {
        chars.upsert(c);
        setOpen(null);
        if (created) {
          chars.setActive(c.slug);
          showToast(`Персонаж сохранён в проект: ${c.path}/`, '', 'info');
        }
      }}
      onDeleted={slug => { chars.remove(slug); setOpen(null); }}
      onClose={() => setOpen(null)} />
  ) : null;
  return {
    dialog,
    openNew: () => setOpen({ slug: null }),
    openCard: (slug: string) => setOpen({ slug }),
  };
}

// Чип подключённого персонажа над полем запроса
export function CharacterChip({ api, projectId, character, disabled, onOpen, onOff }: {
  api: ImageEditorApi;
  projectId: string;
  character: ImageEditCharacter;
  disabled?: boolean;
  onOpen: () => void;
  onOff: () => void;
}) {
  const photo = primaryPhoto(character);
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xxs, maxWidth: '100%', marginBottom: SP.sm,
      padding: `0 ${SP.xxs}px`, borderRadius: R.max, border: `1px solid ${C.accent}`, background: C.accentLight,
    }}>
      <Button variant="ghostFilled" size="xs" pill onClick={onOpen} title="Карточка персонажа"
        style={{ border: 'none', background: 'transparent', color: C.accent, gap: SP.xs, minWidth: 0 }}>
        <Avatar url={photo ? api.characterPhotoUrl(projectId, character.slug, photo) : null} size={18} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{character.name}</span>
      </Button>
      <IconButton size="xs" tone="accent" title="Отключить персонажа" ariaLabel="Отключить персонажа" disabled={disabled} onClick={onOff}>
        {ic(X, ICON_SIZE.xs)}
      </IconButton>
    </div>
  );
}

// Секция «Персонажи проекта»
export function CharacterSection({ api, projectId, chars, disabled, onNew, onCard, bare }: {
  api: ImageEditorApi;
  projectId: string;
  chars: CharactersState;
  disabled?: boolean;
  onNew: () => void;
  onCard: (slug: string) => void;
  // Без своего заголовка — внутри секции «Персонажи» левой панели
  bare?: boolean;
}) {
  return (
    <div>
      {(!bare || chars.list.length > 0) && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
          <span style={{ flex: 1, fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em' }}>
            {bare ? '' : 'Персонажи проекта'}
          </span>
          {chars.list.length > 0 && (
            <Button variant="ghost" size="xs" leftIcon={ic(UserPlus, ICON_SIZE.xs)} disabled={disabled} onClick={onNew}>Персонаж</Button>
          )}
        </div>
      )}
      {chars.error && <div style={{ fontSize: FS.sm, color: C.dangerText, marginBottom: SP.sm }}>{chars.error}</div>}
      {chars.list.length ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {chars.list.map(c => {
            const on = chars.active?.slug === c.slug;
            const photo = primaryPhoto(c);
            return (
              <div key={c.slug} style={{
                display: 'flex', alignItems: 'center', gap: SP.xxs, padding: SP.xxs, borderRadius: R.lg,
                border: `1px solid ${on ? C.accent : C.border}`, background: on ? C.accentLight : C.bgWhite,
              }}>
                <Button variant="ghost" size="sm" disabled={disabled} onClick={() => chars.setActive(on ? null : c.slug)}
                  style={{ flex: 1, minWidth: 0, justifyContent: 'flex-start', border: 'none', background: 'transparent', color: C.textPrimary, padding: SP.xs }}>
                  <Avatar url={photo ? api.characterPhotoUrl(projectId, c.slug, photo) : null} size={32} />
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 400 }}>{c.name}</span>
                  <span style={{ marginLeft: 'auto', fontSize: FS.xs, whiteSpace: 'nowrap', color: on ? C.accent : C.textMuted, fontWeight: on ? 600 : 400 }}>
                    {on ? 'в генерации' : 'подключить'}
                  </span>
                </Button>
                <IconButton size="sm" title="Карточка персонажа" ariaLabel="Карточка персонажа" onClick={() => onCard(c.slug)}>
                  {ic(MoreHorizontal)}
                </IconButton>
              </div>
            );
          })}
        </div>
      ) : (
        <div style={{
          display: 'flex', gap: SP.md, alignItems: 'center', border: `1.5px dashed ${C.dashed}`, borderRadius: R.lg,
          padding: SP.md, fontSize: FS.sm, lineHeight: 1.4, color: C.textSecondary,
        }}>
          <span style={{
            width: 32, height: 32, flex: '0 0 32px', borderRadius: R.lg, background: C.accentLight, color: C.accent,
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          }}>{ic(User)}</span>
          <span style={{ flex: 1, minWidth: 0 }}>Добавьте персонажа — лицо сохранится во всех сценах</span>
          <Button variant="ghost" size="sm" leftIcon={ic(Plus, ICON_SIZE.xs)} disabled={disabled} onClick={onNew}>Персонаж</Button>
        </div>
      )}
    </div>
  );
}
