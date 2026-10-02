// Полоса «Картинки» над композером (реестр composer-strip; прототип полос, вариант C —
// docs/mockups/image-editor-v3-strips-prototype.html). Одна строка высотой как у git-полосы:
// переключатель от хоста, чип «Работаем с: …» (или «Нарисовать новую»), кнопка-сводка
// «поставщик · модель · N вар. · цена ▾» и аватар персонажа. Свёрнутая — строка 30 px со
// сводкой, клик разворачивает. В личном чате вне проекта (область personal) нет персонажа и
// образцов из проекта: их источник — папки проекта.
// Настройки живут в панели «Картинки» (panel/ImagesPanel): от 800 сводка открывает её на
// «Настройках», аватар — на «Персонажах»; пока панель стоит колонкой, сводка подсвечена.
// Уже 800 панели нет места в зоне: ту же панель полоса рисует шторкой каркаса, и автооткрытие
// по выбору картинки (revealWorkspacePanel) поднимает её же. Свёрнутая полоса открывает панель
// кликом по сводке.
// С флагом image-panel-v5 (макет image-panel-v5, вариант 1): зеркало «Создать / Править» после
// заголовка, без выбранной картинки — пунктир «Новая картинка», над полосой — плашка «Вернуть»
// после снятия картинки в «Править»; на телефоне сводка сжата до «▴», свёрнутая строка
// показывает иконку режима.

