using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace CallAutomation_AzureAI_Speech_Translation
{
    public class CallContext
    {

        //Caller
        public string? CallerCallConnectionId { get; set; }
        public string? CallerCorrelationId { get; set; }
        public WebSocket? CallerWebSocket { get; set; }
        public SpeechTranslator? CallerTranslator { get; set; }

        //Agent
        public string? AgentCallConnectionId { get; set; }
        public string? AgentCorrelationId { get; set; }
        public WebSocket? AgentWebSocket { get; set; }
        public SpeechTranslator? AgentTranslator { get; set; }
    }

    public class CallStore
    {
        private readonly ConcurrentDictionary<string, CallContext> _callContexts;

        public CallStore()
        {
            _callContexts = new ConcurrentDictionary<string, CallContext>();
        }

        /// <summary>
        /// Adds or updates a call context in the store.
        /// </summary>
        public void AddOrUpdateCallContext(CallContext callContext)
        {
            if (callContext == null ||
                (string.IsNullOrEmpty(callContext.AgentCallConnectionId) &&
                string.IsNullOrEmpty(callContext.CallerCallConnectionId)))
            {
                throw new ArgumentException("Call context and both CallConnectionIds cannot be null or empty.");
            }

            // Add entries for both Agent and Caller CallConnectionIds
            if(!string.IsNullOrEmpty(callContext.AgentCallConnectionId)) _callContexts[callContext.AgentCallConnectionId] = callContext;
            if (!string.IsNullOrEmpty(callContext.CallerCallConnectionId)) _callContexts[callContext.CallerCallConnectionId] = callContext;
        }

        /// <summary>
        /// Retrieves a call context by either Agent or Caller CallConnectionId.
        /// </summary>
        public CallContext GetCallContext(string callConnectionId)
        {
            if (string.IsNullOrEmpty(callConnectionId))
            {
                throw new ArgumentException("CallConnectionId cannot be null or empty.");
            }

            _callContexts.TryGetValue(callConnectionId, out var callContext);
            return callContext;
        }

        /// <summary>
        /// Removes a call context by Agent or Caller CallConnectionId.
        /// </summary>
        public bool RemoveCallContext(string callConnectionId)
        {
            if (string.IsNullOrEmpty(callConnectionId))
            {
                throw new ArgumentException("CallConnectionId cannot be null or empty.");
            }

            if (_callContexts.TryRemove(callConnectionId, out var callContext))
            {
                // Ensure the other CallConnectionId is also removed
                if (callContext.AgentCallConnectionId == callConnectionId)
                {
                    _callContexts.TryRemove(callContext.CallerCallConnectionId, out _);
                }
                else if (callContext.CallerCallConnectionId == callConnectionId)
                {
                    _callContexts.TryRemove(callContext.AgentCallConnectionId, out _);
                }
                return true;
            }
            return false;
        }
    }
}
