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

// Simple in-memory store for user context and memories
class MemoryStore
{
    private readonly Dictionary<string, string> memories = new();

    public void AddMemory(string key, string value)
    {
        memories[key] = value;
    }

    public string? GetMemory(string key)
    {
        return memories.TryGetValue(key, out var value) ? value : null;
    }

    public bool RemoveMemory(string key)
    {
        return memories.Remove(key);
    }

    public bool LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        var json = File.ReadAllText(filePath);
        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (data == null)
            return false;

        memories.Clear();
        foreach (var (key, value) in data)
        {
            memories[key] = value;
        }
        return true;
    }

    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = System.Text.Json.JsonSerializer.Serialize(memories, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }

    public string GetAllMemoriesAsContext()
    {
        if (memories.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("### User Context & Memories:");
        foreach (var (key, value) in memories)
        {
            sb.AppendLine($"- {key}: {value}");
        }
        return sb.ToString();
    }

    public void LoadDefaultUserProfile()
    {
        // Información personal de Pablo Piovano
        AddMemory("user_name", "Pablo Piovano");
        AddMemory("nickname", "Pablito Piova");
        AddMemory("title", "Microsoft MVP");
        AddMemory("interests", "Café, Cocinar Asados Argentinos, Viajar");
        AddMemory("location", "Sunchales, Santa Fe");
        AddMemory("country", "Argentina");
        AddMemory("friends", "Amigo de Bruno y Quique");
    }
}
class Program
{
    private static readonly List<ChatMessage> conversationHistory = new();
    private static AIAgent? agent;
    private static AgentThread? thread;
    private static MemoryStore? memoryStore;
    private static bool exitRequested = false;
    private static string? memoryFilePath;
    private static IChatClient? chatClient;
    private static List<AITool>? availableTools;
    private static string? baseSystemPrompt;
    private static IConfiguration? configuration;
    private static ILogger? logger;
    private static CancellationTokenSource? cancellationTokenSource;
    private static int retryMaxAttempts = 3;
    private static int retryBaseDelayMs = 500;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        cancellationTokenSource = new CancellationTokenSource();

        configuration = BuildConfiguration();
        logger = BuildLogger(configuration);

