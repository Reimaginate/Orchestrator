using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Reimaginate.Mediator;
using Reimaginate.Orchestrator.Common.Executors;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class RequestExecutorJsonNodeBindingTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        RequestExecutor<JsonNodeBindingRequest, JsonNodeBindingResponse>
            .CreateRequestSerializerOptions(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

    [Fact(DisplayName = "Request binding preserves JsonNode shapes, required init properties, nulls, and source isolation")]
    public void S00001()
    {
        var source = CreateRequestObject(7);
        var sourceBefore = source.ToJsonString();

        var request = RequestExecutor<JsonNodeBindingRequest, JsonNodeBindingResponse>
            .DeserializeRequest(source, JsonOptions);

        request.Name.Should().Be("request-7");
        request.AbstractNode.Should().BeAssignableTo<JsonValue>();
        request.AbstractNode.GetValue<int>().Should().Be(7);
        request.ArrayNode.Should().HaveCount(2);
        request.ObjectNode["NoSync"]!.GetValue<string>().Should().Be("legacy");
        request.ObjectNode["noSync"]!.GetValue<bool>().Should().BeFalse();
        request.NullNode.Should().BeNull();

        ReferenceEquals(request.AbstractNode, source["abstractnode"]).Should().BeFalse();
        ReferenceEquals(request.ArrayNode, source["arraynode"]).Should().BeFalse();
        ReferenceEquals(request.ObjectNode, source["objectnode"]).Should().BeFalse();

        request.ArrayNode[0]!["id"] = "changed";
        request.ObjectNode["NoSync"] = "changed";

        source.ToJsonString().Should().Be(sourceBefore);
        source["arraynode"]![0]!["id"]!.GetValue<string>().Should().Be("A-7");
        source["objectnode"]!["NoSync"]!.GetValue<string>().Should().Be("legacy");
    }

    [Fact(DisplayName = "Request JsonNode binding is isolated across concurrent deserializations of one source")]
    public async Task S00002()
    {
        var source = CreateRequestObject(11);
        var sourceBefore = source.ToJsonString();

        var requests = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() =>
                RequestExecutor<JsonNodeBindingRequest, JsonNodeBindingResponse>
                    .DeserializeRequest(source, JsonOptions))));

        requests.Select(request => request.ArrayNode)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Should().HaveCount(requests.Length);
        requests.Select(request => request.ObjectNode)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Should().HaveCount(requests.Length);
        requests.Should().OnlyContain(request =>
            !ReferenceEquals(request.ArrayNode, source["arraynode"])
            && !ReferenceEquals(request.ObjectNode, source["objectnode"]));

        for (var index = 0; index < requests.Length; index++)
        {
            requests[index].ArrayNode[0]!["worker"] = index;
        }

        requests.Select(request => request.ArrayNode[0]!["worker"]!.GetValue<int>())
            .Should().Equal(Enumerable.Range(0, requests.Length));
        source.ToJsonString().Should().Be(sourceBefore);
    }

    [Fact(DisplayName = "Request binding retains concrete JsonArray and required-member validation")]
    public void S00003()
    {
        var wrongShape = CreateRequestObject(3);
        wrongShape["arraynode"] = new JsonObject();

        var wrongShapeAction = () =>
            RequestExecutor<JsonNodeBindingRequest, JsonNodeBindingResponse>
                .DeserializeRequest(wrongShape, JsonOptions);
        wrongShapeAction.Should().Throw<JsonException>();

        var missingRequired = CreateRequestObject(3);
        missingRequired.Remove("objectnode");

        var missingRequiredAction = () =>
            RequestExecutor<JsonNodeBindingRequest, JsonNodeBindingResponse>
                .DeserializeRequest(missingRequired, JsonOptions);
        missingRequiredAction.Should().Throw<JsonException>()
            .WithMessage("*required properties*");
    }

    private static JsonObject CreateRequestObject(int value)
        => new()
        {
            ["name"] = $"request-{value}",
            ["abstractnode"] = value,
            ["arraynode"] = new JsonArray(
                new JsonObject { ["id"] = $"A-{value}" },
                new JsonObject { ["id"] = $"B-{value}" }),
            ["objectnode"] = new JsonObject
            {
                ["NoSync"] = "legacy",
                ["noSync"] = false
            },
            ["nullnode"] = null
        };

    public sealed class JsonNodeBindingRequest : IRequest<JsonNodeBindingResponse>
    {
        public required string Name { get; init; }

        public required JsonNode AbstractNode { get; init; }

        public required JsonArray ArrayNode { get; init; }

        public required JsonObject ObjectNode { get; init; }

        public required JsonNode? NullNode { get; init; }
    }

    public sealed class JsonNodeBindingResponse;
}
