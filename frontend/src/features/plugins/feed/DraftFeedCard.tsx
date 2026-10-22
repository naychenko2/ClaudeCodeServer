// Карточка черновика в ленте чата (шаг 4.5b, ADR-031). Запись plugin_draft несёт только
// slug; данные (сводка + предпросмотр) — из общего кэша draftsStore (одна загрузка на slug).
//
// Состояния: загрузка; черновик (все состояния DraftCard из 4.4); 404 (чужой/удалённый) —
// нейтральная строка; отклонён — «Отклонён» + «Вернуть»; установлен. «Принять…» — тот же
// ConsentDialog, что в 4.4. Включена только под флагом plugins-build-yourself.

import { useEffect, useRef, useState } from 'react';
import { C, FS } from '../../../lib/design';
import { FLAGS, useFeature } from '../../../lib/featureFlags';
import { api } from '../../../lib/api';
import { ConsentDialog, type PluginConsentData } from '../ConsentDialog';
import { DraftCard } from '../DraftCard';
import { toConsentDataFromAccept } from '../draftModel';
import { refreshDraft, useDraftEntry } from './draftsStore';

// Подсветка «Черновик обновлён агентом» после события, мс (как в useDraftCard)
const JUST_UPDATED_MS = 6000;

// Текст ошибки действия: message сервера (409/5xx) или сбой сети; не-Error — общий текст
function errText(e: unknown): string {
  return e instanceof Error ? e.message : 'Не удалось выполнить действие';
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

export function DraftFeedCard({ slug, fallback, projectId, chatId }: {
  slug: string;
  fallback: string | null;
  projectId: string | null;
  // Id чата, в чьей ленте нарисована карточка: «Править»/«Просить агента исправить»
  // кладут реплику в композер именно этого чата (cc-draft-reply несёт chatId, слушатели
  // принимают, только если chatId = их активный чат). Проект не сравниваем: черновик может
  // быть из проекта, а чат — вне проекта (и наоборот).
  chatId: string | null;
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
  const [consent, setConsent] = useState<{ data: PluginConsentData; newPermissions: string[]; inheritedPermissions: string[]; mode: 'install' | 'modify' } | null>(null);
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
        setConsent({ data: toConsentDataFromAccept(res), newPermissions: res.newPermissions, inheritedPermissions: res.inheritedPermissions ?? [], mode });
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
  // «Править»/«Попросить агента исправить»: готовая реплика → композер ТОГО чата, в чьей
  // ленте нарисована карточка (cc-draft-reply несёт chatId; слушатели принимают, только если
  // chatId = их активный чат). Проект не сравниваем: черновик может быть из проекта, а чат —
  // вне проекта (и наоборот) — сравнение проектов роняло реплику в чате вне проекта.
  const handleEdit = (reply: string) => {
    window.dispatchEvent(new CustomEvent('cc-draft-reply', { detail: { chatId, reply } }));
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
          inheritedPermissions={consent.inheritedPermissions}
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
