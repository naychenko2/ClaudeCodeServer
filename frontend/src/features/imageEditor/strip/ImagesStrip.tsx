// Полоса «Картинки» над композером (реестр composer-strip; прототип полос, вариант C —
// docs/mockups/image-editor-v3-strips-prototype.html). Одна строка высотой как у git-полосы:
// переключатель от хоста, чип «Работаем с: …» (или «Нарисовать новую»), кнопка-сводка
// «поставщик · модель · N вар. · цена ▾» и аватар персонажа. Настройки — карточкой над
// полосой (на телефоне шторкой): поставщик, модель, число вариантов, персонаж, образцы,
// «Размер оригинала». Свёрнутая — строка 30 px со сводкой, клик разворачивает.
// В личном чате вне проекта (область personal) нет персонажа и образцов из проекта: их
// источник — папки проекта.
// С флагом image-editor-panel (десктоп) карточки нет: сводка открывает панель «Картинки»
// на «Настройках», аватар — на «Персонажах» (panel/ImagesPanel). На телефоне с флагом
// зоны панелей нет: ту же панель полоса рисует шторкой каркаса вместо Modal настроек,
// и автооткрытие по выбору картинки (revealWorkspacePanel) поднимает её же.

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, ChevronUp, Image as ImageIcon, SlidersHorizontal, Sparkles, User, X } from 'lucide-react';
import {
  Button, Chip, IconButton, Modal, C, FS, R, SHADOW, SP, Z, ICON_SIZE, FLAGS, markGenPanelDismissed, useFeature,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import type { ImageEditCatalog } from '../api';
import { CharactersPanel } from '../characters/CharactersPanel';
import { CHARACTERS_PANEL, IMAGES_PANEL, revealWorkspacePanel } from '../characters/panel';
import { ImagesPanel } from '../panel/ImagesPanel';
import { createDraft, releaseFocus } from '../thread/actions';
import { enterScope, isPersonalScope } from '../scope';
import { focusLabel } from '../thread/model';
import { getFocusedThread, useThreads } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { activeSrc, launchSummaryParts, useThreadLaunch } from '../thread/useThreadLaunch';
import { openCharacters, useCharacter } from './settings/CharacterSection';
import { ic, type Launch } from './settings/primitives';
import { SettingsSections } from './settings/SettingsSections';
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

// Карточка настроек генерации — над полосой на десктопе и шторкой на телефоне; тело общее
// с боковой панелью генерации (settings/SettingsSections)
function SettingsPanel({ projectId, L, catalog, isMobile, thread }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; isMobile: boolean; thread: ImageThread | null;
}) {
  const [charSheet, setCharSheet] = useState(false);
  const personal = isPersonalScope(projectId);

  return (
    <>
      <SettingsSections projectId={projectId} L={L} catalog={catalog} isMobile={isMobile} thread={thread}
        onCharacterSheet={() => setCharSheet(true)} />
      {charSheet && !personal && (
        <Modal title="Персонажи" onClose={() => setCharSheet(false)}>
          <CharactersPanel projectId={projectId} />
        </Modal>
      )}
    </>
  );
}

