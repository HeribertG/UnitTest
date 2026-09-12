// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class SseStatusChunkTests
{
    private const string StatusEventName = "status";
    private const long Elapsed = 4200;

    [Test]
    public void Status_SetsTypeStageAndElapsed()
    {
        // Arrange & Act
        var chunk = SseChunk.Status(SseStatusStages.AssemblingToolset, Elapsed);

        // Assert
        chunk.Type.ShouldBe(SseChunkType.Status);
        chunk.Stage.ShouldBe(SseStatusStages.AssemblingToolset);
        chunk.ElapsedMs.ShouldBe((long?)Elapsed);
        chunk.Iteration.ShouldBeNull();
    }

    [Test]
    public void Status_CarriesIterationWhenGiven()
    {
        // Arrange & Act
        var chunk = SseChunk.Status(SseStatusStages.CallingModel, Elapsed, iteration: 2);

        // Assert
        chunk.Iteration.ShouldBe((int?)2);
    }

    [Test]
    public void Status_SerializesStageAndElapsedInCamelCase()
    {
        // Arrange
        var chunk = SseChunk.Status(SseStatusStages.CallingModel, Elapsed, iteration: 1);

        // Act
        var json = JsonSerializer.Serialize(chunk, SseChunkJson.Options);

        // Assert
        json.ShouldContain("\"stage\":\"calling_model\"");
        json.ShouldContain("\"elapsedMs\":4200");
        json.ShouldContain("\"iteration\":1");
    }

    [Test]
    public void Status_WithoutElapsed_OmitsTheFieldEntirely()
    {
        // Arrange
        var chunk = SseChunk.Status(SseStatusStages.PreparingContext);

        // Act
        var json = JsonSerializer.Serialize(chunk, SseChunkJson.Options);

        // Assert
        json.ShouldNotContain("elapsedMs");
        json.ShouldNotContain("iteration");
    }

    [Test]
    public void ContentChunk_CarriesNoStatusFields()
    {
        // Arrange
        var chunk = SseChunk.Content("hello");

        // Act
        var json = JsonSerializer.Serialize(chunk, SseChunkJson.Options);

        // Assert
        json.ShouldNotContain("stage");
        json.ShouldNotContain("elapsedMs");
    }

    [Test]
    public void EventName_ForStatus_IsStatus()
    {
        // Arrange & Act & Assert
        SseEventNames.For(SseChunkType.Status).ShouldBe(StatusEventName);
    }

    [Test]
    public void EventName_ForEveryChunkType_IsMapped()
    {
        // Arrange
        var types = Enum.GetValues<SseChunkType>();

        // Act & Assert
        foreach (var type in types)
        {
            SseEventNames.For(type).ShouldNotBe(SseEventNames.Unknown, $"{type} has no wire event name");
        }
    }

    [Test]
    public void EventName_ForUnknownValue_FallsBackInsteadOfThrowing()
    {
        // Arrange
        var undefined = (SseChunkType)999;

        // Act & Assert
        SseEventNames.For(undefined).ShouldBe(SseEventNames.Unknown);
    }

    // The frontend maps these keys to localized text; renaming one silently breaks the progress
    // display instead of failing a build, so the wire spelling is pinned here.
    [Test]
    public void StageKeys_AreStableSnakeCaseWireValues()
    {
        // Arrange & Act & Assert
        SseStatusStages.AssemblingToolset.ShouldBe("assembling_toolset");
        SseStatusStages.PreparingContext.ShouldBe("preparing_context");
        SseStatusStages.ResolvingRecipe.ShouldBe("resolving_recipe");
        SseStatusStages.CallingModel.ShouldBe("calling_model");
        SseStatusStages.ExecutingTool.ShouldBe("executing_tool");
    }
}
