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

public sealed record ChatAgentOptions(
    string Endpoint,
    string Model,
    string ApiKey,
    ReasoningEffort Reasoning = ReasoningEffort.Default,
    string? Instructions = null);

/// <summary>
/// Owns the <see cref="AIAgent"/> and its conversation session.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public sealed class ChatSession
{
    private const string DefaultInstructions =
        "You are a helpful Windows PC assistant running inside the WindowsUtils app. " +
        "Use the provided tools whenever the user asks about this PC (system, processes, drives, files). " +
        "All tools are read-only; never claim to change anything. Keep answers concise.";

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
            AIFunctionFactory.Create(new Func<string, int, string>(PcTools.ReadTextFile)),
            AIFunctionFactory.Create(new Func<string, string>(PcTools.GetEnvironmentVariable)),
            AIFunctionFactory.Create(new Func<string, string, string>(PcTools.ComputeFileHash)),
            AIFunctionFactory.Create(new Func<string, string, string>(PcTools.VerifyFileHash)),
        ];
        return chatClient.AsAIAgent(
            options.Instructions ?? DefaultInstructions,
            "windows-utils-assistant",
            "Windows PC assistant",
            tools,
            BuildClientFactory(options.Reasoning),
            null,
            null);
    }

    private static Func<IChatClient, IChatClient>? BuildClientFactory(ReasoningEffort reasoning)
    {
        var level = ToChatReasoningEffortLevel(reasoning);
        if (level is null)
            return null;
        var effort = level.Value;
        return inner => new ReasoningEffortChatClient(inner, effort);
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
    public async IAsyncEnumerable<string> StreamResponseAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = _session ??= await _agent.CreateSessionAsync(cancellationToken);
        await foreach (var update in _agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
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
