// Карта значков проекта (ADR-009 §5): единственная точка, где имя из белого списка
// связано с реальным lucide-компонентом. Серверная копия множества имён —
// `LucideGlyphs.All` в Services/ProjectIcons/ProjectIconGlyphService.cs; равенство
// держит тест-сторож на бэке (§11.5). Пополнение — обычный коммит в оба места.
//
// Статические импорты — не стиль, а требование: динамический `import(\`lucide-react/${name}\`)`
// убивает tree-shaking и тащит в бандл весь набор (тысячи иконок). Компилятор проверяет
// каждое имя: несуществующей иконки в карте не будет.

import {
  House, Sofa, Bed, Key, Wrench, Hammer, Plug, Lightbulb,
  Wallet, PiggyBank, Banknote, CreditCard, Receipt, Coins,
  ChartLine, ChartPie, ChartColumn, Table, Gauge, Target,
  Code, Terminal, GitBranch, Database, Server, Cpu, Bug, Boxes,
  Book, BookOpen, GraduationCap, Pencil, NotebookPen, Brain,
  Heart, Activity, Dumbbell, Stethoscope, Pill, Apple, Leaf,
  Utensils, Coffee, ChefHat, ShoppingCart, Cake,
  Plane, Car, TrainFront, Bike, Map, MapPin, Compass, Tent,
  Camera, Image, Film, Music, Mic, Headphones, Palette, Brush,
  Briefcase, Building2, Store, Factory, Calendar, Clock, Users,
  Rocket, Atom, FlaskConical, Microscope, Telescope,
  Gamepad2, Puzzle, Trophy, Dice5, Flag, Star, Sparkles,
  Folder, FileText, Layers, Shield, Lock, Globe, Bot, Zap,
} from 'lucide-react';

export const GLYPHS = {
  // дом и быт
  'house': House, 'sofa': Sofa, 'bed': Bed, 'key': Key, 'wrench': Wrench, 'hammer': Hammer, 'plug': Plug, 'lightbulb': Lightbulb,
  // деньги
  'wallet': Wallet, 'piggy-bank': PiggyBank, 'banknote': Banknote, 'credit-card': CreditCard, 'receipt': Receipt, 'coins': Coins,
  // аналитика
  'chart-line': ChartLine, 'chart-pie': ChartPie, 'chart-column': ChartColumn, 'table': Table, 'gauge': Gauge, 'target': Target,
  // код
  'code': Code, 'terminal': Terminal, 'git-branch': GitBranch, 'database': Database, 'server': Server, 'cpu': Cpu, 'bug': Bug, 'boxes': Boxes,
  // учёба
  'book': Book, 'book-open': BookOpen, 'graduation-cap': GraduationCap, 'pencil': Pencil, 'notebook-pen': NotebookPen, 'brain': Brain,
  // здоровье
  'heart': Heart, 'activity': Activity, 'dumbbell': Dumbbell, 'stethoscope': Stethoscope, 'pill': Pill, 'apple': Apple, 'leaf': Leaf,
  // еда
  'utensils': Utensils, 'coffee': Coffee, 'chef-hat': ChefHat, 'shopping-cart': ShoppingCart, 'cake': Cake,
  // дорога
  'plane': Plane, 'car': Car, 'train-front': TrainFront, 'bike': Bike, 'map': Map, 'map-pin': MapPin, 'compass': Compass, 'tent': Tent,
  // медиа
  'camera': Camera, 'image': Image, 'film': Film, 'music': Music, 'mic': Mic, 'headphones': Headphones, 'palette': Palette, 'brush': Brush,
  // работа
  'briefcase': Briefcase, 'building-2': Building2, 'store': Store, 'factory': Factory, 'calendar': Calendar, 'clock': Clock, 'users': Users,
  // наука
  'rocket': Rocket, 'atom': Atom, 'flask-conical': FlaskConical, 'microscope': Microscope, 'telescope': Telescope,
  // досуг
  'gamepad-2': Gamepad2, 'puzzle': Puzzle, 'trophy': Trophy, 'dice-5': Dice5, 'flag': Flag, 'star': Star, 'sparkles': Sparkles,
  // прочее
  'folder': Folder, 'file-text': FileText, 'layers': Layers, 'shield': Shield, 'lock': Lock, 'globe': Globe, 'bot': Bot, 'zap': Zap,
} as const;
