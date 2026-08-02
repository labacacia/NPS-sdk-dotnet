// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Transport-independent NOP 0.9 deterministic orchestration and runtime
/// conformance profile.
/// </summary>
public static partial class NopPortableProfile
{
    private const string ClusterSplit = "NDP-CLUSTER-SPLIT";

    /// <summary>Runs one shared deterministic orchestration transcript.</summary>
    public static JsonElement EvaluateOrchestration(JsonElement input)
    {
        var nodes = input.GetProperty("nodes")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var id = node.GetProperty("id").GetString()!;
            if (!byId.TryAdd(id, node))
                return FailureResult([TaskFailed], NopErrorCodes.TaskDagInvalid);
        }

        var topo = StableTopologicalOrder(byId);
        if (topo is null)
            return FailureResult([TaskFailed], NopErrorCodes.TaskDagCycle);

        var events = new List<string>();
        if (OptionalBool(input, "preflight"))
        {
            events.Add("task:preflight");
            if (topo.Any(id => !OptionalBool(byId[id], "preflight_available", true)))
            {
                events.Add(TaskFailed);
                return BuildResult(
                    events,
                    "failed",
                    NopErrorCodes.ResourceInsufficient,
                    null,
                    new Dictionary<string, string>(),
                    new Dictionary<string, int>(),
                    new Dictionary<string, JsonNode?>(),
                    []);
            }
        }

        events.Add("task:running");
        var results = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mapped = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var taskRetries = OptionalInt(input, "max_retries", 0);

        foreach (var id in topo)
        {
            var node = byId[id];
            if (string.Equals(OptionalString(input, "cancel_before"), id, StringComparison.Ordinal))
            {
                events.Add("task:cancelled");
                return BuildResult(
                    events,
                    "cancelled",
                    NopErrorCodes.TaskCancelled,
                    null,
                    states,
                    attempts,
                    mapped,
                    []);
            }

            if (node.TryGetProperty("condition", out var condition))
            {
                var conditionResult = EvaluateCondition(
                    condition.GetString()!,
                    results);
                if (conditionResult is null)
                {
                    states[id] = "failed";
                    attempts[id] = 0;
                    events.Add($"{id}:failed");
                    events.Add(TaskFailed);
                    return BuildResult(
                        events,
                        "failed",
                        NopErrorCodes.ConditionEvalError,
                        null,
                        states,
                        attempts,
                        mapped,
                        []);
                }

                if (!conditionResult.Value)
                {
                    states[id] = "skipped";
                    attempts[id] = 0;
                    events.Add($"{id}:skipped");
                    continue;
                }
            }

            if (node.TryGetProperty("input_mapping", out var mapping))
            {
                var parameters = ResolveMapping(mapping, results);
                if (parameters is null)
                {
                    states[id] = "failed";
                    attempts[id] = 0;
                    events.Add($"{id}:failed");
                    events.Add(TaskFailed);
                    return BuildResult(
                        events,
                        "failed",
                        NopErrorCodes.InputMappingError,
                        null,
                        states,
                        attempts,
                        mapped,
                        []);
                }
                mapped[id] = parameters;
            }

            var scripted = node.GetProperty("attempts").EnumerateArray().ToList();
            var maxRetries = OptionalInt(node, "max_retries", taskRetries);
            string? finalError = null;
            var completed = false;
            var count = 0;

            for (var index = 0; index < scripted.Count && index <= maxRetries; index++)
            {
                count++;
                events.Add($"{id}:attempt:{count}");
                var outcome = scripted[index];
                var kind = outcome.GetProperty("kind").GetString();
                if (string.Equals(kind, "success", StringComparison.Ordinal))
                {
                    results[id] = outcome.TryGetProperty("result", out var result)
                        ? JsonNode.Parse(result.GetRawText())
                        : new JsonObject();
                    states[id] = "completed";
                    events.Add($"{id}:completed");
                    completed = true;
                    break;
                }

                finalError = string.Equals(kind, "timeout", StringComparison.Ordinal)
                    ? NopErrorCodes.DelegateTimeout
                    : OptionalString(outcome, "error_code") ?? NopErrorCodes.DelegateRejected;
                var retryable = string.Equals(kind, "timeout", StringComparison.Ordinal)
                    || OptionalBool(outcome, "retryable");
                var selected = !node.TryGetProperty("retry_on", out var retryOn)
                    || retryOn.EnumerateArray()
                        .Any(item => string.Equals(item.GetString(), finalError, StringComparison.Ordinal));
                var canRetry = retryable && selected && count <= maxRetries && index + 1 < scripted.Count;
                if (canRetry)
                {
                    events.Add($"{id}:retrying");
                    continue;
                }

                states[id] = "failed";
                events.Add($"{id}:failed");
                break;
            }

            attempts[id] = count;
            if (completed) continue;

            var compensationOrder = RunCompensation(
                input,
                id,
                topo,
                byId,
                states,
                events,
                out var compensationError);
            events.Add(TaskFailed);
            return BuildResult(
                events,
                "failed",
                compensationError ?? finalError ?? NopErrorCodes.DelegateRejected,
                null,
                states,
                attempts,
                mapped,
                compensationOrder);
        }

