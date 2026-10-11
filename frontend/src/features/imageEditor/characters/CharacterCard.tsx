// Карточка персонажа новой панели (спека character-panel-final-spec §5): навигация и меню «⋯»,
// коллаж фото, имя и фраза о состоянии, шкала, главная кнопка, тихие чипы и шторки.
// Хуки обучения, версий и числа фото для обучения живут здесь — остальным уходят пропсами.

import { useCallback, useRef, useState, type ReactNode } from 'react';
import { Check, Image as ImageIcon, Layers, MoreHorizontal, Pencil, SlidersHorizontal, Trash2 } from 'lucide-react';
import {
  BackButton, Badge, Chip, ConfirmDialog, Dot, IconButton, Menu, MenuItem, MenuSep, useIsMobile,
  C, FONT, FS, ICON_SIZE, ICON_STROKE, R, SP,
} from 'aihome_shell/kit';
import type { ImageEditCharacter, ImageEditorApi } from '../api';
import { characterCardView, type CardSay } from './characterCardView';
import {
  chipKeys, collageLayout, deleteConfirmTexts, menuKeys, orderedPhotos, photosNeedDot, sheetForChip, sheetForMenu,
  type CardChipKey, type CardMenuKey, type CardSheet,
} from './characterCardSheets';
import { CHIPS, MENU, METER, SAY, type SayPart } from './characterTexts';
import { CharacterHero } from './CharacterHero';
import { CharacterNameSheet } from './CharacterNameSheet';
import { CharacterPhotosSheet } from './CharacterPhotosSheet';
import { CharacterTuneSheet } from './CharacterTuneSheet';
import { CharacterVersionsSheet } from './CharacterVersionsSheet';
import { LikenessMeter } from './LikenessMeter';
import { loraLabelParts, ownLoraLabel } from './loraName';
import { LoraLabel } from './LoraLabel';
import { putCharacter, reloadCharacters } from './useCharacters';
import { useCharacterTrain } from './useCharacterTrain';
import { useLoraList } from './useLoraList';
import { useLoraVersions } from './useLoraVersions';
import { useTrainPhotoCount } from './useTrainPhotoCount';
import { TRAIN_TEXT } from './loraTrainTexts';
import { confirmParts } from './loraTrainView';

const ico = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE } as const;
const MENU_ICON = {
  name: <Pencil size={15} strokeWidth={ICON_STROKE} />,
  photos: <ImageIcon size={15} strokeWidth={ICON_STROKE} />,
  versions: <Layers size={15} strokeWidth={ICON_STROKE} />,
  tune: <SlidersHorizontal size={15} strokeWidth={ICON_STROKE} />,
  remove: <Trash2 size={15} strokeWidth={ICON_STROKE} />,
} as const;
const MENU_LABEL: Record<CardMenuKey, string> = {
  name: MENU.nameDesc, photos: MENU.photos, versions: MENU.versions, tune: MENU.expert, remove: MENU.remove,
};

function sayText(say: CardSay): ReactNode {
  switch (say.key) {
    case 'needTrain': return renderSayParts(SAY.needTrain(say.n));
    case 'ready': return renderSayParts(SAY.ready());
    case 'readyNoTrain': return renderSayParts(SAY.readyNoTrain());
    case 'trained': return SAY.trained(say.title);
    default: return SAY[say.key];
  }
}

// Жирные части фразы — <b> цветом C.textHeading, остальное — обычный текст
function renderSayParts(parts: readonly SayPart[]): ReactNode {
  return parts.map((p, i) =>
    p.bold ? <b key={i} style={{ color: C.textHeading }}>{p.text}</b> : <span key={i}>{p.text}</span>);
}

