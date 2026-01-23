using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

class AgentApp
{
    private IConfiguration? configuration;
    private int retryMaxAttempts = 3;
    private int retryBaseDelayMs = 500;

    // Entry point for the demo app lifecycle
    public async Task RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            ConsoleUi.PrintInfo("Cancellation requested. Finishing current step...");
        };

        configuration = BuildConfiguration();
        LoadRetrySettings();

        await using var mcp = await ConnectToMcp(cts.Token);
        var mcpTools = await LoadTools(mcp, cts.Token);

        var agent = BuildAgent(mcpTools);
        await RunDemo(agent, args, cts.Token);
    }

    // Load retry settings from configuration
    private void LoadRetrySettings()
    {
        retryMaxAttempts = GetOptionalSetting("Retry:MaxAttempts") is string attempts && int.TryParse(attempts, out var parsedAttempts)
            ? parsedAttempts
            : 3;
        retryBaseDelayMs = GetOptionalSetting("Retry:BaseDelayMs") is string delay && int.TryParse(delay, out var parsedDelay)
            ? parsedDelay
            : 500;
    }

    // Connect to Microsoft Learn MCP server
    private async Task<McpClient> ConnectToMcp(CancellationToken cancellationToken)
    {
        ConsoleUi.PrintInfo("🔌 Connecting to Microsoft Learn MCP Server...");
        var learnEndpoint = GetOptionalSetting("LEARN_MCP_ENDPOINT", "LearnMcp:Endpoint")
            ?? "https://learn.microsoft.com/api/mcp";
        if (!Uri.TryCreate(learnEndpoint, UriKind.Absolute, out var learnEndpointUri))
            throw new InvalidOperationException("Invalid LEARN_MCP_ENDPOINT. Provide a valid absolute URL.");

        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "MicrosoftLearn",
            Endpoint = learnEndpointUri
        });

        var mcp = await RunWithRetryAsync(
            () => McpClient.CreateAsync(httpTransport),
            operationName: "MCP connect",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationToken);

        ConsoleUi.PrintSuccess("✓ Connected to Learn MCP");
        return mcp;
    }

    // Discover MCP tools available to the agent
    private async Task<List<AITool>> LoadTools(McpClient mcp, CancellationToken cancellationToken)
    {
        ConsoleUi.PrintInfo("🔧 Discovering tools from MCP...");

        var tools = await RunWithRetryAsync(
            async () => (await mcp.ListToolsAsync()).Cast<AITool>().ToList(),
            operationName: "MCP list tools",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationToken);

        ConsoleUi.PrintToolSummary(tools.Select(t => t.Name).ToList());
        return tools;
    }

    // Build the agent with MCP tools and a system prompt
    private AIAgent BuildAgent(List<AITool> tools)
    {
        ConsoleUi.PrintInfo("🤖 Initializing AI Agent...");

        var endpoint = GetRequiredSetting("AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
        var deployment = GetRequiredSetting("AZURE_OPENAI_DEPLOYMENT_NAME", "AzureOpenAI:DeploymentName");

        IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        var systemPrompt =
            "You are an expert agent in Microsoft technologies. " +
            "For any question about Azure/.NET/Windows/VS/Entra/M365, " +
            "you MUST first use the Microsoft Learn MCP Server tools " +
            "(search/fetch/code samples) and cite the official URL.";

        var agent = chatClient.CreateAIAgent(
            instructions: systemPrompt,
            name: "DocsAgent",
            tools: tools
        );

        ConsoleUi.PrintSuccess("✓ Agent initialized");
        return agent;
    }

    // Run a single demo question through the agent
    private async Task RunDemo(AIAgent agent, string[] args, CancellationToken cancellationToken)
    {
        var demoQuestion = ResolveDemoQuestion(args)
            ?? "I need information on how to create an agent in Azure AI Foundry Agents.";

        ConsoleUi.PrintInfo("🧪 Running demo question...");

        var result = await RunWithRetryAsync(
            () => agent.RunAsync(demoQuestion),
            operationName: "Agent run",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationToken);

        Console.WriteLine("\n=== Agent Response ===\n");
        Console.WriteLine(result.Text);
    }

    // Resolve demo question from args or configuration
    private string? ResolveDemoQuestion(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            return string.Join(' ', args);

        return GetOptionalSetting("DEMO_QUESTION", "Demo:Question");
    }

    // Build configuration from appsettings and environment variables
    private IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    // Retrieve an optional setting from configuration
    private string? GetOptionalSetting(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration?[key]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    // Retrieve a required setting from configuration
    private string GetRequiredSetting(params string[] keys)
    {
        var value = GetOptionalSetting(keys);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required setting. Set one of: {string.Join(", ", keys)}");
    }

    // Execute an operation with exponential backoff retries
    private async Task<T> RunWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        int maxAttempts = 3,
        int baseDelayMs = 500,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt - 1));
                if (ex is RequestFailedException rfe && rfe.Status == 429)
                {
                    ConsoleUi.PrintError($"Rate limit during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                else if (ex is RequestFailedException rfe2)
                {
                    ConsoleUi.PrintError($"Service error {rfe2.Status} during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                else
                {
                    ConsoleUi.PrintError($"Transient error during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                await Task.Delay(delay, cancellationToken);
            }
        }

        return await operation();
    }

    // Identify transient failures worth retrying
    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is TimeoutException
            || (ex is RequestFailedException rfe && (rfe.Status == 429 || rfe.Status >= 500));
    }
}