        retryMaxAttempts = GetOptionalSetting("Retry:MaxAttempts") is string attempts && int.TryParse(attempts, out var parsedAttempts)
            ? parsedAttempts
            : 3;
        retryBaseDelayMs = GetOptionalSetting("Retry:BaseDelayMs") is string delay && int.TryParse(delay, out var parsedDelay)
            ? parsedDelay
            : 500;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitRequested = true;
            cancellationTokenSource.Cancel();
            PrintInfo("\nCancellation requested. Finishing current response...");
        };
        
        PrintWelcomeBanner();

        // === 0) Initialize Memory Store ===
        PrintInfo("🧠 Initializing Memory Store...");
        memoryStore = new MemoryStore();
        memoryFilePath = GetOptionalSetting("MEMORY_FILE", "Memory:File") ?? "memory.json";
        logger?.LogInformation("Memory file path: {MemoryFile}", memoryFilePath);

        try
        {
            if (!memoryStore.LoadFromFile(memoryFilePath))
            {
                memoryStore.LoadDefaultUserProfile();
                memoryStore.SaveToFile(memoryFilePath);
                PrintSuccess("✓ Memory Store initialized with default profile");
            }
            else
            {
                PrintSuccess("✓ Memory Store loaded from file");
            }
        }
        catch (Exception ex)
        {
            memoryStore.LoadDefaultUserProfile();
            PrintError($"Failed to load memory file, using defaults. {ex.Message}");
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   Loaded profile: Pablo Piovano (Pablito Piova)");
        Console.ResetColor();

        // === 1) Connect to Microsoft Learn MCP Server ===
        PrintInfo("🔌 Connecting to Microsoft Learn MCP Server...");
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

        await using var mcp = await RunWithRetryAsync(
            () => McpClient.CreateAsync(httpTransport),
            operationName: "MCP connect",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationTokenSource.Token,
            emitConsoleTiming: true);
        PrintSuccess("✓ Connected to Learn MCP");

        // === 2) Discover MCP tools dynamically ===
        PrintInfo("🔧 Loading available tools...");
        var mcpTools = (await RunWithRetryAsync(
            () => mcp.ListToolsAsync(),
            operationName: "MCP list tools",
            maxAttempts: retryMaxAttempts,
            baseDelayMs: retryBaseDelayMs,
            cancellationToken: cancellationTokenSource.Token,
            emitConsoleTiming: true)).Cast<AITool>().ToList();
        PrintSuccess($"✓ Loaded {mcpTools.Count} tools from Microsoft Learn");
        
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
            PrintError("No MCP tools were discovered. The agent will run without tools.");
        }

        // === 3) Create the Microsoft Agent Framework agent ===
        PrintInfo("🤖 Initializing AI Agent...");
        var endpoint   = GetRequiredSetting("AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint");
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

        availableTools = mcpTools;
        BuildOrRefreshAgent();

        PrintSuccess("✓ Agent initialized and ready!");

        // === 4) Create AgentThread for maintaining conversation context ===
        PrintInfo("🧵 Creating AgentThread for conversation...");
        thread = agent!.GetNewThread();
        PrintSuccess("✓ AgentThread created successfully!");
        Console.WriteLine();

        // === 5) Interactive chat loop ===
        await RunInteractiveChatLoop();
    }

    static async Task RunInteractiveChatLoop()
    {
        while (true)
        {
            if (exitRequested)
            {
                PrintInfo("Exiting...");
                break;
            }
            // Show prompt
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\n💬 You: ");
            Console.ResetColor();

            var userInput = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            // Handle commands
            if (userInput.StartsWith("/"))
            {
                if (!await HandleCommand(userInput))
                    break; // Exit requested
                continue;
            }

            // Add user message to history
            conversationHistory.Add(new ChatMessage(ChatRole.User, userInput));

            // Show thinking indicator
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n🤔 Agent is thinking...");
            Console.ResetColor();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Run the agent with the AgentThread to maintain conversation context
                // The thread automatically tracks all messages and maintains state
                var result = await RunWithRetryAsync(
                    () => agent!.RunAsync(userInput, thread!),
                    operationName: "Agent run",
                    maxAttempts: retryMaxAttempts,
                    baseDelayMs: retryBaseDelayMs,
                    cancellationToken: cancellationTokenSource.Token,
                    emitConsoleTiming: false);

                stopwatch.Stop();

                // Add assistant response to history (for display purposes)
                conversationHistory.Add(new ChatMessage(ChatRole.Assistant, result.Text));

                // Display response
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n🤖 Agent:");
                Console.ResetColor();
                Console.WriteLine(result.Text);

                // Show metrics
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n⏱️  Response time: {stopwatch.ElapsedMilliseconds}ms | Messages: {conversationHistory.Count}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                if (ex is RequestFailedException rfe && rfe.Status == 429)
                {
                    PrintError("❌ Rate limit reached. Please wait a moment and retry.");
                }
                else
                {
                    PrintError($"❌ Error: {ex.Message}");
                }
                logger?.LogError(ex, "Agent execution failed");
                // Remove the failed user message
                conversationHistory.RemoveAt(conversationHistory.Count - 1);
            }
        }
    }

    static async Task<bool> HandleCommand(string command)
    {
        switch (command.ToLower())
        {
            case "/exit":
                PrintInfo("👋 Goodbye! Thanks for chatting.");
                return false;

            case "/clear":
            case "/new":
                conversationHistory.Clear();
                // Create a new thread for the fresh conversation
                thread = agent!.GetNewThread();
                Console.Clear();
                PrintWelcomeBanner();
                PrintSuccess("✓ Started new conversation with fresh AgentThread");
                return true;

            case "/history":
                ShowHistory();
                return true;

            case "/help":
                ShowHelp();
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
                ShowMemory();
                return true;

            case "/profile":
                ShowUserProfile();
                return true;

            default:
                PrintError($"Unknown command: {command}");
                PrintInfo("Type /help to see available commands");
                return true;
        }
    }

    static void ShowHistory()
    {
        if (conversationHistory.Count == 0)
        {
            PrintInfo("No conversation history yet. Start chatting!");
            return;
        }

        Console.WriteLine("\n📜 Conversation History:");
        Console.WriteLine(new string('─', 60));

        for (int i = 0; i < conversationHistory.Count; i++)
        {
            var msg = conversationHistory[i];
            var role = msg.Role == ChatRole.User ? "You" : "Agent";
            var color = msg.Role == ChatRole.User ? ConsoleColor.Cyan : ConsoleColor.Green;

            Console.ForegroundColor = color;
            Console.WriteLine($"\n[{i + 1}] {role}:");
            Console.ResetColor();
            
            var preview = msg.Text.Length > 200 
                ? msg.Text.Substring(0, 200) + "..." 
                : msg.Text;
            Console.WriteLine(preview);
        }

        Console.WriteLine(new string('─', 60));
    }

    static void ShowHelp()
    {
        Console.WriteLine("\n📖 Available Commands:");
        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  /help      - Show this help message");
        Console.WriteLine("  /clear     - Clear conversation and start fresh");
        Console.WriteLine("  /new       - Same as /clear");
        Console.WriteLine("  /history   - Show conversation history");
        Console.WriteLine("  /memory    - Show all stored memories");
        Console.WriteLine("  /profile   - Show user profile information");
        Console.WriteLine("  /set k v   - Set memory key to value");
        Console.WriteLine("  /forget k  - Remove memory by key");
        Console.WriteLine("  /save      - Save conversation to file");
        Console.WriteLine("  /exit      - Exit the application");
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
    }

    static void ShowMemory()
    {
        Console.WriteLine("\n🧠 Memory Store Contents:");
        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(memoryStore!.GetAllMemoriesAsContext());
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
    }

    static void SetMemory(string command)
    {
        var payload = command.Substring(4).Trim();
        var splitIndex = payload.IndexOf(' ');
        if (splitIndex <= 0 || splitIndex == payload.Length - 1)
        {
            PrintInfo("Usage: /set <key> <value>");
            return;
        }

        var key = payload.Substring(0, splitIndex).Trim();
        var value = payload.Substring(splitIndex + 1).Trim();

        memoryStore!.AddMemory(key, value);
        SaveMemoryStore();
        BuildOrRefreshAgent();
        PrintSuccess($"✓ Memory updated: {key}");
        PrintInfo("Run /new if you want a fresh conversation context.");
    }

    static void ForgetMemory(string command)
    {
        var key = command.Substring(8).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            PrintInfo("Usage: /forget <key>");
            return;
        }

        if (memoryStore!.RemoveMemory(key))
        {
            SaveMemoryStore();
            BuildOrRefreshAgent();
            PrintSuccess($"✓ Memory removed: {key}");
            PrintInfo("Run /new if you want a fresh conversation context.");
        }
        else
        {
            PrintError($"Memory key not found: {key}");
        }
    }

    static void ShowUserProfile()
    {
        Console.WriteLine("\n👤 User Profile:");
        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Magenta;
        
        var name = memoryStore!.GetMemory("user_name");
        var nickname = memoryStore.GetMemory("nickname");
        var title = memoryStore.GetMemory("title");
        var interests = memoryStore.GetMemory("interests");
        var location = memoryStore.GetMemory("location");
        var country = memoryStore.GetMemory("country");

        Console.WriteLine($"  Nombre:     {name}");
        Console.WriteLine($"  Apodo:      {nickname}");
        Console.WriteLine($"  Título:     {title}");
        Console.WriteLine($"  Intereses:  {interests}");
        Console.WriteLine($"  Ciudad:     {location}");
        Console.WriteLine($"  País:       {country}");
        
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
    }

    static async Task SaveConversation()
    {
        if (conversationHistory.Count == 0)
        {
            PrintInfo("No conversation to save yet.");
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
            content.AppendLine($"Agent Framework Copilot - Conversation Log");
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
            PrintSuccess($"✓ Conversation saved to: {filename}");
        }
        catch (Exception ex)
        {
            PrintError($"❌ Failed to save conversation: {ex.Message}");
        }
    }

    static void SaveMemoryStore()
    {
        if (string.IsNullOrWhiteSpace(memoryFilePath))
            return;

        try
        {
            memoryStore!.SaveToFile(memoryFilePath);
        }
        catch (Exception ex)
        {
            PrintError($"❌ Failed to save memory file: {ex.Message}");
        }
    }

    static async Task<T> RunWithRetryAsync<T>(
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
                    PrintError($"Rate limit during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                else if (ex is RequestFailedException rfe2)
                {
                    PrintError($"Service error {rfe2.Status} during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                else
                {
                    PrintError($"Transient error during {operationName}. Retrying in {delay.TotalMilliseconds}ms...");
                }
                logger?.LogWarning(ex, "Transient error during {Operation} (attempt {Attempt}/{Max})", operationName, attempt, maxAttempts);
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

    static void BuildOrRefreshAgent()
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

    static string? GetOptionalSetting(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration?[key]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    static string GetRequiredSetting(params string[] keys)
    {
        var value = GetOptionalSetting(keys);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"Missing required setting. Set one of: {string.Join(", ", keys)}");
    }

    static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    static ILogger BuildLogger(IConfiguration config)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        }).CreateLogger("AgentFrameworkCopilot");
    }

    static void PrintWelcomeBanner()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║                                                               ║
║     🤖 Agent Framework Copilot                                ║
║     Powered by Microsoft Learn MCP & Azure OpenAI            ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
        ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Ask me anything about Microsoft technologies!");
        Console.WriteLine("Type /help for commands or /exit to quit\n");
        Console.ResetColor();
    }

    static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
