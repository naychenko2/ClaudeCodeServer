using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Tests.Services.Memory;

public class PersonaDatasetNameTests
{
    [Fact]
    public void DatasetName_ОдинаковыйHandleРазныеПерсоны_ИменаРазные()
    {
        var a = new Persona { Id = "aaaaaaaa-1111", Handle = "kira" };
        var b = new Persona { Id = "bbbbbbbb-2222", Handle = "kira" };

        var na = PersonaMemoryService.DatasetName("an", a);
        var nb = PersonaMemoryService.DatasetName("an", b);

        Assert.NotEqual(na, nb);
        Assert.Equal("an:persona:kira:aaaaaaaa", na);
    }
}
