export interface DifyConfig {
  apiUrl: string;
  apiKey: string;
  defaultDatasetId?: string;
  searchOnly: boolean;
}

export function loadConfig(): DifyConfig {
  const apiUrl = process.env['DIFY_API_URL'];
  const apiKey = process.env['DIFY_API_KEY'];

  if (!apiUrl) throw new Error('DIFY_API_URL не задан');
  if (!apiKey) throw new Error('DIFY_API_KEY не задан');

  return {
    apiUrl: apiUrl.replace(/\/$/, ''),
    apiKey,
    defaultDatasetId: process.env['DIFY_DEFAULT_DATASET_ID'] || undefined,
    searchOnly: process.env['DIFY_SEARCH_ONLY'] === 'true',
  };
}
