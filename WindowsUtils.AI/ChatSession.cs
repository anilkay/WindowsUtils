using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

// ChatReasoningEffortLevel is marked experimental (OPENAI001) but is the only
// way to send reasoning_effort; scoped suppression for this file.
#pragma warning disable OPENAI001

namespace WindowsUtils.AI;

/// <summary>Which reasoning effort to request from the model. Default sends nothing.</summary>
public enum ReasoningEffort
{
    Default,
    Minimal,
    Low,
    Medium,
    High,
}

/// <param name="ResponseTimeout">How long the endpoint may send nothing before a model call
/// fails; null uses <see cref="ChatSession.DefaultResponseTimeout"/>.</param>
public sealed record ChatAgentOptions(
    string Endpoint,
    string Model,
    string ApiKey,
    ReasoningEffort Reasoning = ReasoningEffort.Default,
    string? Instructions = null,
    TimeSpan? ResponseTimeout = null);

/// <summary>What the agent is doing while a reply streams.</summary>
public enum ChatActivity
{
    /// <summary>Waiting for the endpoint to answer (request sent, nothing received yet).</summary>
    Waiting,
    /// <summary>The model is streaming reasoning (reasoning_content) before its answer.</summary>
    Thinking,
    /// <summary>The model asked for a tool; it is running on this PC.</summary>
    RunningTool,
    /// <summary>The model is streaming answer text.</summary>
    Writing,
}

/// <summary>One streamed item: the current activity, plus answer text to append when <see cref="Text"/> is set.</summary>
public readonly record struct ChatStreamUpdate(ChatActivity Activity, string? Text = null, string? ToolName = null);

/// <summary>A tool call that runs only if the user allows it (see <see cref="ChatSession.StreamResponseAsync"/>).</summary>
/// <param name="ToolName">The tool the model asked for, e.g. ReadTextFile.</param>
/// <param name="Prompt">A ready-to-show question naming what would be read and where it is sent.</param>
public sealed record ToolApprovalRequest(string ToolName, string Prompt);

