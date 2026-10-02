// Global usings for Sbroenne.WindowsMcp
// Required for source generator compatibility - XmlToDescriptionGenerator only adds
// System.ComponentModel and ModelContextProtocol.Server namespaces to generated files.
// The Models namespace contains enum types (KeyboardAction, MouseAction, WindowAction)
// that must be accessible in the generated partial method declarations.

global using Sbroenne.WindowsMcp.Models;

// GDI+ (Bitmap, Graphics, Rectangle): раньше глобальный using давал UseWindowsForms
global using System.Drawing;

// Гейт рук (HandsGate/HandsPolicy) — в каждом инструменте форка
global using ClaudeHomeServer.HandsBridge;
global using ClaudeHomeServer.HandsBridge.Policy;
