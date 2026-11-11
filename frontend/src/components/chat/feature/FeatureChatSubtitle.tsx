import type { CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Dot, PressableArea } from '../../ui';
import { FeatureGlyph } from './FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { chatCardStateWord, type ChatCardGlyph, type FeatureChatCardModel } from './featureChatCardModel';

// FP-C1 (макет chat-feature-fit-v1, вариант А, §5 «Строка фичи (1280)» / «Слово состояния» /
// «360: строка»): строка-подзаголовок шапки чата — глиф · название · «6 из 44 · ≈ 2 д» · слово
// состояния. Своей полосы, фона и рамки нет: это строка текста, а не панель (цель шага).
// В шапку встраивает FP-C3; поповер — FP-C2: сюда приходят только onOpen/expanded, и они
// честно лежат в aria (aria-haspopup="dialog", aria-expanded), чтобы поповер не додумывал их.
// Источник правды — модель featureChatCardModel (FS-FCm) и её слово chatCardStateWord:
// компонент ничего не выводит сам, тон — по слову модели, точка на 360 — по глифу модели.
//
// 1280: строка-кнопка на PressableArea (feedback = hover-подложка C.bgSelected и кольцо
// SHADOW.focus, класс .cc-pressable в index.css — тот же отклик, что у строк стены).
// 360: строка 11 px — точка тона 6 px + «{count} · {eta}», а при тревоге (warn/danger) —
// слово вместо счёта; весь блок minHeight 40 (цель на тач).

// Тон модели → форма общего глифа (та же карта, что FeatureChatCard.tsx:40; дублируется —
// карточку этого шага не трогаем): info → «run», warning → «wait», danger → «stopped»,
// success → «done».
const GLYPH_KIND: Record<ChatCardGlyph, GlyphKind> = {
  info: 'run', warning: 'wait', danger: 'stopped', success: 'done',
};

// Цвет точки 360 (макет DOT): по глифу состояния — те же цвета, что у глифа на 1280.
const DOT_COLOR: Record<ChatCardGlyph, string> = {
  info: C.info, warning: C.warning, danger: C.danger, success: C.success,
};

// Цвет слова (макет §5 «Слово состояния»): тревога — 600, спокойное — обычный вес.
const WORD_STYLE: Record<'muted' | 'warn' | 'danger' | 'success', CSSProperties> = {
  muted: { color: C.textSecondary },
  warn: { color: C.warningText, fontWeight: 600 },
  danger: { color: C.dangerText, fontWeight: 600 },
  success: { color: C.successText },
};

export function FeatureChatSubtitle({ model, isMobile, onOpen, expanded }: {
  model: FeatureChatCardModel;
  isMobile: boolean;
  onOpen: () => void;
  expanded: boolean;
}) {
  const word = chatCardStateWord(model);

  // Число mono-счёта — жирным (тот же приём, что FeatureChatCard.tsx:152), остальное —
  // цвет строки; «часы стоят» — C.warningText по признаку модели (etaPaused).
  const countParts = model.count ? /^(\d+)([\s\S]*)$/.exec(model.count) : null;
  const countBlock = (model.count || model.eta) ? (
    <span data-testid="feature-subtitle-count" style={{ flexShrink: 0, fontFamily: FONT.mono, whiteSpace: 'nowrap' }}>
      {model.count && (countParts
        ? <><b style={{ fontWeight: 700, color: C.textHeading }}>{countParts[1]}</b>{countParts[2]}</>
        : model.count)}
      {model.eta ? <span style={model.etaPaused ? { color: C.warningText } : undefined}> · {model.eta}</span> : ''}
    </span>
  ) : null;

  return (
    <PressableArea
      onPress={onOpen}
      feedback
      data-testid="feature-chat-subtitle"
      aria-haspopup="dialog"
      aria-expanded={expanded}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs + 2, minWidth: 0, maxWidth: '100%',
        // 1280: поля строки 2px × SP.xs+2 (макет .fline); 360: полей нет, высота — цель 40.
        padding: isMobile ? 0 : `2px ${SP.xs + 2}px`,
        borderRadius: R.sm, fontSize: isMobile ? FS.xs : FS.sm, color: C.textSecondary,
        whiteSpace: 'nowrap',
        ...(isMobile ? { minHeight: 40 } : {}),
      }}
    >
      {isMobile ? (
        <>
          <Dot color={DOT_COLOR[model.glyph]} size={6} />
          {word.tone === 'warn' || word.tone === 'danger' ? (
            <span data-testid="feature-subtitle-word" style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', ...WORD_STYLE[word.tone] }}>
              {word.text}
            </span>
          ) : countBlock}
        </>
      ) : (
        <>
          <span data-testid="feature-subtitle-glyph" style={{ display: 'flex', flexShrink: 0 }}>
            <FeatureGlyph kind={GLYPH_KIND[model.glyph]} size={14} />
          </span>
          {/* Название — гибкий элемент строки (режется первым, как имя файла в шапке),
              полное — в title (макет §5). */}
          <span
            data-testid="feature-subtitle-name" title={model.title}
            style={{ flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', fontWeight: 600, color: C.textPrimary }}
          >
            {model.title}
          </span>
          {countBlock}
          {word.text && (
            <>
              <span aria-hidden style={{ flexShrink: 0, color: C.textMuted }}>·</span>
              <span data-testid="feature-subtitle-word" style={{ ...WORD_STYLE[word.tone] }}>{word.text}</span>
            </>
          )}
        </>
      )}
    </PressableArea>
  );
}
