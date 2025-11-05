using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    private static int messageCount = 0;
    private static AIAgent? agent;
    private static AgentThread? thread;
    private static MemoryStore? memoryStore;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        
        PrintWelcomeBanner();

        // === 0) Initialize Memory Store ===
        PrintInfo("🧠 Initializing Memory Store...");
        memoryStore = new MemoryStore();
        memoryStore.LoadDefaultUserProfile();
        PrintSuccess("✓ Memory Store loaded with user profile");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   Loaded profile: Pablo Piovano (Pablito Piova)");
        Console.ResetColor();

        // === 1) Connect to Microsoft Learn MCP Server ===
        PrintInfo("🔌 Connecting to Microsoft Learn MCP Server...");
        var learnEndpoint = new Uri("https://learn.microsoft.com/api/mcp");
        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "MicrosoftLearn",
            Endpoint = learnEndpoint
        });

        await using var mcp = await McpClient.CreateAsync(httpTransport);
        PrintSuccess("✓ Connected to Learn MCP");

        // === 2) Discover MCP tools dynamically ===
        PrintInfo("🔧 Loading available tools...");
        var mcpTools = (await mcp.ListToolsAsync()).Cast<AITool>().ToList();
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

        // === 3) Create the Microsoft Agent Framework agent ===
        PrintInfo("🤖 Initializing AI Agent...");
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                         ?? throw new InvalidOperationException("Missing AZURE_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
                         ?? throw new InvalidOperationException("Missing AZURE_OPENAI_DEPLOYMENT_NAME");

        IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        // Build system prompt with memory context
        var memoryContext = memoryStore.GetAllMemoriesAsContext();
        var systemPrompt =
            "You are an expert agent in Microsoft technologies. " +
            "For any question about Azure/.NET/Windows/VS/Entra/M365, " +
            "you MUST first use the Microsoft Learn MCP Server tools " +
            "(search/fetch/code samples) and cite the official URL. " +
            "Be conversational, helpful, and provide practical examples when possible.\n\n" +
            memoryContext;

        agent = chatClient.CreateAIAgent(
            instructions: systemPrompt,
            name: "DocsAgent",
            tools: mcpTools
        );

        PrintSuccess("✓ Agent initialized and ready!");

        // === 4) Create AgentThread for maintaining conversation context ===
        PrintInfo("🧵 Creating AgentThread for conversation...");
        thread = agent.GetNewThread();
        PrintSuccess("✓ AgentThread created successfully!");
        Console.WriteLine();

        // === 5) Interactive chat loop ===
        await RunInteractiveChatLoop();
    }

    static async Task RunInteractiveChatLoop()
    {
        while (true)
        {
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
            messageCount++;

            // Show thinking indicator
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("\n🤔 Agent is thinking...");
            Console.ResetColor();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Run the agent with the AgentThread to maintain conversation context
                // The thread automatically tracks all messages and maintains state
                var result = await agent!.RunAsync(userInput, thread!);

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
                PrintError($"❌ Error: {ex.Message}");
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
                messageCount = 0;
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
            var filename = $"conversation_{timestamp}.txt";
            
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
