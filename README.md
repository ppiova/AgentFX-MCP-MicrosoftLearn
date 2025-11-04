# Agent Framework with Microsoft Learn MCP

This project demonstrates the integration of the **Microsoft Agent Framework** with the **Model Context Protocol (MCP)** from Microsoft Learn.

## 🚀 Features

- Connection to Microsoft Learn MCP server
- Dynamic discovery of tools (search, fetch, code samples)
- AI Agent with Azure OpenAI that uses official Microsoft documentation

## 📋 Prerequisites

- .NET 9.0
- Azure OpenAI Service with a deployment
- Azure CLI installed and authenticated (`az login`)

## ⚙️ Configuration

### Option 1: Using launchSettings.json (Recommended for development)

1. Edit the file `AgentFrameworkWithLearnMcp/Properties/launchSettings.json`
2. Replace the values with your configuration:
   ```json
   {
     "profiles": {
       "AgentFrameworkWithLearnMcp": {
         "commandName": "Project",
         "environmentVariables": {
           "AZURE_OPENAI_ENDPOINT": "https://TU-RECURSO.openai.azure.com/",
           "AZURE_OPENAI_DEPLOYMENT_NAME": "gpt-4"
         }
       }
     }
   }
   ```

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
```

Or from Visual Studio/VS Code: press F5

## 📝 How to get Azure OpenAI credentials

1. Go to the [Azure Portal](https://portal.azure.com)
2. Search for your Azure OpenAI resource
3. Copy the **Endpoint** (under "Keys and Endpoint")
4. Go to "Model deployments" and copy your deployment name

## 🔐 Authentication

The project uses `AzureCliCredential`, so make sure to:
1. Have Azure CLI installed
2. Run `az login`
3. Have permissions on the Azure OpenAI resource

## 📦 Packages Used

- `Azure.AI.OpenAI` (2.5.0-beta.1)
- `Azure.Identity` (1.17.0)
- `Microsoft.Agents.AI.OpenAI` (1.0.0-preview.251028.1)
- `ModelContextProtocol` (0.4.0-preview.3)

## 🛠️ Available MCP Tools

The Microsoft Learn MCP server exposes:
- `microsoft_docs_search` - Search documentation
- `microsoft_code_sample_search` - Search code examples
- `microsoft_docs_fetch` - Fetch specific documentation
