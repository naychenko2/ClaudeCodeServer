// Панель «Картинки» рабочей области (ADR-021 §3, макет docs/mockups/image-editor-v4-panel.html)
// на общем каркасе GenerationPanel. Вкладка «Настройки» — секции strip/settings, «Персонажи» —
// CharactersPanel.
// Живёт и в проекте, и в правой колонке личного чата (projectId = null → область personal):
// там нет персонажей и образцов из проекта. Вверху «Настроек» — операция и режим подбора,
// «− N +», цена и запуск — в закреплённом низу. Запуск идёт тем же путём, что композер: промпт
// отправляет само поле ввода (submitComposerMode), операции без промпта — launchThread.
// На телефоне панель рисует сама полоса «Картинки» шторкой каркаса (layout="sheet"): у
// телефона нет зоны панелей рабочей области.

import { useEffect, useState } from 'react';
import { Contact, Image as ImageIcon, Plus, RotateCw, SlidersHorizontal, Users, X } from 'lucide-react';
import {
  Button, EmptyState, GenerationPanel, IconButton, C, FS, SP, REVEAL_PANEL_EVENT, ICON_SIZE, showToast, submitComposerMode, useAgentPick,
  useIsMobile,
  type GenerationFoot, type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { CharactersPanel, type CharacterEditing } from '../characters/CharactersPanel';
import { IMAGES_PANEL } from '../characters/panel';
import { useCharacters } from '../characters/useCharacters';
import { useCharacter } from '../strip/settings/CharacterSection';
import { enterScope, isPersonalScope } from '../scope';
import { ic } from '../strip/settings/primitives';
import { SettingsSections } from '../strip/settings/SettingsSections';
import { againPrompt, IMAGE_COMPOSER_MODE, launchAgain } from '../composer/imageMode';
import { useImageComposerText } from '../composer/composerText';
import { ImageModeSwitch } from '../strip/ImageModeSwitch';
import { createDraft, releaseFocus } from '../thread/actions';
import { focusLabel, isEmptyThread } from '../thread/model';
import { imageDraftKey, useThreads } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { launchThread, useThreadLaunch } from '../thread/useThreadLaunch';
import { ONE_VARIANT_HINT } from './panelOp';
import { CreateBody } from './CreateBody';
import { EditBody } from './EditBody';
import { useMarkImagesPanelShown } from './panelOpen';

type Tab = 'settings' | 'characters';
const isTab = (t: unknown): t is Tab => t === 'settings' || t === 'characters';

// Вкладку из revealWorkspacePanel(images, tab) ловим на уровне модуля: закрытая панель
// монтируется уже ПОСЛЕ события, и её собственный слушатель его бы пропустил
let wanted: Tab | null = null;
const subs = new Set<() => void>();
const takeWanted = () => { const t = wanted; wanted = null; return t; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
    if (d?.key !== IMAGES_PANEL || !isTab(d.tab)) return;
    wanted = d.tab;
    subs.forEach(fn => fn());
  });
}

// Запуск из закреплённого низа. Без выбранной картинки — черновик «Новая картинка», как
// «Нарисовать новую»: поле ввода само переходит в режим «Картинка», промпт пишут там.
// Операция без промпта — тот же launchThread, что у композера (операцию он берёт из панели);
// с промптом — отправка самим полем ввода, как по Enter
export async function panelRun(projectId: string, sessionId: string, thread: ImageThread | null, noPrompt: boolean) {
  if (!thread) { await createDraft(projectId, sessionId, ''); return; }
  if (noPrompt) { await launchThread(projectId, sessionId, thread, { kind: 'prompt', prompt: '' }); return; }
  if (!submitComposerMode(sessionId, IMAGE_COMPOSER_MODE)) showToast('Поле ввода не найдено — откройте чат', '', 'error');
}

