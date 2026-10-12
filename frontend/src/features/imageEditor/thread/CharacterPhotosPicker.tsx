// Окно «Какие фото взять» (ADR-027, шаг 18): сетка всех фото лица персонажа, первое выбранное
// важнее остальных, лимит — из DTO персонажа. «Перерисовать» — новый запуск с тем же промптом
// и той же базой, что у запуска версии, плюс явный список фото; исходная версия остаётся.
// Вид — спека docs/mockups/character-smart-photos-spec.md §4, тексты дословно из заметки.

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import {
  Badge, Button, C, FS, MODAL_W, R, SHADOW, SP,
  Modal, ModalActions, Notice, getChatContextState, showToast,
  type MediaFormatWish,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCharacter, type ImageEditCharacterPhoto } from '../api';
import { reloadCharacters } from '../characters/useCharacters';
import { labelCaption, type PhotoLabelState } from '../characters/PhotoLabelChips';
import { buildQuoteBody } from './quoteBody';
import { CPS, counterText, emptySelectionText, initialSelection, newRunText, overLimitText, pickLimit, pickerSubtitle, redrawBody, togglePick } from './characterPhotos';
import type { ImageThread, ImageThreadCharacter, ImageThreadLaunch } from './threadsApi';

// Ячейка фото: нажатие добавляет фото в конец порядка, повторное снимает (номера сдвигаются)
function Cell({ photo, url, index, selected, pos, limit, selectedCount, onToggle, firstRef }: {
  photo: ImageEditCharacterPhoto;
  url: string;
  index: number; // номер фото в манифесте, 1-based
  selected: boolean;
  pos: number; // 0 — не выбран
  limit: number;
  selectedCount: number;
  onToggle: () => void;
  firstRef?: (el: HTMLButtonElement | null) => void;
}) {
  const cap = labelCaption({ angle: photo.angle, pitch: photo.pitch, emotion: photo.emotion, labelSource: photo.labelSource } as PhotoLabelState);
  const disabled = !selected && selectedCount >= limit;
  return (
    <button
      type="button"
      ref={firstRef}
      aria-pressed={selected}
      aria-disabled={disabled || undefined}
      aria-label={`Фото ${index}: ${cap.text}${selected ? `, ${pos}-е по порядку` : ''}`}
      title={disabled ? overLimitText(limit) : undefined}
      onClick={disabled ? undefined : onToggle}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs, padding: 0, border: 'none',
        background: 'transparent', textAlign: 'left', cursor: disabled ? 'not-allowed' : 'pointer', minWidth: 0,
      }}
    >
      <span style={{
        position: 'relative', aspectRatio: 1, borderRadius: R.lg, overflow: 'hidden',
        border: selected ? `2px solid ${C.accent}` : `1px solid ${C.border}`,
        boxShadow: selected ? SHADOW.focus : 'none',
        background: C.bgInset, opacity: disabled ? 0.45 : 1,
      }}>
        <img src={url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
        {selected && pos > 0 && (
          <Badge size="xs" style={{
            position: 'absolute', top: SP.xs, left: SP.xs,
            background: C.accent, color: C.onAccent, minWidth: 18, justifyContent: 'center',
          }}>{pos}</Badge>
        )}
        {photo.primary && (
          <Badge size="xs" tone="neutral" style={{ position: 'absolute', bottom: SP.xs, left: SP.xs }}>
            {CPS.main}
          </Badge>
        )}
      </span>
      <span style={{
        fontSize: FS.xs, lineHeight: 1.3, overflowWrap: 'anywhere', color: cap.color,
        fontStyle: cap.italic ? 'italic' : undefined,
        display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
      }}>{cap.text}</span>
    </button>
  );
}

