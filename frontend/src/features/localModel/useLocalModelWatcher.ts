// Сторож «локальная модель настроена»: хук, всегда вызываемый на страницах
// WorkspacePage и ChatsPage (в начале компонента, до ранних return).
// Публикует роль страницы (auth.role — тот же источник, что в HubHeader),
// кормит стор configured лёгким 60-с опросом и ПОДПИСЫВАЕТ страницу на
// configured/роль: isAvailable панели — геттер, который читается в рендере
// страницы, и без подписки смена configured не перерисовала бы рельс.
// Без флага или не-админу — ни одного запроса к /api/local-model/*.

import { useEffect } from 'react';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import {
  setLocalModelAdmin, shouldPollConfigured, useLocalModelAdmin,
  useLocalModelConfigured, useLocalModelConfiguredPolling,
} from './useLocalModelStats';

export function useLocalModelWatcher(isAdmin: boolean): void {
  const flag = useFeature(FLAGS.localModelStats);
  useEffect(() => { setLocalModelAdmin(isAdmin); }, [isAdmin]);
  useLocalModelConfiguredPolling(shouldPollConfigured(flag, isAdmin));
  // Подписка страницы на стор (значение не нужно — нужен ререндер)
  useLocalModelConfigured();
  useLocalModelAdmin();
}
