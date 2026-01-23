using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

class AgentApp
{
    private readonly List<ChatMessage> conversationHistory = new();
    private AIAgent? agent;
    private AgentThread? thread;
    private MemoryStore? memoryStore;
    private bool exitRequested;
    private string? memoryFilePath;
    private IChatClient? chatClient;
    private List<AITool>? availableTools;
    private string? baseSystemPrompt;
    private IConfiguration? configuration;
    private ILogger? logger;
    private CancellationTokenSource? cancellationTokenSource;
    private int retryMaxAttempts = 3;
    private int retryBaseDelayMs = 500;

    public async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        cancellationTokenSource = new CancellationTokenSource();

        configuration = BuildConfiguration();
        logger = BuildLogger(configuration);
        LoadRetrySettings();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitRequested = true;
            cancellationTokenSource.Cancel();
            ConsoleUi.PrintInfo("\nCancellation requested. Finishing current response...");
        };

        ConsoleUi.PrintWelcomeBanner();

        await InitializeMemoryStore();

        await using var mcp = await ConnectToMcp();
        await LoadTools(mcp);

        InitializeAgent();
        CreateThread();

        await RunInteractiveChatLoop();
    }

    private void LoadRetrySettings()
    {
        retryMaxAttempts = GetOptionalSetting("Retry:MaxAttempts") is string attempts && int.TryParse(attempts, out var parsedAttempts)
            ? parsedAttempts
            : 3;
        retryBaseDelayMs = GetOptionalSetting("Retry:BaseDelayMs") is string delay && int.TryParse(delay, out var parsedDelay)
            ? parsedDelay
            : 500;
    }

    private async Task InitializeMemoryStore()
    {
        ConsoleUi.PrintInfo("🧠 Initializing Memory Store...");
        memoryStore = new MemoryStore();
        memoryFilePath = GetOptionalSetting("MEMORY_FILE", "Memory:File") ?? "memory.json";
        logger?.LogInformation("Memory file path: {MemoryFile}", memoryFilePath);

        try
        {
            if (!memoryStore.LoadFromFile(memoryFilePath))
            {
                memoryStore.LoadDefaultUserProfile();
                memoryStore.SaveToFile(memoryFilePath);
                ConsoleUi.PrintSuccess("✓ Memory Store initialized with default profile");
            }
            else
            {
                ConsoleUi.PrintSuccess("✓ Memory Store loaded from file");
            }
        }
        catch (Exception ex)
        {
            memoryStore.LoadDefaultUserProfile();
            ConsoleUi.PrintError($"Failed to load memory file, using defaults. {ex.Message}");
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   Loaded profile: Pablo Piovano (Pablito Piova)");
        Console.ResetColor();
    }

    private async Task<McpClient> ConnectToMcp()
    {
        ConsoleUi.PrintInfo("🔌 Connecting to Microsoft Learn MCP Server...");
        var learnEndpoint = GetOptionalSetting("LEARN_MCP_ENDPOINT", "LearnMcp:Endpoint")
            ?? "https://learn.microsoft.com/api/mcp";
        logger?.LogInformation("Connecting to MCP endpoint: {Endpoint}", learnEndpoint);
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
            cancellationToken: cancellationTokenSource!.Token,
            emitConsoleTiming: true);

        ConsoleUi.PrintSuccess("✓ Connected to Learn MCP");
        return mcp;
    }

    private async Task LoadTools(McpClient mcp)
    {
        ConsoleUi.PrintInfo("🔧 Loading available tools...");
        ConsoleUi.PrintInfo("🛠️  Discovering tools from MCP...");

        var mcpTools = (await RunWithRetryAsync<IReadOnlyList<AITool>>(
            async () => (await mcp.ListToolsAsync()).Cast<AITool>().ToList(),
            operationName: "MCP list tools",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationTokenSource!.Token,
            emitConsoleTiming: true)).ToList();

        ConsoleUi.PrintSuccess("🛠️  Tool discovery completed");
        ConsoleUi.PrintSuccess($"✓ Loaded {mcpTools.Count} tools from Microsoft Learn");

        if (mcpTools.Any())
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   Available tools:");
            foreach (var t in mcpTools.Take(5))
            {
                Console.WriteLine($"   • {t.Name}");
            }
            if (mcpTools.Count > 5)
                Console.WriteLine($"   ... and {mcpTools.Count - 5} more");
            Console.ResetColor();
        }
        else
        {
            ConsoleUi.PrintError("No MCP tools were discovered. The agent will run without tools.");
        }

        availableTools = mcpTools;
    }

    private void InitializeAgent()
    {
        ConsoleUi.PrintInfo("🤖 Initializing AI Agent...");

        var endpoint = GetRequiredSetting("AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
        var deployment = GetRequiredSetting("AZURE_OPENAI_DEPLOYMENT_NAME", "AzureOpenAI:DeploymentName");
        logger?.LogInformation("Using Azure OpenAI deployment: {Deployment}", deployment);

        chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        baseSystemPrompt =
            "You are an expert agent in Microsoft technologies. " +
            "For any question about Azure/.NET/Windows/VS/Entra/M365, " +
            "you MUST first use the Microsoft Learn MCP Server tools " +
            "(search/fetch/code samples) and cite the official URL. " +
            "Be conversational, helpful, and provide practical examples when possible.";

        BuildOrRefreshAgent();
        ConsoleUi.PrintSuccess("✓ Agent initialized and ready!");
    }

    private void CreateThread()
    {
        ConsoleUi.PrintInfo("🧵 Creating AgentThread for conversation...");
        thread = agent!.GetNewThread();
        ConsoleUi.PrintSuccess("✓ AgentThread created successfully!");
        Console.WriteLine();
    }

    private async Task RunInteractiveChatLoop()
    {
        while (true)
        {
            if (exitRequested)
            {
                ConsoleUi.PrintInfo("Exiting...");
                break;
            }

            ConsoleUi.PrintPrompt();
            var userInput = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            if (userInput.StartsWith("/"))
            {
                if (!await HandleCommand(userInput))
                    break;
                continue;
            }

            conversationHistory.Add(new ChatMessage(ChatRole.User, userInput));

            ConsoleUi.PrintThinking();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                ConsoleUi.PrintToolsEnabled(availableTools?.Count ?? 0);
                var result = await RunWithRetryAsync(
                    () => agent!.RunAsync(userInput, thread!),
                    operationName: "Agent run",
                    maxAttempts: retryMaxAttempts,
                    baseDelayMs: retryBaseDelayMs,
                    cancellationToken: cancellationTokenSource!.Token,
                    emitConsoleTiming: false);

                stopwatch.Stop();

                conversationHistory.Add(new ChatMessage(ChatRole.Assistant, result.Text));

                ConsoleUi.PrintAgentResponse(result.Text);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n⏱️  Response time: {stopwatch.ElapsedMilliseconds}ms | Messages: {conversationHistory.Count}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                if (ex is RequestFailedException rfe && rfe.Status == 429)
                {
                    ConsoleUi.PrintError("❌ Rate limit reached. Please wait a moment and retry.");
                }
                else
                {
                    ConsoleUi.PrintError($"❌ Error: {ex.Message}");
                }
                logger?.LogError(ex, "Agent execution failed");
                conversationHistory.RemoveAt(conversationHistory.Count - 1);
            }
        }
    }

    private async Task<bool> HandleCommand(string command)
    {
        switch (command.ToLower())
        {
            case "/exit":
                ConsoleUi.PrintInfo("👋 Goodbye! Thanks for chatting.");
                return false;

            case "/clear":
            case "/new":
                conversationHistory.Clear();
                thread = agent!.GetNewThread();
                Console.Clear();
                ConsoleUi.PrintWelcomeBanner();
                ConsoleUi.PrintSuccess("✓ Started new conversation with fresh AgentThread");
                return true;

            case "/history":
                ConsoleUi.PrintHistory(conversationHistory);
                return true;

            case "/help":
                ConsoleUi.PrintHelp();
                return true;

            case "/save":
                await SaveConversation();
                return true;

            case var cmd when cmd.StartsWith("/set "):
                SetMemory(cmd);
                return true;

            case var cmd when cmd.StartsWith("/forget "):
                ForgetMemory(cmd);
                return true;

            case "/memory":
                ConsoleUi.PrintMemory(memoryStore!.GetAllMemoriesAsContext());
                return true;

            case "/profile":
                ConsoleUi.PrintUserProfile(memoryStore!);
                return true;

            default:
                ConsoleUi.PrintError($"Unknown command: {command}");
                ConsoleUi.PrintInfo("Type /help to see available commands");
                return true;
        }
    }

    private void SetMemory(string command)
    {
        var payload = command.Substring(4).Trim();
        var splitIndex = payload.IndexOf(' ');
        if (splitIndex <= 0 || splitIndex == payload.Length - 1)
        {
            ConsoleUi.PrintInfo("Usage: /set <key> <value>");
            return;
        }

        var key = payload.Substring(0, splitIndex).Trim();
        var value = payload.Substring(splitIndex + 1).Trim();

        memoryStore!.AddMemory(key, value);
        SaveMemoryStore();
        BuildOrRefreshAgent();
        ConsoleUi.PrintSuccess($"✓ Memory updated: {key}");
        ConsoleUi.PrintInfo("Run /new if you want a fresh conversation context.");
    }

    private void ForgetMemory(string command)
    {
        var key = command.Substring(8).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ConsoleUi.PrintInfo("Usage: /forget <key>");
            return;
        }

        if (memoryStore!.RemoveMemory(key))
        {
            SaveMemoryStore();
            BuildOrRefreshAgent();
            ConsoleUi.PrintSuccess($"✓ Memory removed: {key}");
            ConsoleUi.PrintInfo("Run /new if you want a fresh conversation context.");
        }
        else
        {
            ConsoleUi.PrintError($"Memory key not found: {key}");
        }
    }

    private async Task SaveConversation()
    {
        if (conversationHistory.Count == 0)
        {
            ConsoleUi.PrintInfo("No conversation to save yet.");
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var logsDir = "logs";
            Directory.CreateDirectory(logsDir);
            var filename = Path.Combine(logsDir, $"conversation_{timestamp}.txt");

            var content = new StringBuilder();
            content.AppendLine("=".PadRight(70, '='));
            content.AppendLine("Agent Framework Copilot - Conversation Log");
            content.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            content.AppendLine($"Messages: {conversationHistory.Count}");
            content.AppendLine("=".PadRight(70, '='));
            content.AppendLine();

            foreach (var msg in conversationHistory)
            {
                var role = msg.Role == ChatRole.User ? "USER" : "AGENT";
                content.AppendLine($"[{role}]");
                content.AppendLine(msg.Text);
                content.AppendLine();
                content.AppendLine("-".PadRight(70, '-'));
                content.AppendLine();
            }

            await File.WriteAllTextAsync(filename, content.ToString());
            ConsoleUi.PrintSuccess($"✓ Conversation saved to: {filename}");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"❌ Failed to save conversation: {ex.Message}");
        }
    }

    private void SaveMemoryStore()
    {
        if (string.IsNullOrWhiteSpace(memoryFilePath))
            return;

        try
        {
            memoryStore!.SaveToFile(memoryFilePath);
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"❌ Failed to save memory file: {ex.Message}");
        }
    }

    private async Task<T> RunWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        int maxAttempts = 3,
        int baseDelayMs = 500,
        CancellationToken cancellationToken = default,
        bool emitConsoleTiming = false)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await operation();
                sw.Stop();
                logger?.LogInformation("{Operation} completed in {Elapsed}ms", operationName, sw.ElapsedMilliseconds);
                if (emitConsoleTiming)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"⏱️  {operationName} took {sw.ElapsedMilliseconds}ms");
                    Console.ResetColor();
                }
                return result;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                sw.Stop();
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
                logger?.LogWarning(ex, "Transient error during {Operation} (attempt {Attempt}/{Max})", operationName, attempt, maxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
        }

        return await operation();
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is TimeoutException
            || (ex is RequestFailedException rfe && (rfe.Status == 429 || rfe.Status >= 500));
    }

    private void BuildOrRefreshAgent()
    {
        var memoryContext = memoryStore!.GetAllMemoriesAsContext();
        var systemPrompt = baseSystemPrompt;
        if (!string.IsNullOrWhiteSpace(memoryContext))
            systemPrompt += "\n\n" + memoryContext;

        agent = chatClient!.CreateAIAgent(
            instructions: systemPrompt,
            name: "DocsAgent",
            tools: availableTools ?? new List<AITool>()
        );
    }

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

    private string GetRequiredSetting(params string[] keys)
    {
        var value = GetOptionalSetting(keys);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required setting. Set one of: {string.Join(", ", keys)}");
    }

    private IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private ILogger BuildLogger(IConfiguration config)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        }).CreateLogger("AgentFrameworkCopilot");
    }
}
