namespace ClaudeHomeServer.Services;

// Отказ гейта паузы (W4-HOLD-I): отдельный тип — планировщик отличает отказ по источнику,
// а не пересчётом предиката: plain InvalidOperationException («У задачи нет владельца») рядом
// с удержанной фичей — не отказ по правилу, и не должен попадать в тихую ветку автозапуска.
// База — InvalidOperationException: существующие catch вызывающих не меняются.
//
// Перенесён из TaskManager в Core (дефект 79d0fa9a, волна 4 «Обзор фич»): корневой
// TaskExecutionService бросает этот отказ в двух местах, а ссылка корня на тип из
// вертикали Tasks ловится RootSubsystemBoundaryTests. Тип отказа — общий протокол
// гейта паузы, а не логика Tasks: по образцу TaskUrl живёт в спинке.
public class FeatureHoldException : InvalidOperationException
{
    public FeatureHoldException(string message) : base(message) { }
}
