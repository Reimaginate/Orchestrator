using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using NSubstitute;
using Reimaginate.Orchestrator.Common.Helpers;
using Xunit;

namespace Reimaginate.Orchestrator.Test.Unit.Tests.Helpers;

public class WorkflowEventInputTransformerTests
{
    [Fact(DisplayName = "Workflow input transformer resolves workflow metadata from runtime trace context")]
    public void S00001()
    {
        var payload = new JsonObject
        {
            ["Value"] = "payload"
        };

        var template = new JsonObject
        {
            ["RootInstanceId"] = "$.workflowInstanceId",
            ["NestedInstanceId"] = "$.workflow.workflowInstanceId",
            ["WorkflowType"] = "$.workflow.workflowType",
            ["Raw"] = "$"
        };

        var workflowContext = Substitute.For<IWorkflowContext>();
        workflowContext.TraceContext.Returns(new Dictionary<string, string>
        {
            ["runId"] = "wf-123",
            ["workflowType"] = "unit.test.workflow.yaml"
        });

        var transformed = WorkflowEventInputTransformer.Transform(payload, template, workflowContext);

        transformed["RootInstanceId"]?.GetValue<string>().Should().Be("wf-123");
        transformed["NestedInstanceId"]?.GetValue<string>().Should().Be("wf-123");
        transformed["WorkflowType"]?.GetValue<string>().Should().Be("unit.test.workflow.yaml");
        JsonNode.DeepEquals(transformed["Raw"], payload).Should().BeTrue();
    }

    [Fact(DisplayName = "Workflow input transformer append expression appends one cloned item without mutating source")]
    public void S00002()
    {
        var sourceItem = new JsonObject
        {
            ["Name"] = "Original"
        };

        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(sourceItem),
            ["NestedItem"] = new JsonArray("B", "C")
        };

        var template = new JsonObject
        {
            ["Items"] = "append($.Items, $.NestedItem)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var items = transformed["Items"] as JsonArray;
        items.Should().NotBeNull();
        items!.Count.Should().Be(2);
        items[0]?["Name"]?.GetValue<string>().Should().Be("Original");
        items[1].Should().BeAssignableTo<JsonArray>();
        items[1]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("B", "C");

        items[0]!["Name"] = "Changed";
        payload["Items"]![0]!["Name"]?.GetValue<string>().Should().Be("Original");
        payload["Items"]!.AsArray().Count.Should().Be(1);
    }