import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, ChevronUp, Image as ImageIcon, SlidersHorizontal, Sparkles, User, X } from 'lucide-react';
import {
  Button, Chip, IconButton, ReleaseNotice, C, FS, R, SP, ICON_SIZE, REVEAL_PANEL_EVENT, isGenPanelKey, markGenPanelDismissed,
  useGenerationSheet, type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { IMAGES_PANEL, revealWorkspacePanel } from '../characters/panel';
import { ImagesPanel } from '../panel/ImagesPanel';
import { useImagesPanelShown } from '../panel/panelOpen';
import { createDraft, imageReleaseUndo, releaseFocus, undoImageRelease } from '../thread/actions';
import { enterScope, isPersonalScope } from '../scope';
import { focusLabel } from '../thread/model';
import { getFocusedThread, useThreads } from '../thread/threadStore';
import { activeSrc, launchSummaryParts, useThreadLaunch } from '../thread/useThreadLaunch';
import { useCharacter } from './settings/CharacterSection';
import { ic } from './settings/primitives';
import { ImageModeSwitch, MODE_ICON, MODE_LABEL } from './ImageModeSwitch';
import { mobileSummary, settingsToggle } from './settingsToggle';
import { subscribeSheetReveal, takeSheetReveal } from './sheetReveal';
import { stripSummary } from './summary';

function Thumb({ src, round }: { src: string | null; round?: boolean }) {
  if (!src) return null;
  return (
    <span style={{ display: 'inline-flex', width: 20, height: 20, flexShrink: 0, borderRadius: round ? R.full : R.sm, overflow: 'hidden', background: C.bgInset }}>
      <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
    </span>
  );
}

export function ImagesStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  // Режим «Создать / Править» — только с флагом image-panel-v5
  const v5mode = L.imageMode;
  const [drafting, setDrafting] = useState(false);
  const bar = useRef<HTMLDivElement>(null);
  const offer = useSyncExternalStore(imageReleaseUndo.subscribe, imageReleaseUndo.current, imageReleaseUndo.current);
  const undo = v5mode && offer && offer.snapshot.sessionId === sessionId ? offer : null;
  const { current: character, photo } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);
  // Настройки — в панели «Картинки»: от 800 в зоне панелей, уже — шторкой полосы
  const inSheet = useGenerationSheet();
  const [sheet, setSheet] = useState(false);
  const panelShown = useImagesPanelShown();
  const toggle = settingsToggle(inSheet ? 'sheet' : 'panel', inSheet ? sheet : panelShown);
  const openSettings = () => (inSheet ? setSheet(true) : revealWorkspacePanel(IMAGES_PANEL, 'settings'));

  useEffect(() => { setSheet(false); }, [sessionId]);
  // Показ панели извне (автооткрытие по выбору картинки, вкладка «Персонажи») на телефоне
  // поднимает шторку; вкладку панель забирает из того же события сама. Просьба, пришедшая
  // до монтирования полосы или до смены чата, ждёт в буфере (sheetReveal). Эффект стоит
  // после сброса по смене чата — иначе тот гасил бы только что поднятую шторку
  useEffect(() => {
    if (!inSheet) return;
    const check = () => { if (takeSheetReveal(sessionId ?? null)) setSheet(true); };
    check();
    return subscribeSheetReveal(check);
  }, [inSheet, sessionId]);
  // Шторка соседнего раздела (клик по карточке звука) встаёт вместо этой
  useEffect(() => {
    if (!sheet) return;
    const off = (e: Event) => {
      const k = (e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key;
      if (k && k !== IMAGES_PANEL && isGenPanelKey(k)) setSheet(false);
    };
    window.addEventListener(REVEAL_PANEL_EVENT, off);
    return () => window.removeEventListener(REVEAL_PANEL_EVENT, off);
  }, [sheet]);

  const draw = async () => {
    if (!sessionId) return;
    setDrafting(true);
    await createDraft(projectId, sessionId, '');
    setDrafting(false);
  };
  const release = () => { if (sessionId) void releaseFocus(projectId, sessionId, thread); };
  const src = thread ? activeSrc(projectId, thread) : null;
  const parts = {
    focus: thread ? focusLabel(thread, false, personal) : null, provider: L.provider?.label ?? null, model: L.model?.label ?? null,
    count: L.count, price: L.price ?? null, character: character?.name ?? null,
  };
  const title = switcher ?? (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
      {ic(ImageIcon)}Картинки
    </span>
  );

  // ✕ шторки — то же, что закрытие панели на десктопе: дальше в этом чате только руками
  const sheetEl = inSheet && sheet && (
    <ImagesPanel layout="sheet" ctx={{
      projectId: ctx.projectId, sessionId: sessionId ?? null, isMobile: true,
      onClose: () => { markGenPanelDismissed(sessionId, IMAGES_PANEL); setSheet(false); },
    }} />
  );

  if (collapsed) {
    return (
      <>
        <div role="button" tabIndex={0} data-composer-strip="images" data-images-strip="mini" title="Развернуть полосу «Картинки»"
          onClick={() => setCollapsed(false)}
          onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setCollapsed(false); } }}
          style={{
            display: 'flex', alignItems: 'center', gap: SP.sm, height: 30, margin: '4px 0 6px', padding: '0 6px 0 4px',
            boxSizing: 'border-box', minWidth: 0, cursor: 'pointer',
            background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
          }}>
          {title}
          {v5mode && (() => {
            const ModeIcon = MODE_ICON[v5mode];
            return (
              <span data-images-mini-mode={v5mode} title={`Режим: ${MODE_LABEL[v5mode]}`} style={{ display: 'inline-flex', color: C.textSecondary, flexShrink: 0 }}>
                {ic(ModeIcon, ICON_SIZE.sm)}
              </span>
            );
          })()}
          <Thumb src={src} />
          <span data-images-summary="" title={toggle.title}
            onClick={e => { e.stopPropagation(); openSettings(); }}
            style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {stripSummary(parts)}
          </span>
          {thread && (
            <span style={{ display: 'inline-flex' }} onClick={e => e.stopPropagation()}>
              <IconButton size="xs" title="Снять выбор картинки" ariaLabel="Снять выбор картинки" onClick={release}>{ic(X)}</IconButton>
            </span>
          )}
          <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(ChevronDown, ICON_SIZE.sm)}</span>
        </div>
        {sheetEl}
      </>
    );
  }

  const noProviders = L.catalog && !L.catalog.providers.length;

  return (
    <>
      {/* Плашка «Вернуть» встаёт над полосой и не сдвигает поле ввода */}
      {undo && (
        <div style={{ position: 'relative', height: 0 }}>
          <div data-images-release="" style={{ position: 'absolute', left: 0, right: 0, bottom: SP.xs }}>
            <ReleaseNotice text={undo.text} onUndo={() => { void undoImageRelease(); }} isMobile={isMobile} raiseFab />
          </div>
        </div>
      )}
      <div ref={bar} data-composer-strip="images" data-images-strip="full" style={{
        position: 'relative', display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 8, boxSizing: 'border-box', minWidth: 0,
        height: isMobile ? 44 : 51, margin: isMobile ? '6px 0' : '10px 0 8px', padding: isMobile ? '0 6px' : '0 8px',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      }}>
        {title}
        {v5mode && (
          <ImageModeSwitch projectId={projectId} sessionId={sessionId ?? null} mode={v5mode} thread={thread} threads={state.threads}
            isMobile={isMobile} compact={isMobile} quiet={isMobile} bar={bar} />
        )}
        {thread ? (
          <span style={{ display: 'inline-flex', minWidth: isMobile ? 0 : 72, flex: '0 1 auto' }}>
            {/* На телефоне чип — миниатюра без имени: место нужно сводке с ценой */}
            <Chip selected leading={src ? <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : ic(Sparkles)}
              maxW="100%" title={`${focusLabel(thread, true, personal)} — ${personal ? 'режим «Картинка» работает с этой версией' : 'режим «Картинка» и Claude работают с этой версией'}. ✕ — снять выбор`} onRemove={release}>
              {isMobile ? null : <>Работаем с: <b>{focusLabel(thread, true, personal)}</b></>}
            </Chip>
          </span>
        ) : v5mode ? (
          // «Создать» без черновика: первая отправка из поля ввода сама заведёт карточку
          <span data-images-chip="new" style={{ display: 'inline-flex', flexShrink: 0 }}>
            <Chip dashed leading={ic(Sparkles)} title="Новая картинка: опишите её в поле ввода — карточка ляжет в ленту">
              {isMobile ? 'Новая' : 'Новая картинка'}
            </Chip>
          </span>
        ) : (
          <span style={{ display: 'inline-flex', flexShrink: 0 }}>
            <Chip dashed leading={ic(Sparkles)} title={sessionId ? 'Карточка «Новая картинка» в ленте, поле — в режим «Картинка»' : 'Сначала начните чат'}
              onClick={sessionId && !drafting ? () => { void draw(); } : undefined}>
              Нарисовать новую
            </Chip>
          </span>
        )}
        <span style={{ flex: 1 }} />
        {noProviders ? (
          <span style={{ fontSize: FS.xs, color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Рисовать нечем: поставщиков не настроил администратор
          </span>
        ) : v5mode && isMobile ? (
          // Телефон: место нужно переключателю, цена уже на кнопке поля ввода — от сводки остаётся «▴»
          <span data-images-settings-toggle="" style={{ display: 'inline-flex', flexShrink: 0 }}>
            <IconButton size="md" title="Открыть настройки панели «Картинки»" ariaLabel="Открыть настройки панели «Картинки»" onClick={openSettings}>
              {ic(ChevronUp, ICON_SIZE.sm)}
            </IconButton>
          </span>
        ) : (
          <span data-images-settings-toggle="" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto' }}>
            <Button size="xs" variant="secondary"
              leftIcon={isMobile ? undefined : ic(SlidersHorizontal)} title={toggle.title}
              onClick={openSettings}
              style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${toggle.on ? C.accent : C.border}`, background: C.bgWhite }}>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {isMobile ? mobileSummary(L.count, L.price ?? null) : stripSummary(parts, true)}
                </span>
                {L.blocked && <span style={{ display: 'inline-flex', color: C.warningText }}>{ic(AlertTriangle)}</span>}
                {ic(inSheet ? ChevronUp : ChevronRight)}
              </span>
            </Button>
          </span>
        )}
        {!isMobile && !personal && (L.prefs.characterSlug
          ? (
            <IconButton size="sm" title={`Персонаж: ${character?.name ?? L.prefs.characterSlug} — фото уходят в запрос`}
              ariaLabel="Персонаж" onClick={() => revealWorkspacePanel(IMAGES_PANEL, 'characters')}
              style={{ padding: 0, borderRadius: R.full, border: `1.5px solid ${C.accent}`, overflow: 'hidden' }}>
              {photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} /> : ic(User)}
            </IconButton>
          )
          : (
            <IconButton size="sm" title="Подключить персонажа" ariaLabel="Подключить персонажа"
              onClick={() => revealWorkspacePanel(IMAGES_PANEL, 'characters')}>{ic(User)}</IconButton>
          ))}
        {/* Телефон v5: вторая «▴» рядом со сводкой путалась бы с ней (как у «Звука») */}
        {!(v5mode && isMobile) && (
          <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
            {ic(ChevronUp, ICON_SIZE.sm)}
          </IconButton>
        )}

      </div>
      {sheetEl}
    </>
  );
}

// Строка состояния в меню переключателя полос — та же сводка, что у свёрнутой строки
export function imagesStripStatus(projectId: string | null, sessionId: string | null): string {
  const scope = enterScope(projectId, sessionId);
  const t = getFocusedThread(sessionId);
  return stripSummary({ focus: t ? focusLabel(t, false, isPersonalScope(scope)) : null, character: null, ...launchSummaryParts(scope, t, sessionId) });
}
