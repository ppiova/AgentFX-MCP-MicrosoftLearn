using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

class Program
{
    static async Task Main()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Cancellation requested. Finishing current step...");
        };

        var configuration = BuildConfiguration();
        var retryMaxAttempts = GetOptionalSetting(configuration, "Retry:MaxAttempts") is string attempts && int.TryParse(attempts, out var parsedAttempts)
            ? parsedAttempts
            : 3;
        var retryBaseDelayMs = GetOptionalSetting(configuration, "Retry:BaseDelayMs") is string delay && int.TryParse(delay, out var parsedDelay)
            ? parsedDelay
            : 500;
        // === 1) Connect to Microsoft Learn MCP Server (HTTP/Streamable HTTP) ===
        var learnEndpoint = GetOptionalSetting(configuration, "LEARN_MCP_ENDPOINT", "LearnMcp:Endpoint")
            ?? "https://learn.microsoft.com/api/mcp";
        if (!Uri.TryCreate(learnEndpoint, UriKind.Absolute, out var learnEndpointUri))
            throw new InvalidOperationException("Invalid LEARN_MCP_ENDPOINT. Provide a valid absolute URL.");

        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "MicrosoftLearn",
            Endpoint = learnEndpointUri
        });

        await using var mcp = await RunWithRetryAsync(
            () => McpClient.CreateAsync(httpTransport),
            operationName: "MCP connect",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cts.Token);
        Console.WriteLine("Connected to Learn MCP.");

        // === 2) Discover MCP tools dynamically ===
        var mcpTools = (await RunWithRetryAsync(
            () => mcp.ListToolsAsync(),
            operationName: "MCP list tools",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cts.Token)).Cast<AITool>().ToList();
        Console.WriteLine("Tools exposed by Learn MCP:");
        foreach (var t in mcpTools) Console.WriteLine($" - {t.Name}");

        // === 3) Create the Microsoft Agent Framework agent ===
        // (we use Azure OpenAI as the chat backend; you can change it to another IChatClient)
        var endpoint   = GetRequiredSetting(configuration, "AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
        var deployment = GetRequiredSetting(configuration, "AZURE_OPENAI_DEPLOYMENT_NAME", "AzureOpenAI:DeploymentName");

        IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        // Instructions that guide the agent to use Learn tools for Microsoft topics
        var systemPrompt =
            "You are an expert agent in Microsoft technologies. " +
            "For any question about Azure/.NET/Windows/VS/Entra/M365, " +
            "you MUST first use the Microsoft Learn MCP Server tools " +
            "(search/fetch/code samples) and cite the official URL.";

        AIAgent agent = chatClient.CreateAIAgent(
            instructions: systemPrompt,
            name: "DocsAgent",
            tools: mcpTools
        );

        // === 4) Example execution: the question forces the use of official docs ===
        var demoQuestion =
            "I need information on how to create an agent in Azure AI Foundry Agents.";

        var result = await RunWithRetryAsync(
            () => agent.RunAsync(demoQuestion),
            operationName: "Agent run",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cts.Token);
        Console.WriteLine("\n=== Agent Response ===\n");
        Console.WriteLine(result.Text);

        // Tip: if you want to see Traces/Observability, check the Agent Framework repo. 
        // (includes examples and guides for logging/telemetry and latest releases). 
    }

    static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    static string? GetOptionalSetting(IConfiguration config, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = config[key]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    static string GetRequiredSetting(IConfiguration config, params string[] keys)
    {
        var value = GetOptionalSetting(config, keys);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required setting. Set one of: {string.Join(", ", keys)}");
    }

    static async Task<T> RunWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        int maxAttempts = 3,
        int baseDelayMs = 500,
        System.Threading.CancellationToken cancellationToken = default)
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
                Console.WriteLine($"Transient error during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                await Task.Delay(delay, cancellationToken);
            }
        }

        // Final attempt without catching
        return await operation();
    }

    static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is TimeoutException
            || (ex is RequestFailedException rfe && (rfe.Status == 429 || rfe.Status >= 500));
    }
}
