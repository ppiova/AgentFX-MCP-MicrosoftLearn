using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

static class ConsoleUi
{
    public static void PrintWelcomeBanner()
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

    public static void PrintPrompt()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("\n💬 You: ");
        Console.ResetColor();
    }

    public static void PrintThinking()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n🤔 Agent is thinking...");
        Console.ResetColor();
    }

    public static void PrintToolsEnabled(int count)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"🔧 Tools enabled: {count}");
        Console.ResetColor();
    }

    public static void PrintAgentResponse(string text)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n🤖 Agent:");
        Console.ResetColor();
        Console.WriteLine(text);
    }

    public static void PrintHistory(IReadOnlyList<ChatMessage> conversationHistory)
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

    public static void PrintHelp()
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

    public static void PrintMemory(string memoryContext)
    {
        Console.WriteLine("\n🧠 Memory Store Contents:");
        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(memoryContext);
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
    }

    public static void PrintUserProfile(MemoryStore memoryStore)
    {
        Console.WriteLine("\n👤 User Profile:");
        Console.WriteLine(new string('─', 60));
        Console.ForegroundColor = ConsoleColor.Magenta;

        var name = memoryStore.GetMemory("user_name");
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

    public static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
