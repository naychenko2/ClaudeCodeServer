namespace ClaudeHomeServer.Tests.Composition;

// Коллекция для тестов, которые трогают process-wide состояние (например,
//
// env-переменные) и поэтому не могут идти параллельно с другими тестами.
// DisableParallelization запрещает xUnit запускать её параллельно с любыми
// другими коллекциями и параллельными тестами внутри неё.
[CollectionDefinition("InspectionSerial", DisableParallelization = true)]
public class InspectionSerialCollection
{
}