export function CharacterPhotosPicker({
  projectId, sessionId, thread, character, card, launch,
  modelLabel, provider, modelId, maxCharacterPhotos, format, onClose, onLaunching,
}: {
  projectId: string;
  sessionId: string;
  thread: ImageThread;
  character: ImageThreadCharacter;
  card: ImageEditCharacter;
  launch: ImageThreadLaunch;
  // Текущий «Чем»: строка «Новый запуск: {модель}» и котировка
  modelLabel: string | null;
  provider: string | null;
  modelId: string | null;
  // Лимит фото модели «Чем» (maxCharacterPhotos): local — 1, unset — лимит персонажа
  maxCharacterPhotos?: number | null;
  // Желание формата проекта (ADR-025) — в котировку явно
  format: MediaFormatWish | null;
  onClose: () => void;
  onLaunching: (b: boolean) => void;
}) {
  const limit = pickLimit(card, maxCharacterPhotos);
  const [order, setOrder] = useState<string[]>(() => initialSelection(character.photos, card.photos));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const firstRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => { firstRef.current?.focus(); }, []);

  // Карточку перечитали (например, после character_photos_invalid): пропавшие фото снимаем из выбора
  const filesKey = card.photos.map(p => p.file).join('\n');
  useEffect(() => {
    setOrder(cur => cur.filter(f => card.photos.some(p => p.file === f)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filesKey]);

  const full = order.length >= limit;

  const toggle = (file: string) => {
    if (full && !order.includes(file)) return;
    setOrder(cur => togglePick(cur, file, limit));
  };

  // «Перерисовать»: свежая котировка, затем запуск. Ошибка character_photos_invalid — плашкой,
  // окно остаётся; прочий отказ — тостом (как любой запуск), окно тоже остаётся
  const redraw = async () => {
    if (!provider || !modelId) {
      showToast('Рисовать нечем: администратор не настроил поставщиков картинок', '', 'error');
      return;
    }
    setBusy(true);
    onLaunching(true);
    try {
      const contextRevision = getChatContextState(sessionId).revision;
      const hasBase = !!launch.baseVersionId;
      const quote = await imageEditorApi().quote(projectId, buildQuoteBody({
        provider, model: modelId, mode: 'auto', op: hasBase ? 'edit' : 'generate',
        count: 1, hasImage: hasBase, marks: [], withMask: false, removal: false,
        references: 0, hasCharacter: true, size: null,
        context: { sessionId, contextRevision },
        format,
      }));
      await imageEditorApi().startJob(projectId, {
        ...redrawBody({
          prompt: launch.prompt ?? '',
          characterSlug: character.slug,
          characterPhotos: order,
          threadId: thread.id,
          sessionId,
          versionId: launch.baseVersionId,
          baseStepId: launch.baseStepId,
          contextRevision,
        }),
        quoteId: quote.quoteId,
      });
      onClose();
    } catch (e) {
      const err = e as { status?: number; body?: { code?: string; message?: string } };
      if (err?.body?.code === 'character_photos_invalid') {
        setError(CPS.invalid);
        void reloadCharacters(projectId);
      } else {
        showToast(`Генерация не запущена: ${err?.body?.message ?? (e as Error).message}`, '', 'error');
      }
    } finally {
      setBusy(false);
      onLaunching(false);
    }
  };

  return (
    <Modal
      title={CPS.pickerTitle}
      subtitle={pickerSubtitle(limit)}
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        // title на обёртке: «Перерисовать» без выбора — подсказка, почему кнопка не активна
        <span title={order.length === 0 ? emptySelectionText : undefined} style={{ display: 'contents' }}>
          <ModalActions
            cancelLabel={CPS.cancel}
            confirmLabel={CPS.redraw}
            confirmDisabled={order.length === 0 || busy}
            loading={busy}
            onCancel={onClose}
            onConfirm={() => { void redraw(); }}
          />
        </span>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
        {error && <Notice tone="danger" icon={AlertTriangle}>{error}</Notice>}
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minHeight: 24 }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{counterText(order.length, limit)}</span>
          {order.length > 0 && (
            <span style={{ marginLeft: 'auto' }}>
              <Button variant="ghost" size="xs" onClick={() => setOrder([])}>{CPS.reset}</Button>
            </span>
          )}
        </div>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(72px, 1fr))', gap: SP.sm }}>
          {card.photos.map((p, i) => {
            const pos = order.indexOf(p.file);
            return (
              <Cell
                key={p.file}
                photo={p}
                index={i + 1}
                url={imageEditorApi().characterPhotoUrl(projectId, card.slug, p.file)}
                selected={pos >= 0}
                pos={pos + 1}
                limit={limit}
                selectedCount={order.length}
                onToggle={() => toggle(p.file)}
                firstRef={i === 0 ? el => { firstRef.current = el; } : undefined}
              />
            );
          })}
        </div>
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4, overflowWrap: 'anywhere' }}>
          {modelLabel ? newRunText(modelLabel) : 'Новый запуск · исходная версия останется'}
        </div>
      </div>
    </Modal>
  );
}