export function CharacterCard({ api, projectId, character, contextSessionId, onBack, onDeleted }: {
  api: ImageEditorApi;
  projectId: string;
  character: ImageEditCharacter;
  // null — чат не выбран
  contextSessionId: string | null;
  onBack: () => void;
  onDeleted: (slug: string) => void;
}) {
  const isMobile = useIsMobile();
  const slug = character.slug;
  const versions = useLoraVersions(projectId, slug, c => {
    if (c) putCharacter(projectId, c); else void reloadCharacters(projectId);
  });
  const { list: loraList } = useLoraList(api, projectId);
  const trainCount = useTrainPhotoCount(projectId, slug);
  const train = useCharacterTrain(projectId, slug, trainCount.count, versions);
  const items = versions.data?.items ?? [];
  const onChanged = useCallback((c: ImageEditCharacter) => putCharacter(projectId, c), [projectId]);

  const [menu, setMenu] = useState(false);
  const [sheet, setSheet] = useState<{ kind: CardSheet; focus?: 'face' | 'train' } | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  // «Обучить заново» из шторки «Версии»: то же подтверждение, что у главной кнопки
  const [confirmRestart, setConfirmRestart] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Кто вызвал шторку — туда возвращаем фокус при закрытии
  const opener = useRef<HTMLElement | null>(null);
  const remember = () => { opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null; };
  // Из меню «Ещё» вызывающий — его кнопка (запомнена при открытии меню), а не уже исчезающий пункт
  const open = (kind: CardSheet, focus?: 'face' | 'train') => {
    if (!menu) remember();
    setSheet({ kind, focus });
  };
  const restoreFocus = () => {
    const el = opener.current;
    opener.current = null;
    if (el?.isConnected) requestAnimationFrame(() => el.focus());
  };
  const closeSheet = () => { setSheet(null); restoreFocus(); };
  const closeRestart = () => { setConfirmRestart(false); restoreFocus(); };
  const closeDelete = () => { setConfirmDelete(false); restoreFocus(); };

  const cv = characterCardView({
    photos: character.photos.length, train: trainCount.count, view: train.view, versions: items, own: character.local?.lora,
  });
  const failedAllRejected = train.view.kind === 'failed' && train.view.allRejected;
  const trainSum = (trainCount.count ?? 0) + character.photos.length;
  const need = photosNeedDot(trainCount.count, failedAllRejected);

  const photos = orderedPhotos(character.photos);
  const { shown, extra } = collageLayout(photos.length);
  const url = (file: string) => api.characterPhotoUrl(projectId, slug, file);
  const imgStyle = { width: '100%', height: '100%', objectFit: 'cover', display: 'block', borderRadius: R.lg, border: `1px solid ${C.border}` } as const;

  const onMenu = (key: CardMenuKey) => {
    setMenu(false);
    const s = sheetForMenu(key);
    if (s) open(s); else setConfirmDelete(true);
  };
  const remove = async () => {
    try {
      await api.deleteCharacter(projectId, slug);
      onDeleted(slug);
    } catch (e) {
      setError((e as Error).message);
      closeDelete();
    }
  };
  const restart = confirmParts(!!character.local?.lora);
  const del = deleteConfirmTexts(character.name, character.path);

  const ownLabel = character.local?.lora && cv.state === 4
    ? loraLabelParts(ownLoraLabel(character.local.lora, versions.data?.own?.trainedAt, loraList)) : null;

  const chip = (key: CardChipKey) => (
    <Chip key={key} variant="soft" touch={isMobile} keyboard onClick={() => open(sheetForChip(key))}
      title={key === 'photos' && need ? CHIPS.photosNeed : undefined}>
      {key === 'photos' && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
          <ImageIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          {CHIPS.photos} <b style={{ color: C.textPrimary }}>{trainSum}</b>
          {need && <Dot color={C.warning} />}
        </span>
      )}
      {key === 'versions' && <span>{CHIPS.versions} <b style={{ color: C.textPrimary }}>{items.length}</b></span>}
      {key === 'tune' && CHIPS.expert}
    </Chip>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, padding: SP.md, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
        <BackButton onClick={onBack} title="К списку персонажей">
          <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Все персонажи</span>
        </BackButton>
        <div style={{ position: 'relative' }}>
          <IconButton size="sm" ariaLabel="Ещё" title="Ещё" active={menu} onClick={() => { if (!menu) remember(); setMenu(v => !v); }}>
            <MoreHorizontal {...ico} />
          </IconButton>
          {menu && (
            <Menu onClose={() => setMenu(false)} align="right" top={36} minWidth={230}>
              {menuKeys(items.length).map(k => (
                <span key={k} style={{ display: 'contents' }}>
                  {k === 'remove' && <MenuSep />}
                  <MenuItem icon={MENU_ICON[k]} label={MENU_LABEL[k]} danger={k === 'remove'} onClick={() => onMenu(k)} />
                </span>
              ))}
            </Menu>
          )}
        </div>
      </div>

      {photos.length > 0 && (
        <button type="button" aria-label={CHIPS.photos} onClick={() => open('photos')} style={{
          all: 'unset', cursor: 'pointer', display: 'grid', gap: SP.xs,
          gridTemplateColumns: shown === 1 ? '1fr' : '2fr 1fr',
          ...(isMobile ? { height: 168 } : shown === 1 ? { maxHeight: 240 } : {}),
          borderRadius: R.lg,
        }}>
          <img src={url(photos[0].file)} alt="" style={{
            ...imgStyle, ...(isMobile ? {} : { aspectRatio: '1' }),
            ...(shown === 1 && !isMobile ? { maxHeight: 240 } : {}),
            gridRow: shown === 3 ? 'span 2' : undefined,
          }} />
          {shown >= 2 && <img src={url(photos[1].file)} alt="" style={imgStyle} />}
          {shown === 3 && (
            <div style={{ position: 'relative', minHeight: 0 }}>
              <img src={url(photos[2].file)} alt="" style={imgStyle} />
              {extra > 0 && (
                <span style={{
                  position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                  background: C.mediaScrim, color: C.onDark, borderRadius: R.lg, fontSize: FS.md, fontWeight: 600,
                }}>+{extra}</span>
              )}
            </div>
          )}
        </button>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.h2, color: C.textHeading, lineHeight: 1.2, overflowWrap: 'anywhere' }}>
          {character.name}
        </div>
        <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs }}>
          {cv.badge && (
            <Badge size="sm" tone={cv.badge.tone} icon={cv.badge.tone === 'success' ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : undefined}>
              {cv.badge.text === 'done' ? METER.badgeDone : METER.badgeFailed}
            </Badge>
          )}
          <span style={{ fontSize: FS.md, color: C.textPrimary, lineHeight: 1.45 }}>{sayText(cv.say)}</span>
        </div>
        {ownLabel && <LoraLabel parts={ownLabel} />}
      </div>

      <LikenessMeter stops={cv.stops} />

      <CharacterHero action={cv.action} train={train} versions={versions} slug={slug} projectId={projectId}
        hasConnected={!!character.local?.lora} contextSessionId={contextSessionId}
        onOpenPhotos={s => open('photos', s)} onOpenTune={() => open('tune')} />

      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>{chipKeys(items.length).map(chip)}</div>

      {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}

      {sheet?.kind === 'name' && (
        <CharacterNameSheet api={api} projectId={projectId} character={character} onChanged={onChanged} onClose={closeSheet} />
      )}
      {sheet?.kind === 'photos' && (
        <CharacterPhotosSheet api={api} projectId={projectId} character={character} focus={sheet.focus}
          onChanged={onChanged} onCount={trainCount.set} onClose={closeSheet} />
      )}
      {sheet?.kind === 'versions' && (
        <CharacterVersionsSheet loraList={loraList} versions={versions} onClose={closeSheet}
          onRestart={() => { setSheet(null); setConfirmRestart(true); }} />
      )}
      {sheet?.kind === 'tune' && (
        <CharacterTuneSheet api={api} projectId={projectId} character={character} loraList={loraList}
          trigger={train.trigger} edited={train.edited} setTrigger={train.setTrigger}
          onChanged={onChanged} onClose={closeSheet} />
      )}
      {confirmRestart && (
        <ConfirmDialog title={TRAIN_TEXT.confirmTitle}
          subtitle={<>{restart.before}<strong>{restart.bold}</strong>{restart.after}{restart.note && <><br /><br />{restart.note}</>}</>}
          confirmLabel={TRAIN_TEXT.confirmOk} cancelLabel={TRAIN_TEXT.confirmCancel}
          onCancel={closeRestart}
          onConfirm={async () => { await train.training.start?.(train.edited ?? undefined); closeRestart(); }} />
      )}
      {confirmDelete && (
        <ConfirmDialog title={del.title} subtitle={del.subtitle} confirmLabel={del.confirmLabel} confirmVariant="danger"
          wrapTitle onConfirm={remove} onCancel={closeDelete} />
      )}
    </div>
  );
}
