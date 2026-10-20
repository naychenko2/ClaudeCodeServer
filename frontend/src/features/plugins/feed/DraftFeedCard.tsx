// Карточка черновика в ленте чата (шаг 4.5b, ADR-031). Запись plugin_draft несёт только
// slug; данные (сводка + предпросмотр) — из общего кэша draftsStore (одна загрузка на slug).
//
// Состояния: загрузка; черновик (все состояния DraftCard из 4.4); 404 (чужой/удалённый) —
// нейтральная строка; отклонён — «Отклонён» + «Вернуть»; установлен. «Принять…» — тот же
// ConsentDialog, что в 4.4. Включена только под флагом plugins-build-yourself.

import { useEffect, useRef, useState } from 'react';
import { C, FS } from '../../../lib/design';
import { FLAGS, useFeature } from '../../../lib/featureFlags';
import { api, type PluginDraftAcceptResult } from '../../../lib/api';
import { getCachedProject } from '../../projects/useAllProjects';
import { ConsentDialog, type PluginConsentData } from '../ConsentDialog';
import { DraftCard } from '../DraftCard';
import { refreshDraft, useDraftEntry } from './draftsStore';

// Подсветка «Черновик обновлён агентом» после события, мс (как в useDraftCard)
const JUST_UPDATED_MS = 6000;

// Человекочитаемое имя из id пакета {publisher}/{slug} (та же логика, что в DraftsSection)
function humanizeId(id: string): string {
  const slug = id.split('/').pop() ?? id;
  return slug.split('-').filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join(' ') || id;
}

// Текст ошибки действия: message сервера (409/5xx) или сбой сети; не-Error — общий текст
function errText(e: unknown): string {
  return e instanceof Error ? e.message : 'Не удалось выполнить действие';
}

// Результат «Принять» → данные существующего диалога согласия (режим «установить»)
function toConsentDataFromAccept(res: PluginDraftAcceptResult): PluginConsentData {
  return {
    id: res.id,
    name: humanizeId(res.id),
    version: res.version,
    path: res.source.startsWith('upload:') ? res.source.slice('upload:'.length) : undefined,
    sha256: res.sha256 ? res.sha256.slice(0, 8) : undefined,
    contributions: [],
    requested: res.requested,
    secrets: res.secrets.map(s => ({ name: s.name, required: true, set: s.set, setAt: s.updatedAt ?? null })),
    granted: res.granted,
  };
}

// Нейтральная строка в ленте (404 / без флага / загрузка): как тихие строки соседних модулей
function NeutralLine({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      alignSelf: 'center', maxWidth: 420, textAlign: 'center', overflowWrap: 'anywhere',
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    }}>{children}</div>
  );
}