        var aggregate = Aggregate(input, topo, byId, states, results);
        events.Add("task:completed");
        return BuildResult(
            events,
            "completed",
            null,
            aggregate,
            states,
            attempts,
            mapped,
            []);
    }

    /// <summary>Evaluates one runtime/security vector category.</summary>
    public static JsonElement EvaluateRuntime(string category, JsonElement input) =>
        category switch
        {
            "callback" => EvaluateCallback(input),
            "hmac" => EvaluateHmac(input),
            "lease" => EvaluateLease(input),
            "delegation" => EvaluateDelegation(input),
            "spawn_spec" => EvaluateSpawnSpec(input),
            "lifecycle" => EvaluateLifecycle(input),
            "dedup_key" => JsonSerializer.SerializeToElement(new
            {
                value = ComputeDedupKey(
                    input.GetProperty("task_id").GetString()!,
                    input.GetProperty("dag_hash").GetString()!),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown NOP profile category."),
        };

    /// <summary>
    /// Computes the canonical lowercase SHA-256 task deduplication key.
    /// </summary>
    public static string ComputeDedupKey(string taskId, string dagHash)
    {
        var task = Encoding.UTF8.GetBytes(taskId);
        var dag = Encoding.UTF8.GetBytes(dagHash);
        var bytes = new byte[task.Length + 1 + dag.Length];
        task.CopyTo(bytes, 0);
        dag.CopyTo(bytes, task.Length + 1);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonElement EvaluateCallback(JsonElement input)
    {
        var allowed = IsCallbackDestinationAllowed(
            input.GetProperty("url").GetString()!,
            input.GetProperty("resolved_ips"));
        if (allowed && input.TryGetProperty("redirect_url", out var redirect))
        {
            allowed = IsCallbackDestinationAllowed(
                redirect.GetString()!,
                input.GetProperty("redirect_resolved_ips"));
        }

        return JsonSerializer.SerializeToElement(new
        {
            allowed,
            error = allowed ? null : NopErrorCodes.CallbackInvalid,
        });
    }

    private static bool IsCallbackDestinationAllowed(string value, JsonElement addresses)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrEmpty(uri.Host)
            || addresses.GetArrayLength() == 0)
        {
            return false;
        }

        return addresses.EnumerateArray().All(item =>
            IPAddress.TryParse(item.GetString(), out var address)
            && IsPublicAddress(address));
    }

    private static bool IsPublicAddress(IPAddress input)
    {
        var address = input.IsIPv4MappedToIPv6 ? input.MapToIPv4() : input;
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && bytes[0] < 224;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.Equals(IPAddress.IPv6Any)
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && (bytes[0] & 0xFE) != 0xFC;
    }

    private static JsonElement EvaluateHmac(JsonElement input)
    {
        if (!input.TryGetProperty("signature", out var signature)
            || signature.ValueKind == JsonValueKind.Null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                valid = false,
                error = NopErrorCodes.CallbackHmacMissing,
            });
        }

        var valid = TryDecodeBase64Url(
                input.GetProperty("secret_base64url").GetString()!,
                out var key)
            && key.Length == 32
            && TryDecodeSignature(signature.GetString()!, out var supplied)
            && CryptographicOperations.FixedTimeEquals(
                HMACSHA256.HashData(
                    key,
                    Encoding.UTF8.GetBytes(input.GetProperty("raw_body").GetString()!)),
                supplied);
        return JsonSerializer.SerializeToElement(new
        {
            valid,
            error = valid ? null : NopErrorCodes.CallbackHmacInvalid,
        });
    }

    private static bool TryDecodeSignature(string value, out byte[] bytes)
    {
        bytes = [];
        if (!value.StartsWith("sha256=", StringComparison.Ordinal)
            || value.Length != 71)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value[7..]);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - normalized.Length % 4) % 4),
                '=');
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static JsonElement EvaluateLease(JsonElement input)
    {
        var leases = new Dictionary<string, Lease>(StringComparer.Ordinal);
        var terminal = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new List<string>();
        foreach (var item in input.GetProperty("events").EnumerateArray())
        {
            var at = item.GetProperty("at").GetInt64();
            var op = item.GetProperty("op").GetString();
            switch (op)
            {
                case "claim":
                    {
                        var taskId = item.GetProperty("task_id").GetString()!;
                        var runner = item.GetProperty("runner_nid").GetString()!;
                        var seconds = Math.Clamp(item.GetProperty("lease_seconds").GetInt32(), 10, 600);
                        var exists = leases.TryGetValue(taskId, out var lease);
                        if (exists && lease!.ExpiresAt > at)
                        {
                            if (string.Equals(lease.RunnerNid, runner, StringComparison.Ordinal))
                            {
                                leases[taskId] = lease with { ExpiresAt = at + seconds };
                                outcomes.Add("granted");
                            }
                            else
                            {
                                outcomes.Add("conflict");
                            }
                        }
                        else
                        {
                            leases[taskId] = new Lease(runner, at + seconds);
                            outcomes.Add(exists ? "reclaimed" : "granted");
                        }
                        break;
                    }
                case "renew":
                    {
                        var taskId = item.GetProperty("task_id").GetString()!;
                        var runner = item.GetProperty("runner_nid").GetString()!;
                        var seconds = Math.Clamp(item.GetProperty("lease_seconds").GetInt32(), 10, 600);
                        if (leases.TryGetValue(taskId, out var lease)
                            && lease.ExpiresAt > at
                            && string.Equals(lease.RunnerNid, runner, StringComparison.Ordinal))
                        {
                            leases[taskId] = lease with { ExpiresAt = at + seconds };
                            outcomes.Add("granted");
                        }
                        else
                        {
                            outcomes.Add("conflict");
                        }
                        break;
                    }
                case "mark_terminal":
                    terminal.Add(TerminalKey(item));
                    outcomes.Add("recorded");
                    break;
                case "is_terminal":
                    outcomes.Add(terminal.Contains(TerminalKey(item)) ? "terminal" : "pending");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown lease operation '{op}'.");
            }
        }

        return JsonSerializer.SerializeToElement(new { outcomes });
    }

    private static string TerminalKey(JsonElement item) =>
        $"{item.GetProperty("dedup_key").GetString()}\0{item.GetProperty("node_id").GetString()}";

    private static JsonElement EvaluateDelegation(JsonElement input)
    {
        if (!ScopeIsSubset(
                input.GetProperty("parent_scope"),
                input.GetProperty("delegated_scope")))
        {
            return JsonSerializer.SerializeToElement(new
            {
                targets = Array.Empty<string>(),
                error = NopErrorCodes.DelegateScopeViolation,
            });
        }

        var targets = new List<string>();
        foreach (var attempt in input.GetProperty("attempts").EnumerateArray())
        {
            var live = attempt.GetProperty("candidates")
                .EnumerateArray()
                .Where(item => item.GetProperty("live").GetBoolean())
                .Select(item => new
                {
                    Nid = item.GetProperty("nid").GetString()!,
                    Epoch = item.GetProperty("cluster_epoch").GetUInt64(),
                })
                .ToList();
            if (live.Count == 0)
                return DelegationFailure(targets, NopErrorCodes.DelegateRejected);
            var top = live.Max(item => item.Epoch);
            var leaders = live.Where(item => item.Epoch == top).ToList();
            if (leaders.Count != 1)
                return DelegationFailure(targets, ClusterSplit);
            targets.Add(leaders[0].Nid);
        }

        return JsonSerializer.SerializeToElement(new
        {
            targets,
            error = (string?)null,
        });
    }

    private static JsonElement DelegationFailure(IReadOnlyList<string> targets, string error) =>
        JsonSerializer.SerializeToElement(new { targets, error });

    private static bool ScopeIsSubset(JsonElement parent, JsonElement delegated) =>
        IsStringSubset(parent, delegated, "nodes")
        && IsStringSubset(parent, delegated, "actions")
        && delegated.GetProperty("max_token_budget").GetUInt64()
            <= parent.GetProperty("max_token_budget").GetUInt64();

    private static bool IsStringSubset(JsonElement parent, JsonElement delegated, string name)
    {
        var allowed = parent.GetProperty(name)
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        return delegated.GetProperty(name)
            .EnumerateArray()
            .All(item => allowed.Contains(item.GetString()!));
    }

    private static JsonElement EvaluateSpawnSpec(JsonElement input)
    {
        var spec = input.GetProperty("spawn_spec");
        var valid = spec.TryGetProperty("image", out var image)
            && !string.IsNullOrWhiteSpace(image.GetString());
        if (valid
            && spec.TryGetProperty("idle_timeout_seconds", out var idle)
            && spec.TryGetProperty("max_runtime_seconds", out var maximum)
            && idle.GetUInt64() > maximum.GetUInt64())
        {
            valid = false;
        }

        return JsonSerializer.SerializeToElement(new
        {
            error = valid ? null : NopErrorCodes.SpawnSpecInvalid,
        });
    }

    private static JsonElement EvaluateLifecycle(JsonElement input)
    {
        string state;
        string? error;
        if (input.GetProperty("elapsed_seconds").GetUInt64()
            >= input.GetProperty("max_runtime_seconds").GetUInt64())
        {
            state = "failed";
            error = NopErrorCodes.RuntimeMaxRuntime;
        }
        else if (input.GetProperty("idle_seconds").GetUInt64()
            >= input.GetProperty("idle_timeout_seconds").GetUInt64())
        {
            state = "failed";
            error = NopErrorCodes.RuntimeIdleTimeout;
        }
        else if (string.Equals(OptionalString(input, "worker_terminal"), "done", StringComparison.Ordinal))
        {
            state = "completed";
            error = null;
        }
        else
        {
            state = "failed";
            error = NopErrorCodes.DelegateRejected;
        }

        return JsonSerializer.SerializeToElement(new { state, error });
    }

    private static IReadOnlyList<string>? StableTopologicalOrder(
        IReadOnlyDictionary<string, JsonElement> nodes)
    {
        var indegree = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var (id, node) in nodes)
        {
            foreach (var dependency in Dependencies(node))
            {
                if (!nodes.ContainsKey(dependency)) return null;
                indegree[id]++;
                outgoing[dependency].Add(id);
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var order = new List<string>(nodes.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var next in outgoing[id].Order(StringComparer.Ordinal))
            {
                indegree[next]--;
                if (indegree[next] == 0) ready.Add(next);
            }
        }

        return order.Count == nodes.Count ? order : null;
    }

    private static IEnumerable<string> Dependencies(JsonElement node) =>
        node.GetProperty("depends_on")
            .EnumerateArray()
            .Select(item => item.GetString()!);

    private static bool? EvaluateCondition(
        string expression,
        IReadOnlyDictionary<string, JsonNode?> results)
    {
        var match = ConditionPattern().Match(expression);
        if (!match.Success) return null;
        var root = new JsonObject();
        foreach (var (key, value) in results)
            root[key] = value?.DeepClone();
        var left = ResolvePath(root, $"$.{match.Groups["path"].Value}");
        if (left is null) return null;
        JsonNode? right;
        try
        {
            right = JsonNode.Parse(match.Groups["literal"].Value);
        }
        catch (JsonException)
        {
            right = JsonValue.Create(match.Groups["literal"].Value.Trim('"'));
        }

        return Compare(left, right, match.Groups["op"].Value);
    }

    private static bool? Compare(JsonNode left, JsonNode? right, string operation)
    {
        if (double.TryParse(
                left.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var leftNumber)
            && double.TryParse(
                right?.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var rightNumber))
        {
            return operation switch
            {
                "==" => leftNumber == rightNumber,
                "!=" => leftNumber != rightNumber,
                ">" => leftNumber > rightNumber,
                ">=" => leftNumber >= rightNumber,
                "<" => leftNumber < rightNumber,
                "<=" => leftNumber <= rightNumber,
                _ => null,
            };
        }

        var leftText = left.ToJsonString();
        var rightText = right?.ToJsonString() ?? "null";
        return operation switch
        {
            "==" => string.Equals(leftText, rightText, StringComparison.Ordinal),
            "!=" => !string.Equals(leftText, rightText, StringComparison.Ordinal),
            _ => null,
        };
    }

    private static JsonObject? ResolveMapping(
        JsonElement mapping,
        IReadOnlyDictionary<string, JsonNode?> results)
    {
        var root = new JsonObject();
        foreach (var (key, value) in results)
            root[key] = value?.DeepClone();
        var output = new JsonObject();
        foreach (var property in mapping.EnumerateObject())
        {
            var value = ResolvePath(root, property.Value.GetString()!);
            if (value is null) return null;
            output[property.Name] = value.DeepClone();
        }
        return output;
    }

    private static JsonNode? ResolvePath(JsonNode root, string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal)) return null;
        JsonNode? current = root;
        foreach (var segment in path[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
                return null;
        }
        return current;
    }

    private static IReadOnlyList<string> RunCompensation(
        JsonElement task,
        string failedId,
        IReadOnlyList<string> topo,
        IReadOnlyDictionary<string, JsonElement> nodes,
        IDictionary<string, string> states,
        ICollection<string> events,
        out string? error)
    {
        error = null;
        var policy = OptionalString(task, "compensation_policy");
        if (policy is not ("best_effort" or "strict")) return [];
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        CollectAncestors(failedId, nodes, ancestors);
        var candidates = topo
            .Where(id => ancestors.Contains(id)
                && states.TryGetValue(id, out var state)
                && state == "completed")
            .Reverse()
            .ToList();
        if (policy == "strict"
            && candidates.Any(id => !nodes[id].TryGetProperty("compensate_action", out _)))
        {
            error = NopErrorCodes.CompensationNotSupported;
            return [];
        }

        var order = new List<string>();
        foreach (var id in candidates)
        {
            var node = nodes[id];
            if (!node.TryGetProperty("compensate_action", out _)) continue;
            order.Add(id);
            events.Add($"{id}:compensating");
            if (string.Equals(
                    OptionalString(node, "compensation_outcome"),
                    "failure",
                    StringComparison.Ordinal))
            {
                states[id] = "compensation_failed";
                events.Add($"{id}:compensation_failed");
                if (policy == "strict")
                {
                    error = NopErrorCodes.CompensationFailed;
                    break;
                }
            }
            else
            {
                states[id] = "compensated";
                events.Add($"{id}:compensated");
            }
        }
        return order;
    }

    private static void CollectAncestors(
        string id,
        IReadOnlyDictionary<string, JsonElement> nodes,
        ISet<string> output)
    {
        foreach (var dependency in Dependencies(nodes[id]))
        {
            if (output.Add(dependency))
                CollectAncestors(dependency, nodes, output);
        }
    }

    private static JsonNode? Aggregate(
        JsonElement task,
        IReadOnlyList<string> topo,
        IReadOnlyDictionary<string, JsonElement> nodes,
        IReadOnlyDictionary<string, string> states,
        IReadOnlyDictionary<string, JsonNode?> results)
    {
        var hasOutgoing = nodes.Values
            .SelectMany(Dependencies)
            .ToHashSet(StringComparer.Ordinal);
        var values = topo
            .Where(id => !hasOutgoing.Contains(id)
                && states.GetValueOrDefault(id) == "completed"
                && results.ContainsKey(id))
            .Select(id => results[id]?.DeepClone())
            .Where(value => value is not null)
            .Cast<JsonNode>()
            .ToList();
        if (values.Count == 0) return null;
        var strategy = OptionalString(task, "aggregate") ?? "merge";
        if (strategy == "all") return new JsonArray(values.ToArray());
        var output = new JsonObject();
        foreach (var value in values)
        {
            if (value is not JsonObject obj) continue;
            foreach (var property in obj)
            {
                if (strategy == "merge_all"
                    && output[property.Key] is JsonArray existing
                    && property.Value is JsonArray incoming)
                {
                    foreach (var item in incoming)
                        existing.Add(item?.DeepClone());
                }
                else
                {
                    output[property.Key] = property.Value?.DeepClone();
                }
            }
        }
        return output;
    }

    private static JsonElement FailureResult(
        IReadOnlyList<string> events,
        string error) =>
        BuildResult(
            events,
            "failed",
            error,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, JsonNode?>(),
            []);

    private static JsonElement BuildResult(
        IReadOnlyList<string> events,
        string state,
        string? error,
        JsonNode? aggregate,
        IReadOnlyDictionary<string, string> states,
        IReadOnlyDictionary<string, int> attempts,
        IReadOnlyDictionary<string, JsonNode?> mapped,
        IReadOnlyList<string> compensation)
    {
        var root = new JsonObject
        {
            ["events"] = new JsonArray(
                events.Select(value => JsonValue.Create(value)).ToArray()),
            ["terminal_state"] = state,
            ["error_code"] = error,
            ["aggregate"] = aggregate?.DeepClone(),
            ["node_states"] = ToObject(states),
            ["attempt_counts"] = ToObject(attempts),
            ["mapped_params"] = ToObject(mapped),
            ["compensation_order"] = new JsonArray(
                compensation.Select(value => JsonValue.Create(value)).ToArray()),
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject ToObject<T>(IReadOnlyDictionary<string, T> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result[key] = JsonSerializer.SerializeToNode(value);
        return result;
    }

    private static bool OptionalBool(JsonElement element, string name, bool fallback = false) =>
        element.TryGetProperty(name, out var property) ? property.GetBoolean() : fallback;

    private static int OptionalInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private const string TaskFailed = "task:failed";

    private sealed record Lease(string RunnerNid, long ExpiresAt);

    [GeneratedRegex(
        @"^\$\.(?<path>[A-Za-z0-9_.-]+)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<literal>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConditionPattern();
}
