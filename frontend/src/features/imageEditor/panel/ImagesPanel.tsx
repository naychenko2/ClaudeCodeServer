// Панель «Картинки» рабочей области (ADR-021 §3, макет docs/mockups/image-editor-v4-panel.html)
// на общем каркасе GenerationPanel, под флагом image-editor-panel. Вкладка «Настройки» — те же
// секции, что у карточки над полосой (strip/settings), «Персонажи» — прежняя CharactersPanel.
// Живёт и в проекте, и в правой колонке личного чата (projectId = null → область personal):
// там нет персонажей и образцов из проекта. Вверху «Настроек» — операция и режим подбора,
// «− N +», цена и запуск — в закреплённом низу. Запуск идёт тем же путём, что композер: промпт
// отправляет само поле ввода (submitComposerMode), операции без промпта — launchThread.

import { useEffect, useState } from 'react';
import { Contact, Image as ImageIcon, SlidersHorizontal, Users, X } from 'lucide-react';
import {
  EmptyState, GenerationPanel, IconButton, C, FS, SP, REVEAL_PANEL_EVENT, ICON_SIZE, showToast, submitComposerMode,
  type GenerationFoot, type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { CharactersPanel } from '../characters/CharactersPanel';
import { IMAGES_PANEL } from '../characters/panel';
import { useCharacters } from '../characters/useCharacters';
import { enterScope, isPersonalScope } from '../scope';
import { ic } from '../strip/settings/primitives';
import { SettingsSections } from '../strip/settings/SettingsSections';
import { IMAGE_COMPOSER_MODE } from '../composer/imageMode';
import { createDraft, releaseFocus } from '../thread/actions';
import { focusLabel } from '../thread/model';
import { useThreads } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { launchThread, useThreadLaunch } from '../thread/useThreadLaunch';
import { ONE_VARIANT_HINT } from './panelOp';

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

export function ImagesPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const { sessionId } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  const { list } = useCharacters(personal ? null : projectId);
  const [tab, setTab] = useState<Tab>(() => takeWanted() ?? 'settings');

  useEffect(() => {
    const on = () => { const t = takeWanted(); if (t) setTab(t); };
    subs.add(on);
    return () => { subs.delete(on); };
  }, []);

  const subtitle = [L.provider?.label, L.model?.label].filter(Boolean).join(' · ');
  const release = () => { if (sessionId) void releaseFocus(projectId, sessionId, thread); };

  const context = tab === 'settings'
    ? thread
      ? (
        <>
          <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            Работаем с: <b style={{ color: C.textHeading }}>{focusLabel(thread, true, personal)}</b>
          </span>
          <IconButton size="xs" title="Снять выбор картинки" ariaLabel="Снять выбор картинки" onClick={release}>{ic(X)}</IconButton>
        </>
      )
      : <span>Новая картинка · результат ляжет в ленту новой карточкой</span>
    : personal ? undefined : <span>Папка <code>characters/</code> проекта · подключённый персонаж уходит в каждую генерацию</span>;

  let body;
  if (tab === 'characters') {
    body = personal
      ? <EmptyState compact icon={ic(Users, ICON_SIZE.sm)} title="Персонажи живут в проекте"
          subtitle="Персонажи хранятся в папке characters/ проекта. В личном чате лицо можно передать образцом с ролью «Лицо» на вкладке «Настройки»." />
      : <CharactersPanel projectId={projectId} />;
  } else if (!L.catalog) {
    body = <div style={{ fontSize: FS.sm, color: C.textMuted, paddingTop: SP.sm }}>Загружаем…</div>;
  } else if (!L.catalog.providers.length) {
    body = <EmptyState compact icon={ic(ImageIcon, ICON_SIZE.sm)} title="Рисовать нечем" subtitle="Поставщиков не настроил администратор" />;
  } else {
    body = (
      <SettingsSections projectId={projectId} L={L} catalog={L.catalog} isMobile={false} thread={thread}
        onCharacterSheet={() => setTab('characters')} onCharacters={() => setTab('characters')} panel />
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
    runLabel: L.runLabel,
    onRun: () => { if (sessionId) void panelRun(projectId, sessionId, thread, !!L.quickAction); },
  } : undefined;

  return (
    <GenerationPanel<Tab>
      title="Картинки"
      subtitle={subtitle || undefined}
      icon={ic(ImageIcon, ICON_SIZE.sm)}
      tabs={[
        { value: 'settings', label: 'Настройки', icon: ic(SlidersHorizontal) },
        { value: 'characters', label: 'Персонажи', icon: ic(Contact), count: personal ? undefined : list?.length },
      ]}
      tab={tab}
      onTabChange={setTab}
      context={context}
      foot={foot}
      onClose={ctx.onClose}
      layout="column"
    >
      {body}
    </GenerationPanel>
  );
}
