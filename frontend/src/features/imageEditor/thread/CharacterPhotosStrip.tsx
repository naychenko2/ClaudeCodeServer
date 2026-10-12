// Блок «Фото персонажа» в карточке версии (ADR-027, шаг 18): миниатюры фото, которые ушли
// модели, пометка, как они выбраны, и кнопка «Заменить». Тихая утопленная плашка (C.bgInset)
// между подписью карточки и её кнопками: «из чего рисовали» отделено от «что получилось».
// Вид — спека docs/mockups/character-smart-photos-spec.md, тексты дословно из заметки.

import { useState } from 'react';
import { Hand, ImageOff, ImagePlus, ListOrdered, Replace, Sparkles } from 'lucide-react';
import {
  Badge, Button, C, FS, R, SP, ICON_SIZE, ICON_STROKE, useIsMobile,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCharacterPhoto } from '../api';
import { wishForQuote } from '../context/formatChoice';
import { useCatalog } from './catalog';
import { usePrefs, modeSettings } from './prefs';
import { launchMode, resolveModel } from './useThreadLaunch';
import { useCharacters } from '../characters/useCharacters';
import { labelCaption, type PhotoLabelState } from '../characters/PhotoLabelChips';
import { LABELS } from '../characters/characterTexts';
import { launchOf } from './model';
import type { ImageThread, ImageThreadCharacter, ImageThreadVersion } from './threadsApi';
import { CPS, overLimitText, pickBadge, pickLimit, sceneLine, showReplace } from './characterPhotos';
import { CharacterPhotosPicker } from './CharacterPhotosPicker';

const ic = (I: typeof Replace, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Размер миниатюры (спека §3.2)
const THUMB = 40;

// Иконка пометки по pick
const PICK_ICON = { auto: Sparkles, manual: Hand, fallback: ListOrdered } as const;

// Миниатюра: не кнопка, не нажимается (смотреть фото крупно — дело панели персонажа)
function Thumb({ url, title, gone }: { url: string | null; title: string; gone?: boolean }) {
  const frame = {
    width: THUMB, height: THUMB, flexShrink: 0, borderRadius: R.md,
    border: `1px solid ${C.borderLight}`, background: C.bgCard, overflow: 'hidden',
    display: 'flex', alignItems: 'center', justifyContent: 'center',
  } as const;
  if (gone) {
    return <span title={title} style={{ ...frame, color: C.textMuted }}>{ic(ImageOff, ICON_SIZE.sm)}</span>;
  }
  if (!url) {
    // Карточка персонажа ещё грузится: заглушка того же размера
    return <span className="cc-skel" style={frame} />;
  }
  return (
    <span title={title} style={frame}>
      <img src={url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
    </span>
  );
}

// Подпись-подсказка миниатюры: метки фото (та же функция, что в панели персонажа);
// меток нет — номер фото
function thumbTitle(photo: ImageEditCharacterPhoto, n: number): string {
  const cap = labelCaption({ angle: photo.angle, pitch: photo.pitch, emotion: photo.emotion, labelSource: photo.labelSource } as PhotoLabelState);
  if (cap.text === LABELS.none || cap.text === LABELS.pending) return `Фото ${n}`;
  return cap.text;
}

export function CharacterPhotosStrip({ projectId, sessionId, thread, version, character }: {
  projectId: string; sessionId: string; thread: ImageThread; version: ImageThreadVersion; character: ImageThreadCharacter;
}) {
  const mobile = useIsMobile();
  const [pickerOpen, setPickerOpen] = useState(false);
  const [launching, setLaunching] = useState(false);
  const { list } = useCharacters(projectId);
  // Текущий «Чем»: модель строки «Новый запуск: …» и котировка окна
  const catalog = useCatalog(projectId);
  const prefs = usePrefs(projectId);
  const card = list?.find(c => c.slug === character.slug) ?? null;
  const launch = version.jobId ? launchOf(thread, version.jobId) : null;
  if (!launch) return null;

  const settings = modeSettings(launchMode(sessionId, thread), prefs, thread.settings);
  const { pv, m } = resolveModel(catalog, settings);

  const api = imageEditorApi();
  const badge = pickBadge(character.pick);
  const scene = sceneLine(character.scene);
  const single = card ? card.photos.length === 1 : false;
  // Персонажа больше нет: список персонажей загружен, а его в нём нет
  const gone = list !== null && card === null;
  const name = card?.name;

  // Пометку не показываем, когда у персонажа одно фото: она тогда ничего не говорит
  const showBadge = !single;
  // «Заменить»: нет при одном фото (тогда подсказка), при удалённом персонаже и без промпта
  const canReplace = showReplace(launch, card) && !gone;

  // Клик по кнопке не выбирает карточку: у Shell свой isCardPick, кнопка — интерактив
  const openPicker = () => { if (canReplace && !pickerOpen && !launching) setPickerOpen(true); };

  return (
    <div style={{
      background: C.bgInset, borderRadius: R.lg, padding: SP.sm,
      display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', minWidth: 0 }}>
        <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{CPS.title}</span>
        {name && (
          <span title={name} style={{
            fontSize: FS.sm, color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>· {name}</span>
        )}
        {showBadge && (
          <Badge size="xs" tone={badge.tone} icon={ic(PICK_ICON[character.pick])} title={badge.title}>
            {badge.text}
          </Badge>
        )}
      </div>
      {character.pick === 'auto' && scene && (
        <div style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.4, overflowWrap: 'anywhere' }}>{scene}</div>
      )}
      {gone ? (
        <div style={{ fontSize: FS.xs, color: C.textMuted }}>{CPS.characterGone}</div>
      ) : (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
          {character.photos.slice(0, 3).map((file, i) => {
            const photo = card?.photos.find(p => p.file === file);
            // Номер фото — в манифесте персонажа; пока карточка грузится — по порядку запуска
            const n = card ? card.photos.findIndex(p => p.file === file) + 1 : i + 1;
            return (
              <Thumb key={file}
                url={photo ? api.characterPhotoUrl(projectId, character.slug, file) : null}
                title={photo ? thumbTitle(photo, n) : (card ? CPS.photoGone : `Фото ${n}`)}
                gone={card !== null && !photo} />
            );
          })}
          {canReplace && (
            <span style={{ marginLeft: 'auto' }}>
              <Button size={mobile ? 'sm' : 'xs'} variant="ghost" leftIcon={ic(Replace)}
                title={overLimitText(pickLimit(card, m?.caps?.maxCharacterPhotos))}
                onClick={openPicker} disabled={launching}>
                Заменить
              </Button>
            </span>
          )}
        </div>
      )}
      {single && !gone && (
        <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start' }}>
          <span style={{ color: C.textMuted, marginTop: 1 }}>{ic(ImagePlus)}</span>
          <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>{CPS.singleHint}</span>
        </div>
      )}
      {pickerOpen && card && (
        <CharacterPhotosPicker
          projectId={projectId} sessionId={sessionId} thread={thread}
          character={character} card={card} launch={launch}
          modelLabel={m?.label ?? null} provider={pv?.key ?? null} modelId={m?.id ?? null}
          maxCharacterPhotos={m?.caps?.maxCharacterPhotos}
          format={wishForQuote(projectId)}
          onClose={() => setPickerOpen(false)}
          onLaunching={setLaunching}
        />
      )}
    </div>
  );
}
