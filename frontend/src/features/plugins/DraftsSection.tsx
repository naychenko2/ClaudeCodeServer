// Секция «Черновики от агента» (макет, экран 1; ADR-031, шаг 4.4) — в настройках проекта,
// под флагом plugins-build-yourself. Агент собрал плагин в чате; здесь человек видит список
// черновиков, открывает карточку (DraftCard), принимает/отклоняет/возвращает.
//
// «Принять…» открывает СУЩЕСТВУЮЩИЙ ConsentDialog (не новое окно): если сервер вернул
// consentRequired — новые права выделены (newPermissions). «Править»/«Попросить агента
// исправить» кладут готовую реплику в поле ввода чата (через onEdit → cc-draft-reply).

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Sparkles } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { useProjectFeature } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project } from '../../types';
import { onMessage } from '../../lib/signalr';
import {
  api,
  type PluginDraftAcceptResult, type PluginDraftSummary,
} from '../../lib/api';
import { ConsentDialog, type PluginConsentData } from './ConsentDialog';
import { DraftCard } from './DraftCard';
import { useDraftCard } from './useDraftCard';

// Человекочитаемое имя из id пакета {publisher}/{slug}: slug → слова с большой буквы
// (та же логика, что в PluginsProjectSection).
function humanizeId(id: string): string {
  const slug = id.split('/').pop() ?? id;
  return slug.split('-').filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join(' ') || id;
}

// Результат «Принять» → данные существующего диалога согласия. Режим «установить»:
// права по умолчанию — все, кроме net:* (решение В1); consentRequired → newPermissions.
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

// Одна карточка черновика: данные из useDraftCard, действия — колбэки секции.
function DraftCardItem({ summary, onAccept, onReject, onRestore, onEdit }: {
  summary: PluginDraftSummary;
  onAccept: (slug: string) => void;
  onReject: (slug: string) => void;
  onRestore: (slug: string) => void;
  onEdit: (reply: string) => void;
}) {
  const { preview, diff, loading, error, justUpdated, loadDiff } = useDraftCard(summary);
  return (
    <DraftCard
      summary={summary}
      preview={preview}
      diff={diff}
      loading={loading}
      error={error}
      justUpdated={justUpdated}
      onAccept={() => onAccept(summary.slug)}
      onReject={() => onReject(summary.slug)}
      onRestore={() => onRestore(summary.slug)}
      onEdit={onEdit}
      onLoadDiff={loadDiff}
    />
  );
}

export function DraftsSection({ project, onUpdated, onEdit }: {
  project: Project;
  onUpdated?: (updated: Project) => void;
  // «Править»/«Попросить агента исправить»: готовая реплика → поле ввода чата
  onEdit?: (reply: string) => void;
}) {
  const buildYourself = useFeature(FLAGS.pluginsBuildYourself);
  const pluginsAvail = useProjectFeature(project, ProjectFeature.Plugins);

  const [drafts, setDrafts] = useState<PluginDraftSummary[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Диалог согласия для «Принять…» (consentRequired): данные + новые права
  const [consent, setConsent] = useState<{ data: PluginConsentData; newPermissions: string[] } | null>(null);

  const loadList = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setDrafts(await api.plugins.draftsList());
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось загрузить черновики');
    } finally {
      setLoading(false);
    }
  }, []);

  // Первичная загрузка, когда фича включена.
  useEffect(() => {
    if (!buildYourself || !pluginsAvail) return;
    void loadList();
  }, [buildYourself, pluginsAvail, loadList]);

  // Агент создаёт/правит черновик (plugin_draft_changed) → обновить список.
  useEffect(() => {
    if (!buildYourself || !pluginsAvail) return;
    const off = onMessage(msg => {
      if (msg.type === 'plugin_draft_changed') void loadList();
    });
    return off;
  }, [buildYourself, pluginsAvail, loadList]);

  if (!buildYourself || !pluginsAvail) return null;

  // «Принять…»: accept → если consentRequired, диалог согласия с newPermissions;
  // иначе черновик сразу установлен.
  const handleAccept = async (slug: string) => {
    try {
      const res = await api.plugins.draftAccept(slug);
      if (res.consentRequired) {
        setConsent({ data: toConsentDataFromAccept(res), newPermissions: res.newPermissions });
      } else {
        void loadList();
        onUpdated?.(project);
      }
    } catch {
      // ошибка accept: карточка останется активной (состояние не поменялось)
    }
  };
  const handleReject = async (slug: string) => {
    try {
      await api.plugins.draftReject(slug);
      void loadList();
      onUpdated?.(project);
    } catch { /* отклонить не вышло — карточка остаётся */ }
  };
  const handleRestore = async (slug: string) => {
    try {
      await api.plugins.draftRestore(slug);
      void loadList();
      onUpdated?.(project);
    } catch { /* вернуть не вышло */ }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.plan }} />
        <span style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 500, color: C.textHeading }}>
          Черновики от агента
        </span>
      </div>

      {loading && drafts == null ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {[0, 1].map(i => (
            <div key={i} style={{ height: 64, borderRadius: R.lg, background: C.bgInset }} />
          ))}
        </div>
      ) : error ? (
        <Notice tone="danger" icon={AlertTriangle} title="Не удалось загрузить черновики"
          action={<Button variant="ghost" size="xs" onClick={() => void loadList()}>Повторить</Button>}>
          {error}
        </Notice>
      ) : (drafts ?? []).length === 0 ? (
        <div style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg, fontSize: FS.sm, color: C.textMuted }}>
          Черновиков от агента пока нет. Попросите агента собрать плагин — он появится здесь.
        </div>
      ) : (
        (drafts ?? []).map(s => (
          <DraftCardItem
            key={s.slug}
            summary={s}
            onAccept={handleAccept}
            onReject={handleReject}
            onRestore={handleRestore}
            onEdit={r => onEdit?.(r)}
          />
        ))
      )}

      {/* Диалог согласия: «Принять…» при consentRequired. Новые права выделены. */}
      {consent && (
        <ConsentDialog
          data={consent.data}
          mode="install"
          newPermissions={consent.newPermissions}
          onClose={() => setConsent(null)}
          onSubmit={async (permissions, secrets) => {
            // Выдача прав: успех — диалог закрывается. Сбой — ошибка показывается
            // в самом диалоге (он остаётся открытым, как и при прочих его действиях),
            // а список перечитывается в обоих случаях: черновик уже установлен accept'ом
            // и не должен висеть в списке.
            let grantError: Error | null = null;
            try {
              await api.plugins.grant(consent.data.id, permissions, secrets);
            } catch (e: unknown) {
              grantError = e instanceof Error ? e : new Error('Не удалось выдать права');
            }
            void loadList();
            onUpdated?.(project);
            if (grantError) throw grantError;
            setConsent(null);
          }}
          onSaveSecret={async (name, value) => (await api.plugins.setSecret(consent.data.id, name, value)).updatedAt}
        />
      )}
    </div>
  );
}
