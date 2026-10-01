// Кнопка «Пересоздать · цена» клона MiniMax (ADR-021 §5): пересоздание платное, поэтому сначала
// котировка (денег не тратит) и цена на кнопке, задача — только по нажатию человека. Котировку можно
// отдать готовой — её приносит отказ запуска voice_clone_stale / voice_clone_missing.

import { useCallback, useEffect, useState } from 'react';
import { Button, showToast, FS } from 'aihome_shell/kit';
import type { AudioQuote } from '../api';
import { voicesApi } from './api';
import { quotePrice, recreateAction } from './model';

const errText = (e: unknown) => (e as Error)?.message || 'Запрос не выполнен';

export function RecreateButton({ scope, slug, quote: given = null, onStarted }: {
  scope: string;
  slug: string;
  quote?: AudioQuote | null;
  onStarted?: () => void;
}) {
  const [quote, setQuote] = useState<AudioQuote | null>(given);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [started, setStarted] = useState(false);

  const load = useCallback(() => {
    setQuote(null);
    setError(null);
    voicesApi.recreateQuote(scope, slug).then(setQuote, e => setError(errText(e)));
  }, [scope, slug]);

  useEffect(() => {
    if (given) setQuote(given);
    else load();
  }, [given, load]);

  if (started) return <span style={{ fontSize: FS.xs }}>Клон пересоздаётся — голос заработает, когда задача закончится</span>;

  const action = recreateAction(quotePrice(quote), error);
  const press = async () => {
    if (!quote || busy) return;
    setBusy(true);
    try {
      await voicesApi.recreate(scope, slug, quote.quoteId);
      setStarted(true);
      showToast('Пересоздаём клон MiniMax', 'Задача видна в очереди звука', 'info');
      onStarted?.();
    } catch (e) {
      showToast(errText(e), '', 'error');
      // Котировка истекла или израсходована — берём новую, цена снова будет на кнопке
      load();
    } finally {
      setBusy(false);
    }
  };
  return (
    <span data-recreate={slug} style={{ display: 'contents' }}>
      <Button size="sm" variant="secondary" disabled={action.disabled || busy} loading={busy}
        title={action.hint ?? undefined} onClick={() => { void press(); }}>
        {action.label}
      </Button>
      {action.hint && <span style={{ fontSize: FS.xs }}>{action.hint}</span>}
    </span>
  );
}