export function ImagesPanel({ ctx, layout = 'column' }: { ctx: WorkspacePanelDefCtx; layout?: 'column' | 'sheet' }) {
  const { sessionId } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  const { list } = useCharacters(personal ? null : projectId);
  const [tab, setTab] = useState<Tab>(() => takeWanted() ?? 'settings');
  const [editing, setEditing] = useState<CharacterEditing>(null);
  const { name: characterName } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);
  useMarkImagesPanelShown(layout === 'column');
  const agentPick = useAgentPick(sessionId, IMAGES_PANEL);
  const isMobile = useIsMobile();
  // «↻ Ещё N» в низу: «Создать», поле ввода пустое, у выбранной картинки есть прошлый запуск
  const composerText = useImageComposerText(sessionId);
  const again = L.imageMode === 'create' && !composerText.trim() && !!againPrompt(sessionId);

  useEffect(() => {
    const on = () => { const t = takeWanted(); if (t) setTab(t); };
    subs.add(on);
    return () => { subs.delete(on); };
  }, []);

  const subtitle = [L.provider?.label, L.model?.label].filter(Boolean).join(' · ');
  const release = () => { if (sessionId) void releaseFocus(projectId, sessionId, thread); };

  // Черновик «Нарисовать новую» — ещё не картинка: строка говорит, куда ляжет результат
  const draft = !thread || (!thread.file && isEmptyThread(thread));
  const context = tab === 'settings'
    ? thread && !draft
      ? (
        <>
          <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            Работаем с: <b style={{ color: C.textHeading }}>{focusLabel(thread, true, personal)}</b>
          </span>
        </>
      )
      : (
        <>
          <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            <b style={{ color: C.textHeading }}>Новая картинка</b> · результат ляжет в ленту новой карточкой
          </span>
        </>
      )
    : personal ? undefined : <span>Папка <code>characters/</code> проекта · подключённый персонаж уходит в каждую генерацию</span>;

  let body;
  if (tab === 'characters') {
    body = personal
      ? <EmptyState compact icon={ic(Users, ICON_SIZE.sm)} title="Персонажи живут в проекте"
          subtitle="Персонажи хранятся в папке characters/ проекта. В личном чате лицо можно передать образцом с ролью «Лицо» на вкладке «Настройки»." />
      : <CharactersPanel projectId={projectId} editing={editing} onEditing={setEditing} />;
  } else if (!L.catalog) {
    body = <div style={{ fontSize: FS.sm, color: C.textMuted, paddingTop: SP.sm }}>Загружаем…</div>;
  } else if (!L.catalog.providers.length) {
    body = <EmptyState compact icon={ic(ImageIcon, ICON_SIZE.sm)} title="Рисовать нечем" subtitle="Поставщиков не настроил администратор" />;
  } else if (L.imageMode === 'create' || (L.imageMode === 'edit' && thread)) {
    // Панель v5 (флаг image-panel-v5): зеркало «Создать / Править» и тело по режиму
    body = (
      <>
        <div style={{ height: SP.sm }} />
        <ImageModeSwitch projectId={projectId} sessionId={sessionId} mode={L.imageMode} thread={thread} threads={state.threads} isMobile={ctx.isMobile} />
        {L.imageMode === 'create' || !thread
          ? <CreateBody projectId={projectId} sessionId={sessionId} L={L} catalog={L.catalog} isMobile={isMobile} onCharacters={() => setTab('characters')} />
          : (
            <EditBody projectId={projectId} sessionId={sessionId} thread={thread} L={L} catalog={L.catalog} isMobile={isMobile}
              onCharacters={() => setTab('characters')} />
          )}
      </>
    );
  } else {
    body = (
      <SettingsSections projectId={projectId} sessionId={sessionId} L={L} catalog={L.catalog} thread={thread} onCharacters={() => setTab('characters')} />
    );
  }

  const foot: GenerationFoot | undefined = tab === 'settings' && L.catalog?.providers.length ? {
    reason: !sessionId ? 'Откройте чат: результат ляжет в его ленту' : L.reason || undefined,
    queue: L.queue,
    count: L.count,
    maxCount: L.maxCount,
    maxCountHint: L.maxCount === 1 ? ONE_VARIANT_HINT : undefined,
    onCountChange: n => L.setSettings({ count: n }),
    price: L.priceLines,
    runLabel: again ? `Ещё ${L.count}` : L.runLabel,
    ...(again ? { runIcon: ic(RotateCw) } : null),
    onRun: () => {
      if (!sessionId) return;
      if (again) void launchAgain(projectId, sessionId);
      else void panelRun(projectId, sessionId, thread, !!L.quickAction);
    },
  } : undefined;

  // Свой низ «Персонажей»: кто подключён и «＋ Персонаж»; на время формы низа нет
  const footContent = tab === 'characters' && !personal && !editing ? (
    <div data-images-characters-foot="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
      <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        Подключён: <b style={{ color: C.textHeading }}>{L.prefs.characterSlug ? characterName : 'никто'}</b>
      </span>
      <Button size="xs" variant="secondary" leftIcon={ic(Plus)} onClick={() => setEditing({ kind: 'new' })} style={{ flexShrink: 0 }}>
        Персонаж
      </Button>
    </div>
  ) : undefined;

  return (
    <GenerationPanel<Tab>
      title="Картинки"
      subtitle={subtitle || undefined}
      icon={ic(ImageIcon, ICON_SIZE.sm)}
      tabs={[
        { value: 'settings', label: 'Настройки', icon: ic(SlidersHorizontal) },
        { value: 'characters', label: 'Персонажи', icon: ic(Contact), count: personal || !list?.length ? undefined : list.length },
      ]}
      tab={tab}
      onTabChange={setTab}
      context={context}
      contextAction={tab === 'settings' && thread
        ? <IconButton size="xs" title="Снять выбор картинки" ariaLabel="Снять выбор картинки" onClick={release}>{ic(X)}</IconButton>
        : undefined}
      panelKey={IMAGES_PANEL}
      agentPick={agentPick}
      draftKey={thread ? imageDraftKey(thread.id) : null}
      foot={foot}
      footContent={footContent}
      // Поставщик и модель уже в подзаголовке шапки строкой выше: в сводке — только выбор
      peekSummary={thread && !draft ? focusLabel(thread, true, personal) : 'Новая картинка'}
      onClose={ctx.onClose}
      layout={layout}
    >
      {body}
    </GenerationPanel>
  );
}
