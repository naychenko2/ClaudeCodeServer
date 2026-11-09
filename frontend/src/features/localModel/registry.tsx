// Регистрация фичи «Локальные модели»: side-effect-импорт манифеста в реестр.
// Статическая фича (пока не MF-remote): строка регистрации — в lib/subsystems/registry.ts.

import { registerSubsystem } from 'aihome_shell/kit';
import { manifest } from './manifest';

registerSubsystem(manifest);
