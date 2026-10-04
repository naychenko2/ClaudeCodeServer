// Редактор звука на весь экран (ADR-023, шаг 2з-2): волна версии с выделением куска, поля обрезки и
// громкости с фейдами из панели «Звук», запуск — правка без ИИ (каждая правка — новая версия).
// Выделение — то же, что у карточки в ленте и поля «Кусок» (стор нитей), поэтому выделенный здесь кусок
// делает живым чип «Перегенерировать кусок». Закрытие (✕, «Готово», Esc) ничего не отменяет.

import { useMemo, useState } from 'react';
import { Check, Scissors } from 'lucide-react';
import { Button, Modal, C, getChatContextState, setPrimary, FS, SP, ICON_SIZE, ICON_STROKE, useIsMobile, showToast } from 'aihome_shell/kit';
import { audioApi, type AudioThreadVersion } from '../api';
import { TrimFields, trimReady } from '../panel/OpFields';
import { DEFAULT_TRIM, type TrimInputs } from '../panel/inputs';
import { runTrimSteps, trimWithPiece } from '../panel/run';
import { AudioPlayer, type AudioSource } from '../player';
import { audioScope } from '../scope';
import { hasMain, threadName, versionLabel } from '../thread/model';
import { useServerPeaks } from '../thread/serverPeaks';
import { closeEditor, getSelection, getThreadsState, mutate, setSelection, useAudioThreads } from '../thread/threadStore';

const PEAK_POINTS = 240;

// Основной объект контекста закреплён за версией: правка дала новую, и «Перегенерировать кусок» должен
// идти от неё, а не от прежней — иначе выделение на новой волне не совпало бы с версией объекта
function followPrimary(sessionId: string, threadId: string) {
  const { primary } = getChatContextState(sessionId);
  const current = getThreadsState(sessionId).threads.find(t => t.id === threadId)?.currentVersionId;
  if (primary?.kind === 'audio' && primary.ref.threadId === threadId && current && primary.ref.versionId !== current) {
    void setPrimary(sessionId, { kind: 'audio', ref: { threadId, versionId: current } });
  }
}