    [Fact(DisplayName = "Workflow input transformer append expression treats null and missing arrays as empty")]
    public void S00003()
    {
        var payload = new JsonObject
        {
            ["Value"] = "A"
        };

        var template = new JsonObject
        {
            ["FromMissing"] = "append($.MissingItems, $.Value)",
            ["FromNull"] = "append(null, $.Value)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["FromMissing"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A");
        transformed["FromNull"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A");
    }

    [Fact(DisplayName = "Workflow input transformer insert expression clamps low middle and high positions")]
    public void S00004()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "C"),
            ["Start"] = "Start",
            ["Middle"] = "B",
            ["End"] = "End"
        };

        var template = new JsonObject
        {
            ["AtStart"] = "insert($.Items, $.Start, -10)",
            ["InMiddle"] = "insert($.Items, $.Middle, 1)",
            ["AtEnd"] = "insert($.Items, $.End, 999)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["AtStart"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("Start", "A", "C");
        transformed["InMiddle"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
        transformed["AtEnd"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C", "End");
        payload["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C");
    }

    [Fact(DisplayName = "Workflow input transformer insert expression accepts integer string positions")]
    public void S00005()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "C"),
            ["Position"] = "1"
        };

        var template = new JsonObject
        {
            ["Items"] = "insert($.Items, 'B', $.Position)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
    }

    [Fact(DisplayName = "Workflow input transformer append and insert expressions reject non-array first arguments")]
    public void S00006()
    {
        var payload = new JsonObject
        {
            ["NotArray"] = "value",
            ["Item"] = "A"
        };

        var appendTemplate = new JsonObject
        {
            ["Items"] = "append($.NotArray, $.Item)"
        };

        var insertTemplate = new JsonObject
        {
            ["Items"] = "insert($.NotArray, $.Item, 0)"
        };

        Action append = () => WorkflowEventInputTransformer.Transform(payload, appendTemplate);
        Action insert = () => WorkflowEventInputTransformer.Transform(payload, insertTemplate);

        append.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'append' expects the first argument to be an array or null*");
        insert.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'insert' expects the first argument to be an array or null*");
    }

    [Fact(DisplayName = "Workflow input transformer insert expression rejects invalid positions")]
    public void S00007()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A"),
            ["Position"] = "not-an-integer"
        };

        var template = new JsonObject
        {
            ["Items"] = "insert($.Items, 'B', $.Position)"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'insert' requires a valid integer index argument*");
    }

    [Fact(DisplayName = "Workflow input transformer removeWhere expression removes matching items without mutating source")]
    public void S00008()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["Name"] = "A", ["Remove"] = false },
                new JsonObject { ["Name"] = "B", ["Remove"] = true },
                new JsonObject { ["Name"] = "C", ["Remove"] = false })
        };

        var template = new JsonObject
        {
            ["Items"] = "removeWhere($.Items, '$.item.Remove == true')"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var items = transformed["Items"]!.AsArray();
        items.Select(item => item?["Name"]?.GetValue<string>()).Should().Equal("A", "C");

        items[0]!["Name"] = "Changed";
        payload["Items"]![0]!["Name"]?.GetValue<string>().Should().Be("A");
        payload["Items"]!.AsArray().Count.Should().Be(3);
    }

    [Fact(DisplayName = "Workflow input transformer removeWhere expression treats null and missing arrays as empty")]
    public void S00009()
    {
        var payload = new JsonObject();

        var template = new JsonObject
        {
            ["FromMissing"] = "removeWhere($.MissingItems, '$.item.Remove == true')",
            ["FromNull"] = "removeWhere(null, '$.item.Remove == true')"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["FromMissing"]!.AsArray().Should().BeEmpty();
        transformed["FromNull"]!.AsArray().Should().BeEmpty();
    }

    [Fact(DisplayName = "Workflow input transformer removeAt expression removes valid indexes and no-ops out-of-range indexes")]
    public void S00010()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "B", "C")
        };

        var template = new JsonObject
        {
            ["RemoveFirst"] = "removeAt($.Items, 0)",
            ["RemoveMiddle"] = "removeAt($.Items, 1)",
            ["RemoveLast"] = "removeAt($.Items, 2)",
            ["RemoveNegative"] = "removeAt($.Items, -1)",
            ["RemoveTooLarge"] = "removeAt($.Items, 999)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["RemoveFirst"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("B", "C");
        transformed["RemoveMiddle"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C");
        transformed["RemoveLast"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B");
        transformed["RemoveNegative"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
        transformed["RemoveTooLarge"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
        payload["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
    }

    [Fact(DisplayName = "Workflow input transformer removeAt expression accepts integer string positions")]
    public void S00011()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "B", "C"),
            ["Position"] = "1"
        };

        var template = new JsonObject
        {
            ["Items"] = "removeAt($.Items, $.Position)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C");
    }

    [Fact(DisplayName = "Workflow input transformer remove expressions reject invalid arguments")]
    public void S00012()
    {
        var payload = new JsonObject
        {
            ["NotArray"] = "value",
            ["Items"] = new JsonArray("A"),
            ["Position"] = "not-an-integer"
        };

        var removeWhereTemplate = new JsonObject
        {
            ["Items"] = "removeWhere($.NotArray, '$.item.value == \"A\"')"
        };

        var removeAtArrayTemplate = new JsonObject
        {
            ["Items"] = "removeAt($.NotArray, 0)"
        };

        var removeAtIndexTemplate = new JsonObject
        {
            ["Items"] = "removeAt($.Items, $.Position)"
        };

        Action removeWhere = () => WorkflowEventInputTransformer.Transform(payload, removeWhereTemplate);
        Action removeAtArray = () => WorkflowEventInputTransformer.Transform(payload, removeAtArrayTemplate);
        Action removeAtIndex = () => WorkflowEventInputTransformer.Transform(payload, removeAtIndexTemplate);

        removeWhere.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'removeWhere' expects the first argument to be an array or null*");
        removeAtArray.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'removeAt' expects the first argument to be an array or null*");
        removeAtIndex.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'removeAt' requires a valid integer index argument*");
    }

    [Fact(DisplayName = "Workflow input transformer removeWhere expression supports scalar item predicates")]
    public void S00013()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "B", "C")
        };

        var template = new JsonObject
        {
            ["RemoveMiddle"] = "removeWhere($.Items, '$.item.value == \"B\"')",
            ["RemoveNone"] = "removeWhere($.Items, '$.item.value == \"Z\"')",
            ["RemoveAll"] = "removeWhere($.Items, '$.item.value != \"Z\"')"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["RemoveMiddle"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C");
        transformed["RemoveNone"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
        transformed["RemoveAll"]!.AsArray().Should().BeEmpty();
        payload["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "B", "C");
    }

    [Fact(DisplayName = "Workflow input transformer removeAt expression treats null and missing arrays as empty")]
    public void S00014()
    {
        var payload = new JsonObject();

        var template = new JsonObject
        {
            ["FromMissing"] = "removeAt($.MissingItems, 0)",
            ["FromNull"] = "removeAt(null, 0)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["FromMissing"]!.AsArray().Should().BeEmpty();
        transformed["FromNull"]!.AsArray().Should().BeEmpty();
    }

    [Fact(DisplayName = "Workflow input transformer removeAt expression returns cloned array items")]
    public void S00015()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["Name"] = "A" },
                new JsonObject { ["Name"] = "B" })
        };

        var template = new JsonObject
        {
            ["Items"] = "removeAt($.Items, 1)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var items = transformed["Items"]!.AsArray();
        items.Select(item => item?["Name"]?.GetValue<string>()).Should().Equal("A");

        items[0]!["Name"] = "Changed";
        payload["Items"]![0]!["Name"]?.GetValue<string>().Should().Be("A");
        payload["Items"]!.AsArray().Count.Should().Be(2);
    }

    [Fact(DisplayName = "Workflow input transformer removeWhere expression rejects invalid predicates")]
    public void S00016()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A")
        };

        var template = new JsonObject
        {
            ["Items"] = "removeWhere($.Items, '$.item.value ==')"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid condition expression*");
    }

    [Fact(DisplayName = "Workflow input transformer removeAt expression rejects fractional positions")]
    public void S00017()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "B")
        };

        var template = new JsonObject
        {
            ["Items"] = "removeAt($.Items, 1.5)"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Function 'removeAt' requires a valid integer index argument*");
    }

    [Fact(DisplayName = "Workflow input transformer append expression supports inline object literals")]
    public void S00018()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(new JsonObject { ["Operation"] = "Existing" })
        };

        var template = new JsonObject
        {
            ["Items"] = "append($.Items, { Operation: \"Set\", Path: \"MARKETING_DP.AlwBlkEmail\", Value: false })"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var items = transformed["Items"]!.AsArray();
        items.Count.Should().Be(2);
        items[1]?["Operation"]?.GetValue<string>().Should().Be("Set");
        items[1]?["Path"]?.GetValue<string>().Should().Be("MARKETING_DP.AlwBlkEmail");
        items[1]?["Value"]?.GetValue<bool>().Should().BeFalse();

        items[0]!["Operation"] = "Changed";
        payload["Items"]![0]!["Operation"]?.GetValue<string>().Should().Be("Existing");
    }

    [Fact(DisplayName = "Workflow input transformer value expressions support quoted keys nested objects and arrays")]
    public void S00019()
    {
        var payload = new JsonObject
        {
            ["Suffix"] = "Email",
            ["PathRoot"] = "MARKETING_DP"
        };

        var template = new JsonObject
        {
            ["Items"] = "append(null, { Operation: Set, 'operation-type': \"Patch\", \"display-name\": toUpper($.Suffix), Path: $.PathRoot + \".AlwBlk\" + $.Suffix, Metadata: { Source: \"workflow\" }, Tags: [\"contact\", $.Suffix], Values: [1, true, null], EmptyObject: {}, EmptyArray: [] })"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var item = transformed["Items"]!.AsArray()[0]!.AsObject();
        item["Operation"]?.GetValue<string>().Should().Be("Set");
        item["operation-type"]?.GetValue<string>().Should().Be("Patch");
        item["display-name"]?.GetValue<string>().Should().Be("EMAIL");
        item["Path"]?.GetValue<string>().Should().Be("MARKETING_DP.AlwBlkEmail");
        item["Metadata"]?["Source"]?.GetValue<string>().Should().Be("workflow");
        item["Tags"]!.AsArray().Select(tag => tag?.GetValue<string>()).Should().Equal("contact", "Email");
        item["Values"]!.AsArray()[0]?.GetValue<decimal>().Should().Be(1);
        item["Values"]!.AsArray()[1]?.GetValue<bool>().Should().BeTrue();
        item["Values"]!.AsArray()[2].Should().BeNull();
        item["EmptyObject"]!.AsObject().Count.Should().Be(0);
        item["EmptyArray"]!.AsArray().Should().BeEmpty();
    }

    [Fact(DisplayName = "Workflow input transformer object and array literals reject invalid syntax")]
    public void S00020()
    {
        var payload = new JsonObject();

        var missingColonTemplate = new JsonObject
        {
            ["Items"] = "append(null, { Operation \"Set\" })"
        };

        var missingCommaTemplate = new JsonObject
        {
            ["Items"] = "append(null, { Operation: \"Set\" Path: \"X\" })"
        };

        var unterminatedObjectTemplate = new JsonObject
        {
            ["Items"] = "append(null, { Operation: \"Set\""
        };

        var trailingArrayCommaTemplate = new JsonObject
        {
            ["Items"] = "append(null, [\"A\",])"
        };

        var trailingObjectCommaTemplate = new JsonObject
        {
            ["Items"] = "append(null, { Operation: \"Set\", })"
        };

        var missingArrayCommaTemplate = new JsonObject
        {
            ["Items"] = "append(null, [\"A\" \"B\"])"
        };

        var unterminatedArrayTemplate = new JsonObject
        {
            ["Items"] = "append(null, [\"A\""
        };

        Action missingColon = () => WorkflowEventInputTransformer.Transform(payload, missingColonTemplate);
        Action missingComma = () => WorkflowEventInputTransformer.Transform(payload, missingCommaTemplate);
        Action unterminatedObject = () => WorkflowEventInputTransformer.Transform(payload, unterminatedObjectTemplate);
        Action trailingArrayComma = () => WorkflowEventInputTransformer.Transform(payload, trailingArrayCommaTemplate);
        Action trailingObjectComma = () => WorkflowEventInputTransformer.Transform(payload, trailingObjectCommaTemplate);
        Action missingArrayComma = () => WorkflowEventInputTransformer.Transform(payload, missingArrayCommaTemplate);
        Action unterminatedArray = () => WorkflowEventInputTransformer.Transform(payload, unterminatedArrayTemplate);

        missingColon.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected ':'*");
        missingComma.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected ','*");
        unterminatedObject.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected ','*");
        trailingArrayComma.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected array item*");
        trailingObjectComma.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected object key*");
        missingArrayComma.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected ','*");
        unterminatedArray.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid value expression*Expected ','*");
    }

    [Fact(DisplayName = "Workflow input transformer array literals append as one item and object literals insert by position")]
    public void S00021()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A", "C")
        };

        var template = new JsonObject
        {
            ["AppendedArray"] = "append($.Items, [\"B\", \"C\"])",
            ["InsertedObject"] = "insert($.Items, { Name: \"B\", Rank: 2 }, 1)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        var appended = transformed["AppendedArray"]!.AsArray();
        appended.Count.Should().Be(3);
        appended[0]?.GetValue<string>().Should().Be("A");
        appended[1]?.GetValue<string>().Should().Be("C");
        appended[2].Should().BeAssignableTo<JsonArray>();
        appended[2]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("B", "C");

        var inserted = transformed["InsertedObject"]!.AsArray();
        inserted.Count.Should().Be(3);
        inserted[0]?.GetValue<string>().Should().Be("A");
        inserted[1]?["Name"]?.GetValue<string>().Should().Be("B");
        inserted[1]?["Rank"]?.GetValue<decimal>().Should().Be(2);
        inserted[2]?.GetValue<string>().Should().Be("C");
        payload["Items"]!.AsArray().Select(item => item?.GetValue<string>()).Should().Equal("A", "C");
    }

    [Fact(DisplayName = "Workflow input transformer resolves full value function expressions in scalar mappings")]
    public void S00022()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["Status"] = "Active" },
                new JsonObject { ["id"] = "B", ["Status"] = "Inactive" },
                new JsonObject { ["id"] = "C", ["Status"] = "Active" })
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, \"$.item.Status == 'Active'\")",
            ["Ids"] = "pluck($.Items, \"id\")"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Filtered"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A", "C");
        transformed["Ids"]!.AsArray()
            .Select(item => item?.GetValue<string>())
            .Should().Equal("A", "B", "C");
    }

    [Fact(DisplayName = "Workflow input transformer passes environment to nested value expression predicates")]
    public void S00023()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["Status"] = "Active" },
                new JsonObject { ["id"] = "B", ["Status"] = "Inactive" })
        };

        var environment = new JsonObject
        {
            ["RequiredStatus"] = "Active"
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, \"$.item.Status == $.env.RequiredStatus\")"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template, environment: environment);

        transformed["Filtered"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A");
    }

    [Fact(DisplayName = "Workflow input transformer resolves object array and concatenation scalar expressions")]
    public void S00024()
    {
        var payload = new JsonObject
        {
            ["Path"] = "MARKETING_DP.AlwBlkEmail",
            ["First"] = "Ada",
            ["Second"] = "Lovelace",
            ["Last"] = "Byron"
        };

        var template = new JsonObject
        {
            ["Operation"] = "{ Operation: \"Set\", Path: $.Path, Value: false }",
            ["Values"] = "[$.First, $.Second]",
            ["FullName"] = "$.First + \" \" + $.Last"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Operation"]?["Operation"]?.GetValue<string>().Should().Be("Set");
        transformed["Operation"]?["Path"]?.GetValue<string>().Should().Be("MARKETING_DP.AlwBlkEmail");
        transformed["Operation"]?["Value"]?.GetValue<bool>().Should().BeFalse();
        transformed["Values"]!.AsArray()
            .Select(item => item?.GetValue<string>())
            .Should().Equal("Ada", "Lovelace");
        transformed["FullName"]?.GetValue<string>().Should().Be("Ada Byron");
    }

    [Fact(DisplayName = "Workflow input transformer preserves plain strings paths and mixed templates")]
    public void S00025()
    {
        var payload = new JsonObject
        {
            ["First"] = "Ada"
        };

        var template = new JsonObject
        {
            ["Path"] = "$.First",
            ["Plain"] = "Hello $.First",
            ["Template"] = "Hello {{ $.First }}"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Path"]?.GetValue<string>().Should().Be("Ada");
        transformed["Plain"]?.GetValue<string>().Should().Be("Hello $.First");
        transformed["Template"]?.GetValue<string>().Should().Be("Hello Ada");
    }

    [Fact(DisplayName = "Workflow input transformer resolves unquoted where predicates in scalar mappings")]
    public void S00026()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["Status"] = "Active" },
                new JsonObject { ["id"] = "B", ["Status"] = "Inactive" },
                new JsonObject { ["id"] = "C", ["Status"] = "Active" })
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, $.item.Status == 'Active')",
            ["QuotedFiltered"] = "where($.Items, \"$.item.Status == 'Active'\")"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Filtered"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A", "C");
        JsonNode.DeepEquals(transformed["Filtered"], transformed["QuotedFiltered"]).Should().BeTrue();
    }

    [Fact(DisplayName = "Workflow input transformer passes environment to unquoted where predicates")]
    public void S00027()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["Status"] = "Active" },
                new JsonObject { ["id"] = "B", ["Status"] = "Inactive" })
        };

        var environment = new JsonObject
        {
            ["RequiredStatus"] = "Active"
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, $.item.Status == $.env.RequiredStatus)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template, environment: environment);

        transformed["Filtered"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A");
    }

    [Fact(DisplayName = "Workflow input transformer resolves unquoted removeWhere predicates")]
    public void S00028()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["Name"] = "A", ["Remove"] = false },
                new JsonObject { ["Name"] = "B", ["Remove"] = true },
                new JsonObject { ["Name"] = "C", ["Remove"] = false })
        };

        var template = new JsonObject
        {
            ["Items"] = "removeWhere($.Items, $.item.Remove == true)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Items"]!.AsArray()
            .Select(item => item?["Name"]?.GetValue<string>())
            .Should().Equal("A", "C");
    }

    [Fact(DisplayName = "Workflow input transformer rejects invalid unquoted predicates")]
    public void S00029()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray("A")
        };

        var template = new JsonObject
        {
            ["Items"] = "removeWhere($.Items, $.item.value ==)"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid condition expression*");
    }

    [Fact(DisplayName = "Workflow input transformer where and removeWhere predicates can read outer stash")]
    public void S00030()
    {
        var payload = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["TargetStatus"] = "Active",
                ["RemoveId"] = "B"
            },
            ["Items"] = new JsonArray(
                new JsonObject { ["id"] = "A", ["Status"] = "Active" },
                new JsonObject { ["id"] = "B", ["Status"] = "Inactive" },
                new JsonObject { ["id"] = "C", ["Status"] = "Active" })
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, $.item.Status == $.stash.TargetStatus)",
            ["Removed"] = "removeWhere($.Items, $.item.id == $.stash.RemoveId)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Filtered"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A", "C");
        transformed["Removed"]!.AsArray()
            .Select(item => item?["id"]?.GetValue<string>())
            .Should().Equal("A", "C");
    }

    [Fact(DisplayName = "Workflow input transformer throws when where predicate transforms a missing item path")]
    public void S00031()
    {
        var payload = new JsonObject
        {
            ["stash"] = new JsonObject
            {
                ["TargetId"] = "marketing"
            },
            ["Items"] = new JsonArray(
                new JsonObject { ["Id"] = "marketing" },
                new JsonObject { ["Id"] = "other" })
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, toLower(trim($.item.id)) != $.stash.TargetId)"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.item.id'*toLower(trim($.item.id)) != $.stash.TargetId*");
    }

    [Fact(DisplayName = "Workflow input transformer throws when predicates compare missing paths")]
    public void S00032()
    {
        var payload = new JsonObject
        {
            ["Items"] = new JsonArray(new JsonObject { ["id"] = "A" })
        };

        var template = new JsonObject
        {
            ["Filtered"] = "where($.Items, $.item.Missing == \"x\")"
        };

        Action transform = () => WorkflowEventInputTransformer.Transform(payload, template);

        transform.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.item.Missing'*$.item.Missing == \"x\"*");
    }

    [Fact(DisplayName = "Workflow input transformer throws when trim and toLower receive missing paths")]
    public void S00033()
    {
        var payload = new JsonObject();

        var trimTemplate = new JsonObject
        {
            ["Value"] = "trim($.Missing)"
        };

        var lowerTemplate = new JsonObject
        {
            ["Value"] = "toLower($.Missing)"
        };

        Action trim = () => WorkflowEventInputTransformer.Transform(payload, trimTemplate);
        Action lower = () => WorkflowEventInputTransformer.Transform(payload, lowerTemplate);

        trim.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.Missing'*trim($.Missing)*");
        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing JSON path '$.Missing'*toLower($.Missing)*");
    }

    [Fact(DisplayName = "Workflow input transformer preserves optional missing path functions")]
    public void S00034()
    {
        var payload = new JsonObject();

        var template = new JsonObject
        {
            ["Fallback"] = "coalesce($.Missing, \"fallback\")",
            ["Empty"] = "isEmpty($.Missing)",
            ["Null"] = "isNull($.Missing)",
            ["Exists"] = "exists($.Missing)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Fallback"]?.GetValue<string>().Should().Be("fallback");
        transformed["Empty"]?.GetValue<bool>().Should().BeTrue();
        transformed["Null"]?.GetValue<bool>().Should().BeTrue();
        transformed["Exists"]?.GetValue<bool>().Should().BeFalse();
    }

    [Fact(DisplayName = "Workflow input transformer keeps missing array arguments empty where documented")]
    public void S00035()
    {
        var payload = new JsonObject
        {
            ["Value"] = "A"
        };

        var template = new JsonObject
        {
            ["Append"] = "append($.MissingItems, $.Value)",
            ["Removed"] = "removeWhere($.MissingItems, $.item.Remove == true)"
        };

        var transformed = WorkflowEventInputTransformer.Transform(payload, template);

        transformed["Append"]!.AsArray()
            .Select(item => item?.GetValue<string>())
            .Should().Equal("A");
        transformed["Removed"]!.AsArray().Should().BeEmpty();
    }
}
