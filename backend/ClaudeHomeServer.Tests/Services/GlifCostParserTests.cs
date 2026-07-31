using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

public class GlifCostParserTests
{
    [Fact]
    public void TryParse_СтартComposeProject_НеСчитаетГенерацией()
    {
        var json = """
            {
              "content": [{ "type": "text", "text": "Project job started" }],
              "structuredContent": {
                "outputType": "project_job_started",
                "projectId": "p-start",
                "jobId": "job-start",
                "status": "working",
                "pollIntervalSeconds": 5
              }
            }
            """;

        GlifCostParser.TryParse(json).Should().BeNull("compose_project на старте ещё не завершён");
    }

    [Fact]
    public void TryParse_GetJobStatusWorking_НеСчитаетГенерацией()
    {
        var json = """
            {
              "content": [{ "type": "text", "text": "Still working" }],
              "structuredContent": {
                "outputType": "job_status",
                "projectId": "p-working",
                "jobId": "job-working",
                "status": "working",
                "pollIntervalSeconds": 5
              }
            }
            """;

        GlifCostParser.TryParse(json).Should().BeNull("промежуточный опрос не завершён");
    }

    [Fact]
    public void TryParse_GetJobStatusRunning_НеСчитаетГенерацией()
    {
        // Альтернативное имя статуса, которое могут возвращать другие боты glif.
        var json = """
            {
              "content": [{ "type": "text", "text": "Still running" }],
              "structuredContent": {
                "outputType": "job_status",
                "projectId": "p-running",
                "jobId": "job-running",
                "status": "running"
              }
            }
            """;

        GlifCostParser.TryParse(json).Should().BeNull();
    }

    [Fact]
    public void TryParse_ПолныйJsonСMeta_ИзвлекаетВсеПоля()
    {
        var json = """
            {
              "content": [
                { "type": "text", "text": "Done" },
                { "type": "resource_link", "name": "icon.jpg", "uri": "https://glifusercontent.com/i:r/abc.jpg" }
              ],
              "structuredContent": {
                "outputType": "image",
                "projectId": "p-1",
                "jobId": "job-1",
                "status": "completed",
                "media": [
                  { "url": "https://glifusercontent.com/i:r/abc.jpg", "title": "icon.jpg", "kind": "image" }
                ]
              },
              "_meta": {
                "glif": {
                  "outputType": "image",
                  "projectId": "p-1",
                  "jobId": "job-1",
                  "status": "completed",
                  "subtractedCredits": "4.6",
                  "toolExecutions": [
                    { "toolExecutionId": 1, "nativeToolId": "image_seedream_v45_v1", "sellingPriceCredits": "4.4" }
                  ]
                }
              }
            }
            """;

        var msg = GlifCostParser.TryParse(json);

        msg.Should().NotBeNull();
        msg!.JobId.Should().Be("job-1");
        msg.OutputType.Should().Be("image");
        msg.MediaCount.Should().Be(1);
        msg.Credits.Should().BeApproximately(4.6, 0.001);
        msg.Model.Should().Be("image_seedream_v45_v1");
    }

    [Fact]
    public void TryParse_СплющенныйGetJobStatus_ИзвлекаетJobIdMediaCredits()
    {
        var tail = /*lang=json,strict*/"""{"outputType":"project_update","projectId":"p-2","jobId":"job-2","projectUrl":"https://glif.app/chat/p-2","status":"completed","media":[{"url":"https://glifusercontent.com/i:r/x.png","title":"x.png","kind":"image"}]}""";
        var flat = $"[Resource link: x.png] https://glifusercontent.com/i:r/x.png\r\nShowing 1 media item(s). View on Glif: https://glif.app/chat/p-2\r\n{tail}";

        var msg = GlifCostParser.TryParse(flat);

        msg.Should().NotBeNull();
        msg!.JobId.Should().Be("job-2");
        msg.OutputType.Should().Be("project_update");
        msg.MediaCount.Should().Be(1);
    }

    [Fact]
    public void TryParse_ViewMediaБезJobId_НеСчитаетГенерацией()
    {
        var tail = /*lang=json,strict*/"""{"outputType":"media_view","projectId":"p-3","projectUrl":"https://glif.app/chat/p-3","status":"completed","media":[{"url":"https://glifusercontent.com/i:r/y.png","title":"y.png","kind":"image"}]}""";
        var flat = $"[Resource link: y.png] https://glifusercontent.com/i:r/y.png\r\n{tail}";

        var msg = GlifCostParser.TryParse(flat);

        msg.Should().BeNull("view_media не несёт jobId — повторно учитывать генерацию нельзя");
    }

    [Fact]
    public void TryParse_БезПризнаковGlif_ВозвращаетNull()
    {
        GlifCostParser.TryParse("{\"foo\":\"bar\"}").Should().BeNull();
        GlifCostParser.TryParse("обычный текст результата").Should().BeNull();
    }

    [Fact]
    public void TryParse_КредитыВЧисле_ТожеПарсятся()
    {
        var json = """{"_meta":{"glif":{"jobId":"job-3","outputType":"image","subtractedCredits":12.5,"status":"completed"}},"structuredContent":{"jobId":"job-3","outputType":"image","media":[]}}""";

        var msg = GlifCostParser.TryParse(json);

        msg.Should().NotBeNull();
        msg!.Credits.Should().BeApproximately(12.5, 0.001);
    }

    [Fact]
    public void TryParse_НетКредитов_СообщениеБезCredits()
    {
        var json = """{"_meta":{"glif":{"jobId":"job-4","outputType":"image","status":"completed"}},"structuredContent":{"jobId":"job-4","outputType":"image","media":[]}}""";

        var msg = GlifCostParser.TryParse(json);

        msg.Should().NotBeNull();
        msg!.Credits.Should().BeNull();
    }
}