function Editor({ projectId, sessionId, threadId, versionId }: {
  projectId: string | null; sessionId: string; threadId: string; versionId: string | null;
}) {
  const mobile = useIsMobile();
  const scope = audioScope(projectId);
  const state = useAudioThreads(scope, sessionId);
  const thread = state.threads.find(t => t.id === threadId) ?? null;
  const [trim, setTrim] = useState<TrimInputs>(DEFAULT_TRIM);
  const [busy, setBusy] = useState(false);
  // Открытая не текущая версия остаётся показанной, пока не применили правку: правка идёт от версии в работе
  const [shownId, setShownId] = useState(versionId);
  const version: AudioThreadVersion | null = thread
    ? thread.versions.find(v => v.id === (shownId ?? thread.currentVersionId)) ?? null
    : null;
  // Пики — только у версии с главным файлом: у стемов без общей дорожки и у версии, которой ещё нет, главного звука нет (404)
  const peaks = useServerPeaks(thread && version && hasMain(version)
    ? [{ scope, sessionId, threadId: thread.id, versionId: version.id, role: null, points: PEAK_POINTS }]
    : []);

  const stored = getSelection(sessionId, threadId);
  const sel = stored && version && stored.versionId === version.id ? stored : null;
  const source: AudioSource | null = thread && version && hasMain(version) ? {
    key: version.id,
    label: versionLabel(version),
    url: audioApi.versionFileUrl(scope, sessionId, thread.id, version.id),
    peaks: peaks[0]?.peaks ?? [],
    duration: peaks[0]?.seconds,
    title: versionLabel(version),
  } : null;
  const ready = trimReady(trim, sel);
  const piece = useMemo(() => (thread && version ? { sessionId, threadId: thread.id, versionId: version.id } : null),
    [sessionId, thread, version]);

  if (!thread) return null;
  const running = thread.launches.some(l => l.status === 'running');
  const stemsOnly = !!version && !hasMain(version) && version.files.some(f => f.role.startsWith('stem:'));

  const apply = async () => {
    if (!version || busy) return;
    setBusy(true);
    try {
      if (version.id !== thread.currentVersionId
        && !(await mutate(scope, sessionId, rev => audioApi.current(scope, sessionId, thread.id, version.id, rev)))) return;
      if (await runTrimSteps(scope, sessionId, thread.id, trimWithPiece(trim, sel))) {
        // Новая версия: кусок и поля относились к прежней
        setSelection(sessionId, thread.id, null);
        setTrim(DEFAULT_TRIM);
        followPrimary(sessionId, thread.id);
        setShownId(null);
        showToast('Готово: правка легла новой версией', 'Её карточка — в ленте', 'info');
      }
    } finally {
      setBusy(false);
    }
  };

  const body = (
    <div style={{ display: 'flex', flexDirection: mobile ? 'column' : 'row', flex: mobile ? 'none' : 1, minHeight: 0 }}>
      <div data-audio-editor-wave style={{
        flex: mobile ? 'none' : 1, minWidth: 0, padding: mobile ? SP.md : SP.lg, background: C.bgInset,
        display: 'flex', flexDirection: 'column', justifyContent: 'center',
      }}>
        {source ? (
          <AudioPlayer sources={[source]} selection={sel} large
            onSelectionChange={s => setSelection(sessionId, thread.id, s ? { ...s, versionId: version!.id } : null)} />
        ) : (
          <div style={{ color: C.textMuted, fontSize: FS.sm, textAlign: 'center' }}>
            {running
              ? 'Звук готовится — волна появится, когда запуск закончится.'
              : stemsOnly
                ? 'У этой версии только стемы, общей дорожки нет. Сведите их чипом «Свести» — волна появится в новой версии.'
                : 'Звука ещё нет. Озвучьте или сочините его в композере — волна появится здесь.'}
          </div>
        )}
      </div>
      <div data-audio-editor-side style={{
        display: 'flex', flexDirection: 'column', gap: SP.md, padding: mobile ? SP.md : SP.lg,
        width: mobile ? '100%' : 340, flexShrink: 0, boxSizing: 'border-box',
        overflowY: mobile ? undefined : 'auto', borderLeft: mobile ? undefined : `1px solid ${C.borderLight}`,
      }}>
        <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
          Без ИИ <span style={{ fontWeight: 400, color: C.textMuted }}>· бесплатно, шагом новой версии</span>
        </div>
        {source ? (
          <>
            <TrimFields t={trim} piece={piece} inEditor set={patch => setTrim(prev => ({ ...prev, ...patch }))} />
            <span data-editor-apply="" style={{ display: 'contents' }}>
              <Button size="sm" variant="primary" leftIcon={<Scissors size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                disabled={!ready} loading={busy}
                title={ready ? 'Применить правки новой версией' : 'Выделите кусок или задайте громкость, нарастание, затухание'}
                onClick={() => { void apply(); }}>
                Применить
              </Button>
            </span>
          </>
        ) : null}
      </div>
    </div>
  );

  return (
    <Modal size="fullscreen" title={`Редактор · ${threadName(thread)}`} subtitle={version ? versionLabel(version) : undefined}
      onClose={closeEditor} closeOnBackdrop={false}
      footer={(
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', justifyContent: 'flex-end' }}>
          {!mobile && (
            <span style={{ marginRight: 'auto', fontSize: FS.xs, color: C.textMuted }}>
              Выделенный кусок доступен чипу «Перегенерировать кусок» после закрытия редактора
            </span>
          )}
          <Button size="sm" variant="primary" leftIcon={<Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={closeEditor}>Готово</Button>
        </div>
      )}>
      {body}
    </Modal>
  );
}

export function AudioEditorModal(p: { projectId: string | null; sessionId: string; threadId: string; versionId: string | null }) {
  return <Editor key={`${p.threadId}`} {...p} />;
}
