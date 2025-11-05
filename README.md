# Agent Framework with Microsoft Learn MCP

Two .NET console applications demonstrating the integration of **Microsoft Agent Framework** with **Model Context Protocol (MCP)** to access Microsoft Learn documentation.

## � Projects

### 1. AgentFrameworkWithLearnMcp
Simple demo with a single question execution. Perfect for learning the basics.

### 2. AgentFrameworkCopilot ⭐
Interactive ChatGPT-style experience with conversation history, commands, and enhanced UI. Uses **AgentThread** to maintain conversation context and state across multiple interactions. **Recommended for interactive use.**

## ✨ Key Features

- 🔌 Connection to Microsoft Learn MCP Server
- 🔍 Dynamic tool discovery (search, fetch, code samples)
- 🤖 AI Agent powered by Azure OpenAI
- 🧵 AgentThread for maintaining conversation context and state
- 💬 Interactive chat loop with conversation history
- 📊 Real-time metrics and colored UI
- 💾 Save conversations to file

## 📋 Prerequisites

- .NET 9.0 SDK
- Azure OpenAI Service with a deployment
- Azure CLI (`az login`)

## ⚙️ Quick Setup

1. **Authenticate with Azure:**
```bash
az login
```

2. **Configure your Azure OpenAI settings:**

Edit `launchSettings.json` in each project's `Properties` folder:

```json
{
  "profiles": {
    "AgentFrameworkCopilot": {
      "commandName": "Project",
      "environmentVariables": {
        "AZURE_OPENAI_ENDPOINT": "https://your-resource.openai.azure.com/",
        "AZURE_OPENAI_DEPLOYMENT_NAME": "gpt-5-mini"
      }
    }
  }
}
```

3. **Run the project:**

```bash
# Simple demo
### Option 2: Environment variables in PowerShell

```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_DEPLOYMENT_NAME = "gpt-5-mini"
```

### Option 3: Permanent environment variables

```powershell
[System.Environment]::SetEnvironmentVariable('AZURE_OPENAI_ENDPOINT', 'https://your-resource.openai.azure.com/', 'User')
[System.Environment]::SetEnvironmentVariable('AZURE_OPENAI_DEPLOYMENT_NAME', 'gpt-5-mini', 'User')
```

## 🏃 Run

```powershell
cd AgentFrameworkWithLearnMcp
dotnet run

# Interactive chat (recommended)
cd AgentFrameworkCopilot
dotnet run
```

## 💬 Interactive Commands

Once running `AgentFrameworkCopilot`:

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/clear` | Start new conversation |
| `/history` | View conversation history |
| `/save` | Save conversation to file |
| `/exit` | Quit application |

## 📦 Dependencies

- `Azure.AI.OpenAI` (2.5.0-beta.1)
- `Azure.Identity` (1.17.0)
- `Microsoft.Agents.AI.OpenAI` (1.0.0-preview.251028.1)
- `ModelContextProtocol` (0.4.0-preview.3)

## 🔄 How It Works

1. Connects to Microsoft Learn MCP Server (https://learn.microsoft.com/api/mcp)
2. Discovers available tools (docs search, code samples, etc.)
3. Creates an AI Agent with Azure OpenAI (`gpt-5-mini`)
4. Initializes an **AgentThread** to maintain conversation state
5. Agent automatically uses MCP tools to fetch official documentation
6. AgentThread tracks all messages and context across the conversation
7. Returns answers with official Microsoft Learn references

### 🧵 About AgentThreads

**AgentThread** is a key component of the Microsoft Agent Framework that:
- Maintains conversation context across multiple user interactions
- Automatically tracks all messages (user queries and agent responses)
- Preserves state between agent runs
- Enables multi-turn conversations with memory
- Can be reset with `/clear` command to start fresh conversations

## 🔐 Authentication

Uses `AzureCliCredential` for Azure authentication. Ensure you have:
- Azure CLI installed
- Authenticated via `az login`
- Proper permissions on the Azure OpenAI resource

---

**Happy coding!** 🎉