/// <summary>
/// Owns the <see cref="AIAgent"/> and its conversation session.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public sealed class ChatSession
{
    private const string DefaultInstructions =
        "You are a helpful Windows PC assistant running inside the WindowsUtils app. " +
        "Use the provided tools whenever the user asks about this PC (system, processes, drives, files). " +
        "All tools are read-only; never claim to change anything. " +
        "Reading a file's text or an environment variable asks the user first; if the user declines, " +
        "do not ask for the same item again, just say you were not allowed. Keep answers concise.";

    /// <summary>Default for <see cref="ChatAgentOptions.ResponseTimeout"/>. Reasoning models stream
    /// their thinking, which counts as data, so only a silent endpoint hits this. Kept below the
    /// OpenAI SDK's 100 s network timeout so this clear error fires before the SDK's own retries.</summary>
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(90);

    private AIAgent _agent;
    private AgentSession? _session;
    private ChatAgentOptions _options;

    private ChatSession(ChatAgentOptions options, AIAgent agent)
    {
        _options = options;
        _agent = agent;
    }

    public static ChatSession Create(ChatAgentOptions options)
    {
        var endpoint = options.Endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Enter a model name (e.g. gpt-4o-mini).");
        // API keys never contain meaningful surrounding whitespace (often pasted with a stray space/newline).
        var apiKey = options.ApiKey?.Trim() ?? "";
        if (apiKey.Length == 0)
            throw new InvalidOperationException("Enter an API key.");

        var normalized = options with { Endpoint = endpoint, ApiKey = apiKey };
        return new ChatSession(normalized, BuildAgent(normalized, endpointUri));
    }

    private static AIAgent BuildAgent(ChatAgentOptions options, Uri endpointUri)
    {
        var chatClient = PcTools.CreateChatClient(options.Model, options.ApiKey, endpointUri);
        AITool[] tools =
        [
            AIFunctionFactory.Create(new Func<string>(PcTools.GetCurrentTime)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetSystemInfo)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetNetFrameworkVersion)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetJavaVersion)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetPythonVersion)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetNodeVersion)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetWindowsFeatures)),
            AIFunctionFactory.Create(new Func<int, string>(PcTools.GetProcesses)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetDrives)),
            AIFunctionFactory.Create(new Func<string, string, int, string>(PcTools.ListFiles)),
            AIFunctionFactory.Create(new Func<string, string, int, string>(PcTools.ListFolders)),
            AIFunctionFactory.Create(new Func<string, int, bool, string>(PcTools.GetLargestFiles)),
            AIFunctionFactory.Create(new Func<string, int, int, CancellationToken, string>(PcTools.FindDuplicateFiles)),
            AIFunctionFactory.Create(new Func<string, int, CancellationToken, string>(PcTools.GetFolderSizes)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetStartupPrograms)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetLocalUsers)),
            AIFunctionFactory.Create(new Func<string>(PcTools.GetNetworkAdapters)),
            AIFunctionFactory.Create(new Func<string, int, CancellationToken, Task<string>>(PcTools.PingHost)),
            // These return file contents and environment variable values (which can hold secrets),
            // so each call waits for the user's permission; see StreamResponseAsync.
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(new Func<string, int, string>(PcTools.ReadTextFile))),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(new Func<string, string>(PcTools.GetEnvironmentVariable))),
            AIFunctionFactory.Create(new Func<string, string, string>(PcTools.ComputeFileHash)),
            AIFunctionFactory.Create(new Func<string, string, string>(PcTools.VerifyFileHash)),
        ];
        return chatClient.AsAIAgent(
            options.Instructions ?? DefaultInstructions,
            "windows-utils-assistant",
            "Windows PC assistant",
            tools,
            BuildClientFactory(options),
            null,
            null);
    }

    // Wraps each model call made by the agent (including the calls inside its tool loop).
    private static Func<IChatClient, IChatClient> BuildClientFactory(ChatAgentOptions options)
    {
        var timeout = options.ResponseTimeout ?? DefaultResponseTimeout;
        var level = ToChatReasoningEffortLevel(options.Reasoning);
        return inner =>
        {
            IChatClient client = new ResponseTimeoutChatClient(inner, timeout);
            if (level is { } effort)
                client = new ReasoningEffortChatClient(client, effort);
            return client;
        };
    }

    // NOTE: do not use a switch expression with a `_ => null` arm here. Roslyn compiles
    // the null arm through the struct's implicit string operator with a null string,
    // which throws ArgumentNullException. Plain `return null` is safe.
    internal static OpenAI.Chat.ChatReasoningEffortLevel? ToChatReasoningEffortLevel(ReasoningEffort reasoning)
    {
        if (reasoning == ReasoningEffort.Minimal)
            return OpenAI.Chat.ChatReasoningEffortLevel.Minimal;
        if (reasoning == ReasoningEffort.Low)
            return OpenAI.Chat.ChatReasoningEffortLevel.Low;
        if (reasoning == ReasoningEffort.Medium)
            return OpenAI.Chat.ChatReasoningEffortLevel.Medium;
        if (reasoning == ReasoningEffort.High)
            return OpenAI.Chat.ChatReasoningEffortLevel.High;
        return null;
    }

    public bool HasReasoningEffort => _options.Reasoning != ReasoningEffort.Default;

    /// <summary>Rebuilds the agent without reasoning_effort and drops the session.</summary>
    public void DisableReasoningEffort()
    {
        if (!HasReasoningEffort)
            return;
        _options = _options with { Reasoning = ReasoningEffort.Default };
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        _agent = BuildAgent(_options, endpointUri);
        _session = null;
    }

    /// <summary>Drops the conversation session so the next call starts fresh.</summary>
    public void Reset() => _session = null;

    // Note: no try/catch here (yield is not allowed in a try block with a catch
    // clause). Callers must call Reset() after a failed turn: a failed turn can
    // leave the session in an unusable state (e.g. a 400 mid-run).
    // A TimeoutException (silent endpoint) leaves the session intact: the failed turn is
    // not added to the history, so the conversation can simply continue.
    /// <param name="approveToolCall">Asked before each tool call that needs the user's permission
    /// (ReadTextFile, GetEnvironmentVariable); returns true to run it. When null, those calls are declined.</param>
    public async IAsyncEnumerable<ChatStreamUpdate> StreamResponseAsync(
        string prompt,
        Func<ToolApprovalRequest, CancellationToken, Task<bool>>? approveToolCall = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = _session ??= await _agent.CreateSessionAsync(cancellationToken);
        List<Microsoft.Extensions.AI.ChatMessage> input = [new(ChatRole.User, prompt)];
        while (true)
        {
            var approvals = new List<ToolApprovalRequestContent>();
            await foreach (var update in _agent.RunStreamingAsync(input, session, cancellationToken: cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            yield return new(ChatActivity.Writing, text.Text);
                            break;
                        case TextReasoningContent:
                            yield return new(ChatActivity.Thinking);
                            break;
                        case FunctionCallContent call:
                            yield return new(ChatActivity.RunningTool, ToolName: call.Name);
                            break;
                        case FunctionResultContent:
                            // The tool finished; its result goes back to the model.
                            yield return new(ChatActivity.Waiting);
                            break;
                        case ToolApprovalRequestContent approval:
                            approvals.Add(approval);
                            break;
                    }
                }
            }
            if (approvals.Count == 0)
                yield break;

            // The run stopped before calling tools that need permission (the agent already runs the
            // other tools of the same batch itself). Send back one answer per request: approved
            // calls then run, declined ones reach the model as rejected.
            var answers = new List<AIContent>(approvals.Count);
            foreach (var approval in approvals)
            {
                var approved = approveToolCall is not null
                    && await approveToolCall(DescribeToolCall(approval.ToolCall), cancellationToken);
                answers.Add(approval.CreateResponse(approved, approved ? null : "The user did not allow this tool call."));
            }
            input = [new(ChatRole.User, answers)];
            yield return new(ChatActivity.Waiting);
        }
    }

    // The question shown before a tool call runs. The path is shown the way ReadTextFile resolves it.
    private ToolApprovalRequest DescribeToolCall(ToolCallContent toolCall)
    {
        var destination = Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var uri) ? uri.Authority : _options.Endpoint;
        if (toolCall is not FunctionCallContent call)
            return new(toolCall.GetType().Name, $"The AI model wants to run a tool. Its result will be sent to {destination}.\n\nAllow?");

        string Argument(string name) =>
            call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";
        var prompt = call.Name switch
        {
            nameof(PcTools.ReadTextFile) =>
                $"The AI model wants to read this file:\n\n{PcTools.ToLocalPath(Argument("path")) ?? Argument("path")}\n\n"
                + $"Its text will be sent to {destination}.",
            nameof(PcTools.GetEnvironmentVariable) =>
                $"The AI model wants to read this environment variable:\n\n{Argument("name")}\n\n"
                + $"Its value will be sent to {destination}.",
            _ => $"The AI model wants to run the tool {call.Name}. Its result will be sent to {destination}.",
        };
        return new(call.Name, prompt + "\n\nAllow?");
    }

    public static bool IsReasoningEffortError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when the endpoint rejected the credentials (HTTP 401/403).</summary>
    /// <remarks>Uses the HTTP status code, not the message text: messages often contain
    /// numbers such as token counts ("Requested 4031") that looked like 401/403.</remarks>
    public static bool IsUnauthorizedError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                ClientResultException clientError => clientError.Status,
                HttpRequestException { StatusCode: { } code } => (int)code,
                _ => 0,
            };
            if (status is 401 or 403)
                return true;
        }
        return false;
    }
}
