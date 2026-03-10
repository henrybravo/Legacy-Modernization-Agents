using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Core;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using AzureOpenAIOptions = Azure.AI.OpenAI.AzureOpenAIClientOptions;
using AzureServiceVersion = Azure.AI.OpenAI.AzureOpenAIClientOptions.ServiceVersion;

namespace CobolToQuarkusMigration.Agents.Infrastructure;

/// <summary>
/// Factory for creating IChatClient instances for Azure OpenAI or OpenAI.
/// </summary>
public static class ChatClientFactory
{
    private static readonly AzureServiceVersion AzureApiVersion = AzureServiceVersion.V2024_06_01;

    /// <summary>
    /// Creates an IChatClient for Azure OpenAI using API key authentication.
    /// </summary>
    public static IChatClient CreateAzureOpenAIChatClient(
        string endpoint,
        string apiKey,
        string modelId,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentNullException(nameof(endpoint));
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentNullException(nameof(apiKey));
        if (string.IsNullOrEmpty(modelId))
            throw new ArgumentNullException(nameof(modelId));

        logger?.LogInformation("Creating Azure OpenAI chat client for endpoint: {Endpoint}, model: {Model}",
            endpoint, modelId);

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            new System.ClientModel.ApiKeyCredential(apiKey),
            CreateOptions());

        return client.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>
    /// Creates an IChatClient for Azure OpenAI using a TokenCredential (e.g. DefaultAzureCredential).
    /// </summary>
    public static IChatClient CreateAzureOpenAIChatClient(
        string endpoint,
        TokenCredential credential,
        string modelId,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentNullException(nameof(endpoint));
        if (credential == null)
            throw new ArgumentNullException(nameof(credential));
        if (string.IsNullOrEmpty(modelId))
            throw new ArgumentNullException(nameof(modelId));

        logger?.LogInformation("Creating Azure OpenAI chat client with TokenCredential for endpoint: {Endpoint}, model: {Model}",
            endpoint, modelId);

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            credential,
            CreateOptions());

        return client.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>
    /// Creates an IChatClient for Azure OpenAI using DefaultAzureCredential.
    /// </summary>
    public static IChatClient CreateAzureOpenAIChatClientWithDefaultCredential(
        string endpoint,
        string modelId,
        ILogger? logger = null)
    {
        return CreateAzureOpenAIChatClient(endpoint, new DefaultAzureCredential(), modelId, logger);
    }

    /// <summary>
    /// Creates an IChatClient for OpenAI (not Azure).
    /// </summary>
    public static IChatClient CreateOpenAIChatClient(
        string apiKey,
        string modelId,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentNullException(nameof(apiKey));
        if (string.IsNullOrEmpty(modelId))
            throw new ArgumentNullException(nameof(modelId));

        logger?.LogInformation("Creating OpenAI chat client for model: {Model}", modelId);

        var client = new OpenAIClient(apiKey);

        return client.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>
    /// Creates an IChatClient for GitHub Copilot SDK.
    /// Requires the Copilot CLI in PATH.
    /// </summary>
    public static IChatClient CreateGitHubCopilotChatClient(
        string modelId,
        string? githubToken = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(modelId))
            throw new ArgumentNullException(nameof(modelId));

        logger?.LogInformation("Creating GitHub Copilot SDK chat client for model: {Model}", modelId);

        var options = new CopilotClientOptions
        {
            UseStdio = true
        };

        if (!string.IsNullOrEmpty(githubToken))
        {
            options.GitHubToken = githubToken;
        }

        // Don't pass the app logger to the SDK — it produces very verbose
        // internal JSON-RPC tracing that floods the console output.
        return new CopilotChatClient(modelId, options);
    }

    /// <summary>
    /// Creates an IChatClient by routing to Azure OpenAI, OpenAI, or GitHub Copilot based on serviceType.
    /// </summary>
    public static IChatClient CreateChatClient(
        string? endpoint,
        string apiKey,
        string modelId,
        bool useDefaultCredential = false,
        ILogger? logger = null,
        string? serviceType = null)
    {
        if (string.Equals(serviceType, "GitHubCopilot", StringComparison.OrdinalIgnoreCase))
        {
            return CreateGitHubCopilotChatClient(modelId, githubToken: null, logger);
        }

        if (!string.IsNullOrEmpty(endpoint))
        {
            if (useDefaultCredential)
            {
                return CreateAzureOpenAIChatClientWithDefaultCredential(endpoint, modelId, logger);
            }
            return CreateAzureOpenAIChatClient(endpoint, apiKey, modelId, logger);
        }

        return CreateOpenAIChatClient(apiKey, modelId, logger);
    }

    private static AzureOpenAIOptions CreateOptions() => new AzureOpenAIOptions(AzureApiVersion);
}
