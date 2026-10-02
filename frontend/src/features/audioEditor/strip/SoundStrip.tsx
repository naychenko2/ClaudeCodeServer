// Полоса «Звук» над композером (реестр composer-strip; макет audio-editor-v2-proposal.md,
// раздел «Полоса «Звук» над композером»). Настроек в себе не раскрывает — это сводка и вход в
// панель «Звук»: заголовок-переключатель от хоста (в его меню — ярлыки «Голос» и «Музыка»),
// зеркало режимов «Голос / Музыка / Обработка» (макет audio-panel-v4, вариант 3), чип
// «Работаем с: файл · версия ✕» или пунктирный «✦ Новый звук», бейдж очереди GPU,
// кнопка-сводка «Режим · операция · поставщик · модель · голос · N вар. · цена» → панель на
// «Настройках» (на телефоне — одна «▴»). Над полосой — плашка «Вернуть» после снятия звука
// в «Обработке». Высота 48 px на десктопе, 42 на телефоне, свёрнутая строка — 30 px с иконкой режима.

import { useRef, useState, useSyncExternalStore } from 'react';
import type { KeyboardEvent, ReactNode } from 'react';
import { AudioLines, ChevronDown, ChevronRight, ChevronUp, Cpu, Sparkles, X } from 'lucide-react';
import type { User } from 'lucide-react';
import {
  Button, Chip, IconButton, ReleaseNotice, C, FS, R, SP, ICON_SIZE, ICON_STROKE, revealWorkspacePanel, useGenerationSheet,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { audioScope, isPersonalScope } from '../scope';
import { createDraft, releaseFocus, soundReleaseUndo, undoSoundRelease } from '../thread/actions';
import {
  getCatalog, getFocusedThread, getJobsOf, SOUND_PANEL, useAudioThreads,
} from '../thread/threadStore';
import { soundSource } from '../thread/modeState';
import type { AudioThread } from '../api';
import { MODE_LABEL } from '../ops';
import { focusLabel, queueBadge, resolveLaunch, soundSummary } from './summary';
import { MODE_ICON, SoundModeSwitch } from './SoundModeSwitch';

const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Всё, что полоса, свёрнутая строка и меню переключателя показывают о чате, — одной точкой
export function stripModel(projectId: string | null, sessionId: string | null, thread: AudioThread | null) {
  const scope = audioScope(projectId);
  const catalog = getCatalog(scope);
  const src = soundSource(scope, sessionId, thread);
  const launch = resolveLaunch(src.thread, src.prefs, catalog, src.mode);
  const running = !!thread?.launches.some(l => l.status === 'running');
  return {
    scope,
    catalog,
    launch,
    focus: thread ? focusLabel(thread) : null,
    badge: queueBadge(getJobsOf(sessionId, thread?.id ?? null), running, launch),
    noProviders: !!catalog && catalog.providers.length === 0,
  };
}

// Строка состояния в меню переключателя — та же сводка, что у свёрнутой строки
export function soundStripStatus(projectId: string | null, sessionId: string | null): string {
  const m = stripModel(projectId, sessionId, getFocusedThread(sessionId));
  return soundSummary({ focus: m.focus, launch: m.launch });
}

function Badge({ children }: { children: ReactNode }) {
  return (
    <span data-sound-queue="" title="Очередь видеокарты для локальных и тяжёлых задач" style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 1, minWidth: 0, height: 22, padding: `0 ${SP.sm}px`,
      fontSize: FS.xs, color: C.textSecondary, background: C.bgInset, borderRadius: R.full,
      whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
    }}>
      {ic(Cpu)}<span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{children}</span>
    </span>
  );
}

