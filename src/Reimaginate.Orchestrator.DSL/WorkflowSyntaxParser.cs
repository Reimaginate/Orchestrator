using YamlDotNet.RepresentationModel;

namespace Reimaginate.Orchestrator.DSL;

public static class WorkflowSyntaxParser
{
    public static async Task<WorkflowAst> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, leaveOpen: true);
        return await ParseAsync(reader, cancellationToken);
    }

    public static async Task<WorkflowAst> ParseAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var yaml = await reader.ReadToEndAsync(cancellationToken);

        var yamlStream = new YamlStream();
        yamlStream.Load(new StringReader(yaml));

        if (yamlStream.Documents.Count == 0 || yamlStream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow definition YAML is empty or invalid.");
        }

        var metadata = ParseMetadata(root);
        var use = ParseUse(root);

        if (!TryGetChild(root, "do", out var doNode) &&
            (!TryGetChild(root, "document", out var documentNode) || documentNode is not YamlMappingNode documentMap || !TryGetChild(documentMap, "do", out doNode)))
        {
            throw new InvalidOperationException("Workflow definition does not contain a 'do' section.");
        }

        if (doNode is not YamlSequenceNode doSequence)
        {
            throw new InvalidOperationException("Workflow definition 'do' section must be a sequence.");
        }

        return new WorkflowAst(ParseTaskSequence(doSequence, "workflow"), metadata, use);
    }

    private static WorkflowMetadataAst? ParseMetadata(YamlMappingNode root)
    {
        if (!TryGetChild(root, "document", out var documentNode) || documentNode is not YamlMappingNode documentMap)
        {
            return null;
        }

        var dsl = TryReadScalar(documentMap, "dsl");
        var ns = TryReadScalar(documentMap, "namespace");
        var name = TryReadScalar(documentMap, "name");
        var version = TryReadScalar(documentMap, "version");

        return dsl is null && ns is null && name is null && version is null
            ? null
            : new WorkflowMetadataAst(dsl, ns, name, version);
    }

    private static WorkflowUseAst? ParseUse(YamlMappingNode root)
    {
        if (!TryGetChild(root, "use", out var useNode))
        {
            if (!TryGetChild(root, "document", out var documentNode) || documentNode is not YamlMappingNode documentMap || !TryGetChild(documentMap, "use", out useNode))
            {
                return null;
            }
        }

        if (useNode is not YamlMappingNode useMap)
        {
            throw new InvalidOperationException("Workflow definition 'use' section must be an object.");
        }

        if (!TryGetChild(useMap, "extensions", out var extensionsNode))
        {
            return null;
        }

        if (extensionsNode is not YamlSequenceNode extensionsSequence)
        {
            throw new InvalidOperationException("Workflow definition 'use.extensions' section must be a sequence.");
        }

        var extensions = new List<WorkflowExtensionAst>();
        foreach (var extensionNode in extensionsSequence)
        {
            if (extensionNode is not YamlMappingNode extensionEntry || extensionEntry.Children.Count != 1)
            {
                throw new InvalidOperationException("Each workflow extension entry must contain exactly one named extension definition.");
            }

            var entry = extensionEntry.Children.Single();
            var extensionName = ReadScalar(entry.Key, "extension name");

            if (entry.Value is not YamlMappingNode extensionMap)
            {
                throw new InvalidOperationException($"Extension '{extensionName}' definition must be a YAML object.");
            }

            var before = TryGetChild(extensionMap, "before", out var beforeNode)
                ? ParseExtensionHook(beforeNode, extensionName, "before")
                : (Tasks: (IReadOnlyList<TaskAst>?)null, When: (string?)null);
            var after = TryGetChild(extensionMap, "after", out var afterNode)
                ? ParseExtensionHook(afterNode, extensionName, "after")
                : (Tasks: (IReadOnlyList<TaskAst>?)null, When: (string?)null);
            var onError = TryGetChild(extensionMap, "onError", out var onErrorNode)
                ? ParseExtensionHook(onErrorNode, extensionName, "onError")
                : (Tasks: (IReadOnlyList<TaskAst>?)null, When: (string?)null);

            extensions.Add(new WorkflowExtensionAst(
                extensionName,
                ParseExtensionExtend(extensionMap, extensionName),
                before.Tasks,
                before.When,
                after.Tasks,
                after.When,
                onError.Tasks,
                onError.When));
        }

        return extensions.Count == 0
            ? null
            : new WorkflowUseAst(extensions);
    }

    private static string? ParseExtensionExtend(YamlMappingNode extensionMap, string extensionName)
    {
        if (!TryGetChild(extensionMap, "extend", out var extendNode))
        {
            return null;
        }

        if (extendNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new InvalidOperationException($"Extension '{extensionName}'.extend must be a non-empty scalar value.");
        }

        return scalar.Value;
    }

    private static (IReadOnlyList<TaskAst>? Tasks, string? When) ParseExtensionHook(YamlNode node, string extensionName, string hookName)
    {
        if (node is YamlSequenceNode legacySequence)
        {
            return (ParseTaskSequence(legacySequence, "workflow"), null);
        }

        if (node is not YamlMappingNode hookMap)
        {
            throw new InvalidOperationException($"Extension '{extensionName}'.{hookName} must be a sequence.");
        }

        if (!TryGetChild(hookMap, "when", out _) && !TryGetChild(hookMap, "do", out _))
        {
            throw new InvalidOperationException($"Extension '{extensionName}'.{hookName} must be a sequence.");
        }

        if (!TryGetChild(hookMap, "do", out var doNode) || doNode is not YamlSequenceNode doSequence)
        {
            throw new InvalidOperationException($"Extension '{extensionName}'.{hookName}.do must be a sequence.");
        }

        return (
            ParseTaskSequence(doSequence, "workflow"),
            TryReadRequiredScalar(hookMap, "when", $"extension '{extensionName}'.{hookName}.when"));
    }

    private static IReadOnlyList<TaskAst> ParseTaskSequence(YamlSequenceNode sequence, string context)
    {
        var parsedTasks = new List<TaskAst>();
        var localTaskNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var nestedNamePrefix = context == "workflow" ? "__do" : $"{context}.__do";
        var nestedTaskIndex = 0;

        foreach (var taskNode in sequence)
        {
            if (taskNode is not YamlMappingNode taskEntry || taskEntry.Children.Count != 1)
            {
                throw new InvalidOperationException("Each workflow task entry must contain exactly one named task definition.");
            }

            var entry = taskEntry.Children.Single();
            var taskName = ReadScalar(entry.Key, "task name");

            if (entry.Value is not YamlMappingNode taskDefinitionMap)
            {
                throw new InvalidOperationException($"Task '{taskName}' definition must be a YAML object.");
            }

            var qualifiedTaskName = context == "workflow"
                ? taskName
                : $"{nestedNamePrefix}[{nestedTaskIndex++}].{taskName}";
            if (!localTaskNameMap.ContainsKey(taskName))
            {
                localTaskNameMap[taskName] = qualifiedTaskName;
            }

            parsedTasks.Add(ParseTask(qualifiedTaskName, taskDefinitionMap));
        }

        return parsedTasks
            .Select(task => NormalizeLocalThenTargets(task, localTaskNameMap))
            .ToList();
    }

    private static TaskAst NormalizeLocalThenTargets(TaskAst task, IReadOnlyDictionary<string, string> localTaskNameMap)
    {
        return task switch
        {
            CallAst call => call with { Then = NormalizeThenValue(call.Then, localTaskNameMap) },
            RunAst run => run with { Then = NormalizeThenValue(run.Then, localTaskNameMap) },
            ListenAst listen => listen with { Then = NormalizeThenValue(listen.Then, localTaskNameMap) },
            WaitAst wait => wait with { Then = NormalizeThenValue(wait.Then, localTaskNameMap) },
            TerminateAst terminate => terminate with { Then = NormalizeThenValue(terminate.Then, localTaskNameMap) },
            RaiseAst raise => raise with { Then = NormalizeThenValue(raise.Then, localTaskNameMap) },
            EmitAst emit => emit with { Then = NormalizeThenValue(emit.Then, localTaskNameMap) },
            MapAst map => map with { Then = NormalizeThenValue(map.Then, localTaskNameMap) },
            StashAst stash => stash with { Then = NormalizeThenValue(stash.Then, localTaskNameMap) },
            SwitchAst switchAst => switchAst with
            {
                Then = NormalizeThenValue(switchAst.Then, localTaskNameMap),
                Branches = switchAst.Branches
                    .Select(branch => branch with { Then = NormalizeThenValue(branch.Then, localTaskNameMap) })
                    .ToList()
            },
            DoAst doAst => doAst with { Then = NormalizeThenValue(doAst.Then, localTaskNameMap) },
            ForkAst forkAst => forkAst with { Then = NormalizeThenValue(forkAst.Then, localTaskNameMap) },
            TryAst tryAst => tryAst with
            {
                Then = NormalizeThenValue(tryAst.Then, localTaskNameMap),
                Catches = tryAst.Catches
                    .Select(c => c with { Then = NormalizeThenValue(c.Then, localTaskNameMap) })
                    .ToList()
            },
            _ => task
        };
    }

    private static object? NormalizeThenValue(object? thenValue, IReadOnlyDictionary<string, string> localTaskNameMap)
    {
        return thenValue switch
        {
            null => null,
            string taskName when localTaskNameMap.TryGetValue(taskName, out var qualifiedTaskName) => qualifiedTaskName,
            string taskName => taskName,
            List<object?> sequence => sequence.Select(item => NormalizeThenValue(item, localTaskNameMap)).ToList(),
            Dictionary<string, object?> mapping => NormalizeInlineThenTaskBlock(mapping, localTaskNameMap),
            _ => thenValue
        };
    }

    private static object NormalizeInlineThenTaskBlock(
        IReadOnlyDictionary<string, object?> mapping,
        IReadOnlyDictionary<string, string> localTaskNameMap)
    {
        if (mapping.Count != 1)
        {
            return mapping;
        }

        var entry = mapping.Single();
        if (entry.Value is not Dictionary<string, object?> taskDefinition)
        {
            return mapping;
        }

        var normalizedTaskDefinition = new Dictionary<string, object?>(taskDefinition, StringComparer.Ordinal);
        if (normalizedTaskDefinition.TryGetValue("then", out var thenValue))
        {
            normalizedTaskDefinition["then"] = NormalizeThenValue(thenValue, localTaskNameMap);
        }
        else if (normalizedTaskDefinition.TryGetValue("Then", out var pascalThenValue))
        {
            normalizedTaskDefinition["Then"] = NormalizeThenValue(pascalThenValue, localTaskNameMap);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [entry.Key] = normalizedTaskDefinition
        };
    }

    private static TaskAst ParseTask(string taskName, YamlMappingNode map)
    {
        var extend = ParseExtendReferences(map, taskName);

        if (TryGetChild(map, "call", out var callNode))
        {
            return new CallAst(
                taskName,
                ReadScalar(callNode, $"task '{taskName}'.call"),
                TryGetChild(map, "with", out var withNode) ? ParseDictionary(withNode, $"task '{taskName}'.with") : null,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "run", out var runNode))
        {
            return new RunAst(
                taskName,
                ParseRunWorkflow(runNode, taskName),
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "switch", out var switchNode))
        {
            return new SwitchAst(
                taskName,
                ParseSwitch(taskName, switchNode),
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "listen", out var listenNode))
        {
            return new ListenAst(
                taskName,
                ParseListenTargets(listenNode, taskName),
                ParseListenRead(listenNode, taskName),
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "wait", out var waitNode))
        {
            var (waitFor, waitUntil) = ParseWait(waitNode, taskName);
            return new WaitAst(
                taskName,
                waitFor,
                waitUntil,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "terminate", out var terminateNode))
        {
            var (status, output, reason) = ParseTerminate(terminateNode, taskName);
            return new TerminateAst(
                taskName,
                status,
                output,
                reason,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "raise", out var raiseNode))
        {
            var (errorType, message, data) = ParseRaise(raiseNode, taskName);
            return new RaiseAst(
                taskName,
                errorType,
                message,
                data,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "emit", out var emitNode))
        {
            var (eventType, source, subject, id, to, data) = ParseEmit(emitNode, taskName);
            return new EmitAst(
                taskName,
                eventType,
                source,
                subject,
                id,
                to,
                data,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "map", out var mapNode))
        {
            return new MapAst(
                taskName,
                ParseMapDefinition(mapNode, taskName),
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "stash", out var stashNode))
        {
            return new StashAst(
                taskName,
                ParseStash(stashNode, taskName),
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "fork", out var forkNode))
        {
            var (branches, joinMode) = ParseFork(forkNode, taskName);
            return new ForkAst(
                taskName,
                branches,
                joinMode,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "try", out var tryNode))
        {
            return new TryAst(
                taskName,
                ParseDoTasks(tryNode, taskName),
                TryGetChild(map, "catch", out var catchNode) ? ParseCatch(taskName, catchNode) : [],
                TryGetChild(map, "finally", out var finallyNode) ? ParseDoTasks(finallyNode, taskName) : null,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        if (TryGetChild(map, "do", out var doNode))
        {
            var (forIn, forEach, forAt, forSize, forMaxConcurrency, forInput, forCollect, forCollectInclude, forOnError) = ParseDoFor(map, taskName);

            return new DoAst(
                taskName,
                ParseDoTasks(doNode, taskName),
                TryGetChild(map, "with", out var withNode) ? ParseDictionary(withNode, $"task '{taskName}'.with") : null,
                forIn,
                forEach,
                forAt,
                forSize,
                forMaxConcurrency,
                forInput,
                forCollect,
                forCollectInclude,
                forOnError,
                TryReadScalar(map, "while"),
                TryGetChild(map, "guard", out var guardNode) ? ParseLoopGuardPolicy(guardNode, taskName) : null,
                TryGetChild(map, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(map, "then", out var thenNode) ? ParseUntyped(thenNode) : null,
                TryReadScalar(map, "if"),
                TryReadScalar(map, "filter"),
                extend);
        }

        throw new InvalidOperationException("Task '{taskName}' is not supported. Supported task types are call, run, switch, listen, wait, terminate, raise, emit, map, stash, do, fork, and try."
            .Replace("{taskName}", taskName, StringComparison.Ordinal));
    }

    private static RunWorkflowAst ParseRunWorkflow(YamlNode runNode, string taskName)
    {
        if (runNode is not YamlMappingNode runMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.run must be an object.");
        }

        if (!TryGetChild(runMap, "workflow", out var workflowNode))
        {
            throw new InvalidOperationException($"Task '{taskName}'.run must define 'workflow'.");
        }

        if (workflowNode is not YamlMappingNode workflowMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.run.workflow must be an object.");
        }

        var workflowType = TryReadScalar(workflowMap, "workflowType");
        if (string.IsNullOrWhiteSpace(workflowType))
        {
            throw new InvalidOperationException($"Task '{taskName}'.run.workflow.workflowType must be a non-empty scalar value.");
        }

        IReadOnlyDictionary<string, object>? input = null;
        if (TryGetChild(workflowMap, "input", out var inputNode))
        {
            input = ParseDictionary(inputNode, $"task '{taskName}'.run.workflow.input");
        }

        return new RunWorkflowAst(workflowType, input);
    }

    private static IReadOnlyList<string>? ParseExtendReferences(YamlMappingNode map, string taskName)
    {
        if (!TryGetChild(map, "extend", out var extendNode))
        {
            return null;
        }

        return extendNode switch
        {
            YamlScalarNode scalar => [ReadScalar(scalar, $"task '{taskName}'.extend")],
            YamlSequenceNode sequence => ParseExtendReferenceSequence(sequence, taskName),
            _ => throw new InvalidOperationException($"Task '{taskName}'.extend must be a scalar or sequence.")
        };
    }

    private static IReadOnlyList<string> ParseExtendReferenceSequence(YamlSequenceNode sequence, string taskName)
    {
        var references = new List<string>();
        foreach (var entry in sequence)
        {
            references.Add(ReadScalar(entry, $"task '{taskName}'.extend entry"));
        }

        return references;
    }

    private static IReadOnlyDictionary<string, object> ParseStash(YamlNode stashNode, string taskName)
    {
        if (stashNode is not YamlMappingNode stashMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.stash must be an object.");
        }

        return ParseDictionary(stashMap, $"task '{taskName}'.stash");
    }

    private static MapDefinitionAst ParseMapDefinition(YamlNode mapNode, string context)
    {
        if (mapNode is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"Task '{context}'.map must be an object.");
        }

        var rules = new List<MapRuleAst>();
        if (TryGetChild(map, "rules", out var rulesNode))
        {
            if (rulesNode is not YamlSequenceNode rulesSequence)
            {
                throw new InvalidOperationException($"Task '{context}'.map.rules must be a sequence.");
            }

            var index = 0;
            foreach (var ruleNode in rulesSequence)
            {
                rules.Add(ParseMapRule(ruleNode, $"{context}.map.rules[{index}]"));
                index++;
            }
        }

        return new MapDefinitionAst(
            TryReadScalar(map, "input"),
            rules,
            TryGetChild(map, "schema", out var schemaNode) ? ParseDictionary(schemaNode, $"task '{context}'.map.schema") : null,
            TryGetChild(map, "options", out var optionsNode) ? ParseDictionary(optionsNode, $"task '{context}'.map.options") : null);
    }

    private static MapRuleAst ParseMapRule(YamlNode ruleNode, string context)
    {
        if (ruleNode is not YamlMappingNode ruleMap)
        {
            throw new InvalidOperationException($"{context} must be an object.");
        }

        var transform = TryGetChild(ruleMap, "transform", out var transformNode)
            ? ParseMapTransform(transformNode, context)
            : null;

        var hasConstant = TryGetChild(ruleMap, "constant", out var constantNode);
        var hasDefault = TryGetChild(ruleMap, "default", out var defaultNode);

        return new MapRuleAst(
            TryReadScalar(ruleMap, "target") ?? string.Empty,
            TryReadScalar(ruleMap, "from"),
            TryReadScalar(ruleMap, "expression"),
            hasConstant ? ParseUntyped(constantNode) : null,
            hasConstant,
            TryReadScalar(ruleMap, "when"),
            transform,
            hasDefault ? ParseUntyped(defaultNode) : null,
            hasDefault,
            TryReadScalar(ruleMap, "foreach"),
            TryGetChild(ruleMap, "map", out var nestedMapNode) ? ParseMapDefinition(nestedMapNode, context) : null,
            TryGetChild(ruleMap, "lookup", out var lookupNode) ? ParseMapLookup(lookupNode, context) : null,
            TryGetChild(ruleMap, "validate", out var validateNode) ? ParseMapValidation(validateNode, context) : null);
    }

    private static IReadOnlyList<string> ParseMapTransform(YamlNode transformNode, string context)
    {
        return transformNode switch
        {
            YamlScalarNode scalar => [ReadScalar(scalar, $"{context}.transform")],
            YamlSequenceNode sequence => sequence.Children.Select((node, index) =>
                ReadScalar(node, $"{context}.transform[{index}]")).ToList(),
            _ => throw new InvalidOperationException($"{context}.transform must be a scalar or sequence.")
        };
    }

    private static MapLookupAst ParseMapLookup(YamlNode lookupNode, string context)
    {
        if (lookupNode is not YamlMappingNode lookupMap)
        {
            throw new InvalidOperationException($"{context}.lookup must be an object.");
        }

        return new MapLookupAst(
            TryReadScalar(lookupMap, "from"),
            TryReadScalar(lookupMap, "using"));
    }

    private static MapValidationAst ParseMapValidation(YamlNode validateNode, string context)
    {
        if (validateNode is not YamlMappingNode validateMap)
        {
            throw new InvalidOperationException($"{context}.validate must be an object.");
        }

        return new MapValidationAst(
            TryReadOptionalBool(validateMap, "required", $"{context}.validate.required"),
            TryReadScalar(validateMap, "pattern"));
    }

    private static (string? ErrorType, string? Message, IReadOnlyDictionary<string, object>? Data) ParseRaise(YamlNode raiseNode, string taskName)
    {
        if (raiseNode is YamlScalarNode scalar)
        {
            if (string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidOperationException($"Task '{taskName}'.raise must define a non-empty error type.");
            }

            return (scalar.Value, null, null);
        }

        if (raiseNode is not YamlMappingNode raiseMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.raise must be either a scalar error type or an object.");
        }

        var errorType = TryReadScalar(raiseMap, "error")
            ?? TryReadScalar(raiseMap, "type");

        if (string.IsNullOrWhiteSpace(errorType))
        {
            throw new InvalidOperationException($"Task '{taskName}'.raise must define a non-empty 'error' or 'type' field.");
        }

        var message = TryReadScalar(raiseMap, "message");
        var data = TryGetChild(raiseMap, "data", out var dataNode)
            ? ParseDictionary(dataNode, $"task '{taskName}'.raise.data")
            : null;

        return (errorType, message, data);
    }

    private static (string? EventType, string? Source, string? Subject, string? Id, string? To, IReadOnlyDictionary<string, object>? Data) ParseEmit(YamlNode emitNode, string taskName)
    {
        if (emitNode is YamlScalarNode scalar)
        {
            if (string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidOperationException($"Task '{taskName}'.emit must define a non-empty event type.");
            }

            return (scalar.Value, null, null, null, null, null);
        }

        if (emitNode is not YamlMappingNode emitMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.emit must be either a scalar event type or an object.");
        }

        var eventType = TryReadScalar(emitMap, "event")
            ?? TryReadScalar(emitMap, "type");

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new InvalidOperationException($"Task '{taskName}'.emit must define a non-empty 'event' or 'type' field.");
        }

        var source = TryReadScalar(emitMap, "source");
        var subject = TryReadScalar(emitMap, "subject");
        var id = TryReadScalar(emitMap, "id");
        var to = TryReadScalar(emitMap, "to");
        var data = TryGetChild(emitMap, "data", out var dataNode)
            ? ParseDictionary(dataNode, $"task '{taskName}'.emit.data")
            : null;

        return (eventType, source, subject, id, to, data);
    }

    private static (IReadOnlyList<ForkBranchAst> Branches, string? JoinMode) ParseFork(YamlNode forkNode, string taskName)
    {
        if (forkNode is not YamlMappingNode forkMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.fork must be an object.");
        }

        if (!TryGetChild(forkMap, "branches", out var branchesNode) || branchesNode is not YamlSequenceNode branchesSequence)
        {
            throw new InvalidOperationException($"Task '{taskName}'.fork.branches must be a sequence.");
        }

        var branches = new List<ForkBranchAst>();
        var branchIndex = 0;
        foreach (var branchNode in branchesSequence)
        {
            if (branchNode is not YamlMappingNode branchMap)
            {
                throw new InvalidOperationException($"Task '{taskName}'.fork.branches entries must be objects.");
            }

            var branchName = TryReadScalar(branchMap, "name");
            if (string.IsNullOrWhiteSpace(branchName))
            {
                throw new InvalidOperationException($"Task '{taskName}'.fork.branches.name must be non-empty.");
            }

            if (!TryGetChild(branchMap, "do", out var branchDoNode) || branchDoNode is not YamlSequenceNode branchDoSequence)
            {
                throw new InvalidOperationException($"Task '{taskName}'.fork.branches['{branchName}'].do must be a sequence.");
            }

            var branchContext = $"{taskName}.__fork[{branchIndex++}]";
            branches.Add(new ForkBranchAst(branchName, ParseTaskSequence(branchDoSequence, branchContext)));
        }

        var joinMode = TryGetChild(forkMap, "join", out var joinNode)
            ? ParseForkJoinMode(joinNode, taskName)
            : null;

        return (branches, joinMode);
    }

    private static string? ParseForkJoinMode(YamlNode joinNode, string taskName)
    {
        if (joinNode is YamlScalarNode scalar)
        {
            return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value;
        }

        if (joinNode is not YamlMappingNode joinMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.fork.join must be a scalar or object.");
        }

        return TryReadScalar(joinMap, "mode");
    }

    private static IReadOnlyList<CatchAst> ParseCatch(string taskName, YamlNode catchNode)
    {
        if (catchNode is not YamlSequenceNode catchSequence)
        {
            throw new InvalidOperationException($"Task '{taskName}'.catch must be a sequence.");
        }

        var catches = new List<CatchAst>();
        foreach (var entry in catchSequence)
        {
            if (entry is not YamlMappingNode catchMap)
            {
                throw new InvalidOperationException($"Task '{taskName}'.catch entries must be objects.");
            }

            if (!TryGetChild(catchMap, "do", out var doNode))
            {
                throw new InvalidOperationException($"Task '{taskName}'.catch entry must define 'do'.");
            }

            catches.Add(new CatchAst(
                TryReadScalar(catchMap, "errors"),
                TryReadScalar(catchMap, "when"),
                ParseDoTasks(doNode, taskName),
                TryGetChild(catchMap, "output", out var outputNode) ? ParseOutput(outputNode, taskName) : null,
                TryGetChild(catchMap, "then", out var thenNode) ? ParseUntyped(thenNode) : null));
        }

        return catches;
    }

    private static (
        string? ForIn,
        string? ForEach,
        string? ForAt,
        int? ForSize,
        int? ForMaxConcurrency,
        IReadOnlyDictionary<string, object>? ForInput,
        string? ForCollect,
        IReadOnlyList<string>? ForCollectInclude,
        string? ForOnError) ParseDoFor(YamlMappingNode map, string taskName)
    {
        if (!TryGetChild(map, "for", out var forNode))
        {
            return (null, null, null, null, null, null, null, null, null);
        }

        if (forNode is not YamlMappingNode forMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.for must be an object.");
        }

        var forIn = TryReadRequiredScalar(forMap, "in", $"task '{taskName}'.for.in");
        var forEach = TryReadRequiredScalar(forMap, "each", $"task '{taskName}'.for.each");
        var forAt = TryReadRequiredScalar(forMap, "at", $"task '{taskName}'.for.at");
        var forSize = TryReadOptionalInt(forMap, "size", $"task '{taskName}'.for.size");
        var forMaxConcurrency = TryReadOptionalInt(forMap, "maxConcurrency", $"task '{taskName}'.for.maxConcurrency");
        var forInput = TryGetChild(forMap, "input", out var inputNode)
            ? ParseDictionary(inputNode, $"task '{taskName}'.for.input")
            : null;
        var (forCollect, forCollectInclude) = TryGetChild(forMap, "collect", out var collectNode)
            ? ParseForCollect(collectNode, taskName)
            : (null, null);
        var forOnError = TryReadRequiredScalar(forMap, "onError", $"task '{taskName}'.for.onError");

        return (forIn, forEach, forAt, forSize, forMaxConcurrency, forInput, forCollect, forCollectInclude, forOnError);
    }

    private static (string? Into, IReadOnlyList<string>? Include) ParseForCollect(YamlNode node, string taskName)
    {
        if (node is YamlScalarNode scalar)
        {
            return (ReadScalar(scalar, $"task '{taskName}'.for.collect"), null);
        }

        if (node is not YamlMappingNode collectMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.for.collect must be a scalar or object.");
        }

        var into = TryReadRequiredScalar(collectMap, "into", $"task '{taskName}'.for.collect.into");
        IReadOnlyList<string>? include = null;
        if (TryGetChild(collectMap, "include", out var includeNode))
        {
            if (includeNode is not YamlSequenceNode includeSequence)
            {
                throw new InvalidOperationException($"Task '{taskName}'.for.collect.include must be a sequence.");
            }

            include = includeSequence.Children
                .Select((entry, index) => ReadScalar(entry, $"task '{taskName}'.for.collect.include[{index}]"))
                .ToArray();
        }

        return (into, include);
    }

    private static LoopGuardPolicyAst ParseLoopGuardPolicy(YamlNode node, string taskName)
    {
        if (node is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"Task '{taskName}'.guard must be an object.");
        }

        return new LoopGuardPolicyAst(
            TryReadOptionalInt(map, "maxIterations", $"task '{taskName}'.guard.maxIterations"),
            TryReadOptionalInt(map, "maxRepeatedPayloads", $"task '{taskName}'.guard.maxRepeatedPayloads"),
            TryReadOptionalInt(map, "timeoutMs", $"task '{taskName}'.guard.timeoutMs"),
            TryReadScalar(map, "onCancel"));
    }

    private static TaskOutputAst ParseOutput(YamlNode node, string taskName)
    {
        if (node is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"Task '{taskName}'.output must be an object.");
        }

        IReadOnlyDictionary<string, object>? stash = null;
        if (TryGetChild(map, "stash", out var stashNode))
        {
            if (stashNode is not YamlMappingNode)
            {
                throw new InvalidOperationException($"Task '{taskName}'.output.stash must be an object.");
            }

            stash = ParseDictionary(stashNode, $"task '{taskName}'.output.stash");
        }

        return new TaskOutputAst(
            TryGetChild(map, "as", out var asNode) ? ParseUntyped(asNode) : null,
            stash);
    }

    private static IReadOnlyList<TaskAst> ParseDoTasks(YamlNode node, string taskName)
    {
        if (node is not YamlSequenceNode sequence)
        {
            throw new InvalidOperationException($"Task '{taskName}'.do must be a sequence.");
        }

        return ParseTaskSequence(sequence, taskName);
    }

    private static IReadOnlyList<ListenTargetAst> ParseListenTargets(YamlNode listenNode, string taskName)
    {
        if (listenNode is not YamlMappingNode listenMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.listen must be an object.");
        }

        if (!TryGetChild(listenMap, "to", out var toNode) || toNode is not YamlMappingNode toMap || !TryGetChild(toMap, "any", out var anyNode))
        {
            return [];
        }

        if (anyNode is not YamlSequenceNode anySequence)
        {
            throw new InvalidOperationException($"Task '{taskName}'.listen.to.any must be a sequence.");
        }

        var targets = new List<ListenTargetAst>();
        foreach (var anyItem in anySequence)
        {
            if (anyItem is not YamlMappingNode anyMap)
            {
                throw new InvalidOperationException($"Task '{taskName}'.listen.to.any entries must be objects.");
            }

            if (!TryGetChild(anyMap, "with", out var withNode) || withNode is not YamlMappingNode withMap)
            {
                targets.Add(new ListenTargetAst(null, null));
                continue;
            }

            targets.Add(new ListenTargetAst(TryReadScalar(withMap, "type"), TryReadScalar(withMap, "filter")));
        }

        return targets;
    }

    private static string? ParseListenRead(YamlNode listenNode, string taskName)
    {
        if (listenNode is not YamlMappingNode listenMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.listen must be an object.");
        }

        return TryReadScalar(listenMap, "read");
    }

    private static (string? For, string? Until) ParseWait(YamlNode waitNode, string taskName)
    {
        if (waitNode is not YamlMappingNode waitMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.wait must be an object.");
        }

        var waitFor = TryReadScalar(waitMap, "for");
        var waitUntil = TryReadScalar(waitMap, "until");
        return (waitFor, waitUntil);
    }

    private static (string? Status, object? Output, string? Reason) ParseTerminate(YamlNode terminateNode, string taskName)
    {
        if (terminateNode is YamlScalarNode scalar)
        {
            if (string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidOperationException($"Task '{taskName}'.terminate must define a non-empty status.");
            }

            return (scalar.Value, null, null);
        }

        if (terminateNode is not YamlMappingNode terminateMap)
        {
            throw new InvalidOperationException($"Task '{taskName}'.terminate must be either a scalar status or an object.");
        }

        return (
            TryReadScalar(terminateMap, "status"),
            TryGetChild(terminateMap, "output", out var outputNode) ? ParseUntyped(outputNode) : null,
            TryReadScalar(terminateMap, "reason"));
    }

    private static IReadOnlyList<SwitchBranchAst> ParseSwitch(string taskName, YamlNode node)
    {
        if (node is not YamlSequenceNode sequence)
        {
            throw new InvalidOperationException($"Task '{taskName}'.switch must be a sequence.");
        }

        var branches = new List<SwitchBranchAst>();
        foreach (var item in sequence)
        {
            if (item is not YamlMappingNode branchNode || branchNode.Children.Count != 1)
            {
                throw new InvalidOperationException($"Task '{taskName}'.switch branch must contain exactly one branch name.");
            }

            var branchEntry = branchNode.Children.Single();
            var branchName = ReadScalar(branchEntry.Key, $"task '{taskName}'.switch branch name");

            if (branchEntry.Value is not YamlMappingNode branchMap)
            {
                throw new InvalidOperationException($"Task '{taskName}'.switch branch '{branchName}' must be an object.");
            }

            branches.Add(new SwitchBranchAst(
                branchName,
                TryReadScalar(branchMap, "when"),
                TryGetChild(branchMap, "output", out var outputNode) ? ParseOutput(outputNode, $"{taskName}.switch.{branchName}") : null,
                TryGetChild(branchMap, "then", out var thenNode) ? ParseUntyped(thenNode) : null));
        }

        return branches;
    }

    private static IReadOnlyDictionary<string, object> ParseDictionary(YamlNode node, string context)
    {
        if (node is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"{context} must be an object.");
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var child in map.Children)
        {
            var key = ReadScalar(child.Key, $"{context} key");
            result[key] = ParseUntyped(child.Value)!;
        }

        return result;
    }

    private static object? ParseUntyped(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => ParseScalarValue(scalar),
            YamlSequenceNode sequence => sequence.Children.Select(ParseUntyped).ToList(),
            YamlMappingNode mapping => mapping.Children.ToDictionary(
                child => ReadScalar(child.Key, "mapping key"),
                child => ParseUntyped(child.Value),
                StringComparer.Ordinal),
            _ => throw new InvalidOperationException($"Unsupported YAML node type '{node.NodeType}'.")
        };
    }

    private static object? ParseScalarValue(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue)) return boolValue;
        if (long.TryParse(value, out var longValue)) return longValue;
        if (decimal.TryParse(value, out var decimalValue)) return decimalValue;
        return value;
    }

    private static string? TryReadRequiredScalar(YamlMappingNode map, string key, string context)
    {
        if (!TryGetChild(map, key, out var node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new InvalidOperationException($"{context} must be a non-empty scalar value.");
        }

        return scalar.Value;
    }

    private static int? TryReadOptionalInt(YamlMappingNode map, string key, string context)
    {
        if (!TryGetChild(map, key, out var node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new InvalidOperationException($"{context} must be a non-empty integer value.");
        }

        if (!int.TryParse(scalar.Value, out var parsed))
        {
            throw new InvalidOperationException($"{context} must be a valid integer value.");
        }

        return parsed;
    }

    private static bool? TryReadOptionalBool(YamlMappingNode map, string key, string context)
    {
        if (!TryGetChild(map, key, out var node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new InvalidOperationException($"{context} must be a non-empty boolean value.");
        }

        if (!bool.TryParse(scalar.Value, out var parsed))
        {
            throw new InvalidOperationException($"{context} must be a valid boolean value.");
        }

        return parsed;
    }

    private static bool TryGetChild(YamlMappingNode map, string key, out YamlNode node)
    {
        return map.Children.TryGetValue(new YamlScalarNode(key), out node!);
    }

    private static string? TryReadScalar(YamlMappingNode map, string key)
    {
        if (!TryGetChild(map, key, out var node) || node is not YamlScalarNode scalar)
        {
            return null;
        }

        return scalar.Value;
    }

    private static string ReadScalar(YamlNode node, string context)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new InvalidOperationException($"{context} must be a non-empty scalar value.");
        }

        return scalar.Value;
    }
}