export function ImagesStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  const [drafting, setDrafting] = useState(false);
  const [open, setOpen] = useState(false);
  const [charSheet, setCharSheet] = useState(false);
  const shell = useRef<HTMLDivElement>(null);
  const { current: character, photo } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);
  // Настройки — в панели «Картинки»: на десктопе в зоне панелей, на телефоне шторкой полосы
  const panelFlag = useFeature(FLAGS.imageEditorPanel);
  const inPanel = panelFlag && !isMobile;
  const inSheet = panelFlag && isMobile;
  const [sheet, setSheet] = useState(false);

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

  // Карточка настроек закрывается кликом мимо полосы и Esc
  useEffect(() => {
    if (!open || isMobile) return;
    const onDown = (e: PointerEvent) => {
      const t = e.target as Node | null;
      // Меню и диалоги карточки рисуются порталом — клик по ним не «мимо»
      if (t && (shell.current?.contains(t) || (t as Element).closest?.('[role="dialog"], [role="menu"]'))) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', onDown);
    window.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('pointerdown', onDown); window.removeEventListener('keydown', onKey); };
  }, [open, isMobile]);

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
          <Thumb src={src} />
          <span data-images-summary="" style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
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
  const settings = L.catalog && !noProviders
    ? <SettingsPanel projectId={projectId} L={L} catalog={L.catalog} isMobile={isMobile} thread={thread} /> : null;

  return (
    <>
      <div ref={shell} data-composer-strip="images" data-images-strip="full" style={{
        position: 'relative', display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 8, boxSizing: 'border-box', minWidth: 0,
        height: isMobile ? 44 : 51, margin: isMobile ? '6px 0' : '10px 0 8px', padding: isMobile ? '0 6px' : '0 8px',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      }}>
        {title}
        {thread ? (
          <span style={{ display: 'inline-flex', minWidth: 72, flex: '0 1 auto' }}>
            <Chip selected leading={src ? <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : ic(Sparkles)}
              maxW="100%" title={personal ? 'Режим «Картинка» работает с этой версией. ✕ — снять выбор' : 'Режим «Картинка» и Claude работают с этой версией. ✕ — снять выбор'} onRemove={release}>
              {!isMobile && 'Работаем с: '}<b>{focusLabel(thread, true, personal)}</b>
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
        ) : (
          <span data-images-settings-toggle="" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto' }}>
            <Button size="xs" variant="secondary"
              leftIcon={ic(SlidersHorizontal)} title={inPanel ? 'Открыть настройки в панели «Картинки»' : 'Настройки генерации'}
              onClick={() => (inPanel ? revealWorkspacePanel(IMAGES_PANEL, 'settings') : inSheet ? setSheet(true) : setOpen(v => !v))}
              style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${open ? C.accent : C.border}`, background: C.bgWhite }}>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {isMobile ? `${L.count} вар.` : stripSummary(parts, true)}
                </span>
                {L.blocked && <span style={{ display: 'inline-flex', color: C.warningText }}>{ic(AlertTriangle)}</span>}
                {ic(inPanel ? ChevronRight : inSheet ? ChevronUp : ChevronDown)}
              </span>
            </Button>
          </span>
        )}
        {!isMobile && !personal && (L.prefs.characterSlug
          ? (
            <IconButton size="sm" title={`Персонаж: ${character?.name ?? L.prefs.characterSlug} — фото уходят в запрос`}
              ariaLabel="Персонаж" onClick={() => (inPanel ? revealWorkspacePanel(IMAGES_PANEL, 'characters') : revealWorkspacePanel(CHARACTERS_PANEL))}
              style={{ padding: 0, borderRadius: R.full, border: `1.5px solid ${C.accent}`, overflow: 'hidden' }}>
              {photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} /> : ic(User)}
            </IconButton>
          )
          : (
            <IconButton size="sm" title="Подключить персонажа" ariaLabel="Подключить персонажа"
              onClick={() => openCharacters(isMobile, () => setCharSheet(true))}>{ic(User)}</IconButton>
          ))}
        <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
          {ic(ChevronUp, ICON_SIZE.sm)}
        </IconButton>

        {open && settings && !isMobile && !inPanel && (
          <div data-images-settings-card="" style={{
            position: 'absolute', right: 0, bottom: 'calc(100% + 6px)', width: 380, maxWidth: 'calc(100vw - 40px)', zIndex: Z.dropdown,
            boxSizing: 'border-box', padding: `${SP.xs}px ${SP.lg}px ${SP.md}px`, cursor: 'default',
            background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xxl, boxShadow: SHADOW.dropdown,
          }}>
            {settings}
          </div>
        )}
        {open && settings && isMobile && (
          <Modal title="Настройки генерации" onClose={() => setOpen(false)}>{settings}</Modal>
        )}
        {charSheet && !personal && (
          <Modal title="Персонажи" onClose={() => setCharSheet(false)}>
            <CharactersPanel projectId={projectId} />
          </Modal>
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
  return stripSummary({ focus: t ? focusLabel(t, false, isPersonalScope(scope)) : null, character: null, ...launchSummaryParts(scope, t) });
}
