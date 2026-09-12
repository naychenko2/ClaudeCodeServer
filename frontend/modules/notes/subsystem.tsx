// MF entry: реэкспорт манифеста подсистемы «Заметки» для Module Federation.
//
// Хост загружает этот модуль через loadRemote('aihome_notes/subsystem') и получает
// SubsystemManifest (manifest + tab + slots). Регистрация в реестре слотов —
// ответственность хоста (registerSubsystem), не этого модуля.
//
// Импортируем из ../../src/features/notes/manifest.tsx — тот же файл, что
// использует side-effect-регистрация, но без side-effect (чистый экспорт).

export { manifest as subsystem } from '../../src/features/notes/manifest';
