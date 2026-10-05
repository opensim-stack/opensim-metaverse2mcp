namespace Opensim.Metaverse2Mcp;

using LibreMetaverse;

internal interface IUserResponseHandler : IDisposable
{
    /// <summary>
    /// Intercepts incoming user text before it is routed to the AI session.
    /// Implementations may consume star commands and pending prompt replies.
    /// Returns true when the message is fully handled and should not be AI-routed.
    /// </summary>
    Task<bool> TryHandleIncomingMessageAsync(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string text,
        bool whileRequestInFlight,
        CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the handler that a permission prompt has been resolved externally
    /// (for example via a star command) so local prompt state can be cleared/drained.
    /// </summary>
    void NotifyPermissionPromptResolved(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string permissionId);

    /// <summary>
    /// Notifies the handler that a question prompt has been resolved externally
    /// (for example via a star command) so local prompt state can be cleared/drained.
    /// </summary>
    void NotifyQuestionPromptResolved(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string questionId);
}
