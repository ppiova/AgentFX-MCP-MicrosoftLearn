using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

class Program
{
    static async Task Main()
    {
        // === 1) Connect to Microsoft Learn MCP Server (HTTP/Streamable HTTP) ===
        var learnEndpoint = new Uri("https://learn.microsoft.com/api/mcp");
        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "MicrosoftLearn",
            Endpoint = learnEndpoint
        });

        await using var mcp = await McpClient.CreateAsync(httpTransport);
        Console.WriteLine("Connected to Learn MCP.");

        // === 2) Discover MCP tools dynamically ===
        var mcpTools = (await mcp.ListToolsAsync()).Cast<AITool>().ToList();
        Console.WriteLine("Tools exposed by Learn MCP:");
        foreach (var t in mcpTools) Console.WriteLine($" - {t.Name}");

        // === 3) Create the Microsoft Agent Framework agent ===
        // (we use Azure OpenAI as the chat backend; you can change it to another IChatClient)
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                         ?? throw new InvalidOperationException("Missing AZURE_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
                         ?? throw new InvalidOperationException("Missing AZURE_OPENAI_DEPLOYMENT_NAME");

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
            "I need information on how to create an agent in Azure AI Foundry Agents. " +
            "Include the Learn reference and code examples if they exist.";

        var result = await agent.RunAsync(demoQuestion);
        Console.WriteLine("\n=== Agent Response ===\n");
        Console.WriteLine(result.Text);

        // Tip: if you want to see Traces/Observability, check the Agent Framework repo. 
        // (includes examples and guides for logging/telemetry and latest releases). 
    }
}
