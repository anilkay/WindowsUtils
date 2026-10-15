using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsUtils.AI;

/// <summary>Settings for the locally hosted MCP server.</summary>
/// <param name="Agent">Endpoint, model and key used by the agent exposed as the ask_windows_utils_agent tool.</param>
/// <param name="ApproveToolCall">Asked before a tool reads a file's text or an environment variable;
/// when null those calls are declined. Runs on a background (Kestrel) thread, so a UI callback must marshal itself.</param>
/// <param name="Port">First port to try; if it is already in use the next ports are tried too.</param>
public sealed record McpServerHostOptions(
    ChatAgentOptions Agent,
    Func<ToolApprovalRequest, CancellationToken, Task<bool>>? ApproveToolCall = null,
    int Port = McpServerHost.DefaultPort);

/// <summary>
/// Hosts a local Model Context Protocol server (streamable HTTP) that exposes the WindowsUtils AI
/// agent as the ask_windows_utils_agent tool and the read-only PcTools as native MCP tools, so any
/// MCP client on this PC (VS Code, Claude Desktop, another agent) can use them. Started/stopped by
/// the AI Chat screen. Binds loopback only: nothing outside this PC can connect, there is no
/// authentication, but any local process can call it while it runs.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int DefaultPort = 5199;
    private const int PortAttempts = 10;
    private const string Path = "/mcp";

    private readonly McpServerHostOptions _options;
    private readonly ChatSession _agent;
    // The agent owns one conversation session; two MCP clients (or two parallel calls) must take turns.
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private WebApplication? _app;
    private bool _stopped;

    /// <summary>The URL an MCP client connects to, e.g. http://localhost:5199/mcp.</summary>
    public string Url { get; private set; } = "";

    private McpServerHost(McpServerHostOptions options, ChatSession agent)
    {
        _options = options;
        _agent = agent;
    }

    /// <summary>Starts the server, trying <see cref="McpServerHostOptions.Port"/> and the following ports.</summary>
    public static async Task<McpServerHost> StartAsync(McpServerHostOptions options, CancellationToken cancellationToken = default)
    {
        // Rejects incomplete endpoint/model/key settings before Kestrel starts, so the caller can show the message.
        var host = new McpServerHost(options, ChatSession.Create(options.Agent));
        try
        {
            await host.StartCoreAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var port = _options.Port; port < _options.Port + PortAttempts; port++)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders(); // the app has no console to log to
            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = "windowsutils", Title = "WindowsUtils", Version = "1.0" };
                    o.ServerInstructions =
                        "Read-only tools that inspect this Windows PC: system, drives, processes, network, firewall, "
                        + "startup programs, local users, folders, files and hashes. ask_windows_utils_agent is an AI "
                        + "assistant that answers questions about this PC with the same tools. Nothing is ever changed. "
                        + "Reading a file's text or an environment variable value asks the desktop user for permission first.";
                })
                .WithHttpTransport()
                .WithTools(CreateTools());
            builder.WebHost.UseUrls($"http://localhost:{port}");
            var app = builder.Build();
            // MCP security requirement (DNS rebinding): a browser page on another origin must not be
            // able to call these tools. Requests without an Origin header (normal MCP clients) pass;
            // a present-but-foreign Origin gets a 403.
            var serverPort = port;
            app.Use(async (context, next) =>
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && !IsThisServerOrigin(origin, serverPort))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            });
            app.MapMcp(Path);
            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await app.DisposeAsync();
                if (ex is not (IOException or SocketException))
                    throw;
                // Another program already listens on this port; try the next one.
                lastError = ex;
                continue;
            }
            _app = app;
            Url = $"http://localhost:{port}{Path}";
            return;
        }
        throw new InvalidOperationException(
            $"Could not start the MCP server: ports {_options.Port} to {_options.Port + PortAttempts - 1} are all in use. "
            + lastError?.Message);
    }

    // Only the server's own loopback origins are accepted (browsers use the scheme+host+port
    // form). Non-browser MCP clients send no Origin at all and skip this check.
    private static bool IsThisServerOrigin(string origin, int port) =>
        origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
        || origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<McpServerTool> CreateTools()
    {
        var tools = new List<McpServerTool> { CreateAgentTool() };
        foreach (var (function, needsApproval, openWorld) in PcTools.CreateTools())
        {
            // The private-data tools keep their "ask the user first" rule on this path too.
            var exposed = needsApproval ? new ApprovalGateFunction(function, _options.ApproveToolCall) : function;
            tools.Add(McpServerTool.Create(exposed, new McpServerToolCreateOptions
            {
                // Destructive defaults to true in the MCP spec when the hint is absent.
                ReadOnly = true,
                Destructive = false,
                OpenWorld = openWorld,
            }));
        }
        return tools;
    }

    private McpServerTool CreateAgentTool()
    {
        var function = PcTools.CreateFunction(
            (string request, CancellationToken cancellationToken) => AskAgentAsync(request, cancellationToken),
            "ask_windows_utils_agent",
            "Asks the WindowsUtils AI assistant a question about this PC. It can combine the other tools "
            + "(system info, drives, folders, files, network) and answers in natural language; use it for questions "
            + "that need judgement or several data points. Read-only; may ask the desktop user to allow reading a file.");
        return McpServerTool.Create(function, new McpServerToolCreateOptions
        {
            ReadOnly = true,
            Destructive = false,
            OpenWorld = true, // every call goes out to the configured AI endpoint
        });
    }

    private async Task<string> AskAgentAsync(string request, CancellationToken cancellationToken)
    {
        await _agentLock.WaitAsync(cancellationToken);
        try
        {
            // Each MCP call is a fresh question: the MCP client owns the conversation, not the server.
            _agent.Reset();
            try
            {
                return await RunAgentAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ChatSession.IsReasoningEffortError(ex) && _agent.HasReasoningEffort)
            {
                // The model rejects reasoning_effort: drop it and retry once, like the chat screen does.
                _agent.DisableReasoningEffort();
                _agent.Reset();
                return await RunAgentAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _agent.Reset(); // a failed turn can leave the session in an unusable state
                return $"Error: {ex.Message}";
            }
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private async Task<string> RunAgentAsync(string request, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var update in _agent.StreamResponseAsync(request, _options.ApproveToolCall, cancellationToken))
        {
            if (update.Text is not null)
                text.Append(update.Text);
        }
        return text.Length > 0 ? text.ToString() : "(the agent returned no text)";
    }

    /// <summary>Stops the server and releases the port. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        var app = _app;
        _app = null;
        if (app is null)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await app.StopAsync(timeout.Token);
        }
        catch
        {
            // In-flight requests are cut off when the app closes anyway.
        }
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // Wraps a private-data tool (ReadTextFile, GetEnvironmentVariable) so the desktop user is asked
    // before each MCP call, matching chat where these are ApprovalRequiredAIFunction.
    private sealed class ApprovalGateFunction(AIFunction inner, Func<ToolApprovalRequest, CancellationToken, Task<bool>>? approve)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var call = new FunctionCallContent("mcp", Name, arguments);
            var request = ChatSession.DescribeToolCall(call, "the connected MCP client");
            if (approve is null || !await approve(request, cancellationToken))
                return "The user did not allow this tool call.";
            return await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
    }
}