export function SoundStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  const scope = audioScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const state = useAudioThreads(scope, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const m = stripModel(ctx.projectId, sessionId, thread);
  const [drafting, setDrafting] = useState(false);
  const bar = useRef<HTMLDivElement>(null);
  const offer = useSyncExternalStore(soundReleaseUndo.subscribe, soundReleaseUndo.current, soundReleaseUndo.current);
  const undo = offer && offer.snapshot.sessionId === sessionId ? offer : null;
  const ModeIcon = MODE_ICON[m.launch.mode];
  // Уже 800 панель встаёт шторкой над полем ввода, а не колонкой справа
  const narrow = useGenerationSheet();

  const openSettings = () => revealWorkspacePanel(SOUND_PANEL, 'settings');
  const draft = async () => {
    if (!sessionId) return;
    setDrafting(true);
    await createDraft(scope, sessionId, m.launch.mode);
    setDrafting(false);
  };
  const release = () => { if (sessionId) void releaseFocus(scope, sessionId, thread); };
  const title = switcher ?? (
    <span title="Звук" style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
      {ic(AudioLines)}{!isMobile && 'Звук'}
    </span>
  );

  if (collapsed) {
    const expand = () => setCollapsed(false);
    return (
      <div role="button" tabIndex={0} data-composer-strip="sound" data-sound-strip="mini" title="Развернуть полосу «Звук»"
        onClick={expand}
        onKeyDown={(e: KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); expand(); } }}
        style={{
          // Телефон: строка высотой с полную полосу, чтобы «⌄» встал тач-целью 40×40 (место — за счёт сводки)
          display: 'flex', alignItems: 'center', gap: SP.sm, height: isMobile ? 42 : 30, margin: isMobile ? '6px 0' : '4px 0 6px',
          padding: isMobile ? '0 0 0 4px' : '0 6px 0 4px',
          boxSizing: 'border-box', minWidth: 0, cursor: 'pointer',
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
        }}>
        {title}
        <span data-sound-mini-mode={m.launch.mode} title={`Режим: ${MODE_LABEL[m.launch.mode]}`} style={{ display: 'inline-flex', color: C.textSecondary, flexShrink: 0 }}>
          {ic(ModeIcon, ICON_SIZE.sm)}
        </span>
        {/* Клик по сводке в строке открывает панель, по остальной строке — разворачивает полосу */}
        <span data-sound-summary="" title="Открыть настройки в панели «Звук»"
          onClick={e => { e.stopPropagation(); openSettings(); }}
          style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {soundSummary({ focus: m.focus, launch: m.launch })}
        </span>
        {thread && (
          <span style={{ display: 'inline-flex' }} onClick={e => e.stopPropagation()}>
            <IconButton size="xs" title="Снять выбор звука" ariaLabel="Снять выбор звука" onClick={release}>{ic(X)}</IconButton>
          </span>
        )}
        <span data-sound-mini-expand="" style={{
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, color: C.textMuted,
          ...(isMobile && { width: 40, height: 40 }),
        }}>{ic(ChevronDown, ICON_SIZE.sm)}</span>
      </div>
    );
  }

  const modeSwitch = (
    <SoundModeSwitch scope={scope} sessionId={sessionId} mode={m.launch.mode} thread={thread} threads={state.threads}
      isMobile={isMobile} compact={isMobile} quiet={isMobile} bar={bar} />
  );

  return (
    <>
    {/* Плашка «Вернуть» встаёт над полосой и не сдвигает поле ввода */}
    {undo && (
      <div style={{ position: 'relative', height: 0 }}>
        <div data-sound-release="" style={{ position: 'absolute', left: 0, right: 0, bottom: SP.xs }}>
          <ReleaseNotice text={undo.text} onUndo={() => { void undoSoundRelease(); }} isMobile={isMobile} />
        </div>
      </div>
    )}
    <div ref={bar} data-composer-strip="sound" data-sound-strip="full" style={{
      position: 'relative', display: 'flex', alignItems: 'center', gap: isMobile ? 0 : 8, boxSizing: 'border-box', minWidth: 0,
      // Телефон: без полей и зазоров — переключатель полос и «▴» (по 40×40) встают вплотную к рамке,
      // воздух между соседями дают поля самих иконок-кнопок, а ширина остаётся имени в чипе
      height: isMobile ? 42 : 48, margin: isMobile ? '6px 0' : '10px 0 8px', padding: isMobile ? 0 : '0 8px',
      background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
    }}>
      {title}
      {modeSwitch}
      {thread ? (
        // Телефон: имени отдано всё, что осталось от сегментов и «▴»; иконка-дубль режима и
        // широкие поля чипа уходят, чтобы имя читалось (≥ 96 px на 360)
        <span data-sound-chip="focus" style={{ display: 'inline-flex', minWidth: isMobile ? 0 : 72, flex: isMobile ? '1 1 0' : '0 1 auto' }}>
          <Chip selected leading={isMobile ? undefined : ic(AudioLines)} dense={isMobile} maxW="100%" onRemove={release}
            title={personal ? 'Режим «Звук» работает с этой версией. ✕ — снять выбор' : 'Режим «Звук» и Claude работают с этой версией. ✕ — снять выбор'}>
            {!isMobile && 'Работаем с: '}<b>{m.focus}</b>
          </Chip>
        </span>
      ) : (
        <span data-sound-chip="new" style={{ display: 'inline-flex', flexShrink: isMobile ? 1 : 0, minWidth: 0 }}>
          <Chip dashed maxW="100%" leading={ic(Sparkles)} title={sessionId ? 'Карточка «Новый звук» в ленте, поле — в режим «Звук»' : 'Сначала начните чат'}
            onClick={sessionId && !drafting ? () => { void draft(); } : undefined}>
            Новый звук
          </Chip>
        </span>
      )}
      {m.badge && !isMobile && <Badge>{m.badge}</Badge>}
      {!isMobile && <span style={{ flex: 1 }} />}
      {m.noProviders ? (
        <span style={{ fontSize: FS.xs, color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Звучать нечем: поставщиков не настроил администратор
        </span>
      ) : isMobile ? (
        // Телефон: место нужно переключателю, цена уже на кнопке поля ввода — от сводки остаётся «▴»
        <span data-sound-settings-toggle="" style={{ display: 'inline-flex', flexShrink: 0, marginLeft: 'auto' }}>
          <IconButton size="lg" title="Открыть настройки панели «Звук»" ariaLabel="Открыть настройки панели «Звук»" onClick={openSettings}>
            {ic(ChevronUp, ICON_SIZE.sm)}
          </IconButton>
        </span>
      ) : (
        <span data-sound-settings-toggle="" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto' }}>
          <Button size="xs" variant="secondary" title={narrow ? 'Открыть настройки панели «Звук»' : 'Настройки открываются в панели «Звук» справа'} onClick={openSettings}
            style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${C.border}`, background: C.bgWhite }}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
              <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {soundSummary({ focus: m.focus, launch: m.launch }, true)}
              </span>
              {ic(narrow ? ChevronUp : ChevronRight)}
            </span>
          </Button>
        </span>
      )}
      {/* Телефон: вторая «▴» рядом со сводкой путалась бы с ней (макет v4) */}
      {!isMobile && (
        <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
          {ic(ChevronUp, ICON_SIZE.sm)}
        </IconButton>
      )}
    </div>
    </>
  );
}
