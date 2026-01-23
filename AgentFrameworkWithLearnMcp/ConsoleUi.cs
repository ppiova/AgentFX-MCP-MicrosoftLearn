using System;
using System.Collections.Generic;

static class ConsoleUi
{
    // Print an informational message
    public static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    // Print a success message
    public static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    // Print an error message
    public static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    // Print a short summary of discovered tool names
    public static void PrintToolSummary(IReadOnlyList<string> toolNames)
    {
        PrintSuccess($"✓ Loaded {toolNames.Count} tools from Microsoft Learn");
        if (toolNames.Count == 0)
        {
            PrintError("No MCP tools were discovered. The agent will run without tools.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   Available tools:");
        foreach (var name in toolNames.Count > 5 ? toolNames[..5] : toolNames)
        {
            Console.WriteLine($"   • {name}");
        }
        if (toolNames.Count > 5)
            Console.WriteLine($"   ... and {toolNames.Count - 5} more");
        Console.ResetColor();
    }
}