export function DraftFeedCard({ slug, fallback, projectId }: {
  slug: string;
  fallback: string | null;
  projectId: string | null;
}) {
  const buildYourself = useFeature(FLAGS.pluginsBuildYourself);
  const entry = useDraftEntry(slug);

  // Подсветка «Черновик обновлён агентом»: локальный таймер на смену ревизии
  const [justUpdated, setJustUpdated] = useState(false);
  const prevRev = useRef(entry.revision);
  useEffect(() => {
    if (entry.revision !== prevRev.current) {
      prevRev.current = entry.revision;
      setJustUpdated(true);
      const t = setTimeout(() => setJustUpdated(false), JUST_UPDATED_MS);
      return () => clearTimeout(t);
    }
    return undefined;
  }, [entry.revision]);

  // «Принять…»: consentRequired → диалог согласия с newPermissions + режим (MAJOR 4:
  // обновление → modify, первая установка → install)
  const [consent, setConsent] = useState<{ data: PluginConsentData; newPermissions: string[]; mode: 'install' | 'modify' } | null>(null);
  // Ошибка действия («Принять…», «Вернуть»): текст сервера, пока действие не повторят
  const [actionError, setActionError] = useState<string | null>(null);

  // Без флага — нейтральная строка (как делают соседние модули), не карточка
  if (!buildYourself) return fallback ? <NeutralLine>{fallback}</NeutralLine> : null;

  // Список ещё не прочитан — загрузка
  if (!entry.listResolved) return <NeutralLine>Загружаем черновик…</NeutralLine>;

  // Slug не в списке владельца — 404 (чужой/удалённый): нейтральная строка, без ошибок и кнопок
  if (!entry.summary) return <NeutralLine>{`Черновик «${slug}» больше недоступен`}</NeutralLine>;

  const summary = entry.summary;

  const handleAccept = async () => {
    setActionError(null);
    try {
      const res = await api.plugins.draftAccept(slug);
      if (res.consentRequired) {
        // MAJOR 4: обновление (isUpdate) → modify (новые права не отмечены, выданные —
        // отмечены); первая установка (isUpdate false/undefined) → install (все запрошенные,
        // кроме net:*). Иначе новое право при обновлении приходило бы уже отмеченным.
        const mode: 'install' | 'modify' = res.isUpdate ? 'modify' : 'install';
        setConsent({ data: toConsentDataFromAccept(res), newPermissions: res.newPermissions, mode });
      } else {
        void refreshDraft(slug);
      }
    } catch (e: unknown) { setActionError(errText(e)); }
  };
  const handleReject = async () => {
    // «Отклонить»: как «Принять…»/«Вернуть» — ошибка (409/сеть/5xx) видна на карточке
    setActionError(null);
    try { await api.plugins.draftReject(slug); void refreshDraft(slug); }
    catch (e: unknown) { setActionError(errText(e)); }
  };
  const handleRestore = async () => {
    setActionError(null);
    try { await api.plugins.draftRestore(slug); void refreshDraft(slug); } catch (e: unknown) { setActionError(errText(e)); }
  };
  // «Править»/«Попросить агента исправить»: готовая реплика → композер активного чата (cc-draft-reply)
  const handleEdit = (reply: string) => {
    const project = projectId ? getCachedProject(projectId) : undefined;
    window.dispatchEvent(new CustomEvent('cc-draft-reply', { detail: { project, reply } }));
  };

  return (
    <>
      <DraftCard
        summary={summary}
        preview={entry.preview}
        diff={null}
        loading={entry.loading}
        error={entry.error}
        actionError={actionError}
        justUpdated={justUpdated}
        onAccept={() => void handleAccept()}
        onReject={() => void handleReject()}
        onRestore={() => void handleRestore()}
        onEdit={handleEdit}
        onLoadDiff={() => { /* diff — по требованию в шторке; в ленте не грузим заранее */ }}
        // «Открыть в «Плагинах»» (установлен): настройки проекта (секция плагинов); WorkspacePage
        // слушает cc-open-plugins и раскрывает диалог. Вне проекта (projectId null) настроек
        // проекта нет — кнопку скрываем, а не показываем мёртвой (ChatsPage cc-open-plugins не
        // слушает, и в этом контексте вести некуда).
        onOpenInPlugins={projectId
          ? () => window.dispatchEvent(new CustomEvent('cc-open-plugins', { detail: { projectId } }))
          : undefined}
      />
      {consent && (
        <ConsentDialog
          data={consent.data}
          mode={consent.mode}
          newPermissions={consent.newPermissions}
          onClose={() => setConsent(null)}
          onSubmit={async (permissions, secrets) => {
            // Выдача прав: успех — диалог закрывается. Сбой — ошибка показывается
            // в самом диалоге (он остаётся открытым, как и при прочих его действиях),
            // а карточка перечитывается в обоих случаях: черновик уже установлен accept'ом.
            let grantError: Error | null = null;
            try {
              await api.plugins.grant(consent.data.id, permissions, secrets);
            } catch (e: unknown) {
              grantError = e instanceof Error ? e : new Error('Не удалось выдать права');
            }
            void refreshDraft(slug);
            // throw — диалог остаётся открытым, чтобы показать ошибку; карточка уже перечитана
            if (grantError) throw grantError;
            setConsent(null);
          }}
          onSaveSecret={async (name, value) => (await api.plugins.setSecret(consent.data.id, name, value)).updatedAt}
        />
      )}
    </>
  );
}
