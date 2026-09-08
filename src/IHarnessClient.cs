namespace Opensim.Metaverse2Mcp;

internal interface IHarnessClient
{
    Task<HarnessChatReply> SendMessageAsync(string conversationKey, string title, string message, HarnessSendOptions? options, CancellationToken cancellationToken);
    void ResetConversation(string conversationKey);
    void SetConversationSessionId(string conversationKey, string? sessionId);
    string? GetConversationSessionId(string conversationKey);
    Task<IReadOnlyList<HarnessProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessProviderSummary>> ListAvailableProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, IReadOnlyList<HarnessProviderAuthMethod>>> ListProviderAuthMethodsAsync(CancellationToken cancellationToken);
    Task SetProviderApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken);
    Task<HarnessOAuthStartResult> StartProviderOAuthAsync(string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs, CancellationToken cancellationToken);
    Task<HarnessOAuthCompleteResult> CompleteProviderOAuthAsync(string providerId, int methodIndex, string? code, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessModelSummary>> ListModelsAsync(string? providerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<HarnessSessionSummary> CreateSessionAsync(string? title, string? parentSessionId, string? configuredModelId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> GetSessionStatusAsync(CancellationToken cancellationToken);
    Task<string> GetSessionDetailsJsonAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessSessionSummary>> GetSessionChildrenAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessPendingPermission>> ListPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken);
    bool TryGetPendingPermissionsFromEvents(string sessionId, out IReadOnlyList<HarnessPendingPermission> pendingPermissions);
    Task<bool> RespondToPermissionAsync(string sessionId, string permissionId, string response, bool remember, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessPendingQuestion>> ListPendingQuestionsAsync(string sessionId, CancellationToken cancellationToken);
    bool TryGetPendingQuestionsFromEvents(string sessionId, out IReadOnlyList<HarnessPendingQuestion> pendingQuestions);
    Task<bool> ReplyToQuestionAsync(string sessionId, string questionId, IReadOnlyList<string> answers, CancellationToken cancellationToken);
    Task<bool> RejectQuestionAsync(string sessionId, string questionId, CancellationToken cancellationToken);
    Task<HarnessSessionSummary> UpdateSessionTitleAsync(string sessionId, string title, CancellationToken cancellationToken);
    Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<bool> SummarizeSessionAsync(string sessionId, string? providerId, string? modelId, CancellationToken cancellationToken);
    Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HarnessProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<HarnessProjectSummary?> GetCurrentProjectAsync(CancellationToken cancellationToken);
    event Action<HarnessSessionStatusEvent>? SessionStatusChanged;
    event Action<HarnessMessagePartUpdatedEvent>? MessagePartUpdated;
}

internal sealed record HarnessChatReply(
    string Text,
    bool IsConfirmationPrompt,
    IReadOnlyList<HarnessPendingPermission>? PendingPermissions = null,
    IReadOnlyList<HarnessPendingQuestion>? PendingQuestions = null,
    HarnessUsageSummary? Usage = null);

internal sealed record HarnessSendOptions(string? ModelId, string? ThinkingLevel, string? SystemPrompt);
internal sealed record HarnessProviderSummary(string Id, string Name, bool? Connected);
internal sealed record HarnessModelSummary(string Id, string Name, string? Provider);
internal sealed record HarnessSessionSummary(string Id, string Title, string? Status, string? ProjectId);
internal sealed record HarnessProjectSummary(string Id, string Name, string? Path, bool? Current);
internal sealed record HarnessPendingPermission(string Id, string SessionId, string Title, string? Description);
internal sealed record HarnessPendingQuestion(string Id, string SessionId, string Header, string Question, IReadOnlyList<string> Options, bool? AllowsMultiple, bool? AllowsCustom);
internal sealed record HarnessProviderAuthMethod(int MethodIndex, string Type, string Label);
internal sealed record HarnessOAuthStartResult(string Url, string? Method, string? Instructions);
internal sealed record HarnessOAuthCompleteResult(bool CallbackAccepted, bool ProviderConfigured, string Message);
internal sealed record HarnessSessionStatusEvent(
    string SessionId,
    string StatusType,
    string? StatusMessage = null,
    DateTimeOffset? NextRetryAt = null,
    int? Attempt = null);
internal sealed record HarnessMessagePartUpdatedEvent(string SessionId, string PartType);
