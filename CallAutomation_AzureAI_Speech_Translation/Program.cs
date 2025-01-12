using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using CallAutomation_AzureAI_Speech_Translation;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;
using System.Text;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);


//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

var acsPhoneNumber = builder.Configuration.GetValue<string>("AcsPhoneNumber");
ArgumentNullException.ThrowIfNullOrEmpty(acsPhoneNumber);

var speechSubscriptionKey = builder.Configuration.GetValue<string>("AzureAISpeechKey");
ArgumentNullException.ThrowIfNullOrEmpty(speechSubscriptionKey);

var speechRegion = builder.Configuration.GetValue<string>("AzureAISpeechRegion");
ArgumentNullException.ThrowIfNullOrEmpty(speechRegion);

//Call Automation Client
var client = new CallAutomationClient(acsConnectionString);

//Call Store
var callStore = new Dictionary<string, CallContext>();

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);

var app = builder.Build();


var devTunnelUri = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);

var callerTransportUrl = devTunnelUri.Replace("https", "wss") + "/ws";
var agentTransportUrl = devTunnelUri.Replace("https", "wss") + "/ws2";

WebSocket callerWebsocket = null;
WebSocket agentWebSocket = null;
string callerCallConnectionId = null;
string agentCallConnectionId = null;
string callerCorrelationId = null;
string agentCorrelationId = null;
bool noAgentCall = false;


app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        logger.LogInformation($"Incoming Call event received.");

        // Handle system events
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        Console.WriteLine($"Callback Url: {callbackUri}");

        var mediaStreamingOptions = new MediaStreamingOptions(
                new Uri(callerTransportUrl),
                MediaStreamingContent.Audio,
                MediaStreamingAudioChannel.Mixed,
                startMediaStreaming: false)
        {
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm16KMono
        };

        var callIntelligenceOptions = new CallIntelligenceOptions()
        {
            CognitiveServicesEndpoint = new Uri("https://communication-services-aiservices.cognitiveservices.azure.com/")
        };

        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = callIntelligenceOptions,
            MediaStreamingOptions = mediaStreamingOptions,
            OperationContext = "firstCallConnected"
        };

        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        Console.WriteLine($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");

        //Use EventProcessor to process CallConnected event
        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        if (answer_result.IsSuccess)
        {
            Console.WriteLine($"Call connected event received for CorrelationId id: {answer_result.SuccessResult.CorrelationId}");
            callerCorrelationId = answer_result.SuccessResult.CorrelationId;
            callerCallConnectionId = answer_result.SuccessResult.CallConnectionId;
        }
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{

    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(cloudEvent)}");

        var callConnection = client.GetCallConnection(@event.CallConnectionId);
        var callMedia = callConnection?.GetCallMedia();

        if (@event is CallConnected callConnected)
        {
            logger.LogInformation($"Received Call Connected Event: for connection id: {@event.CallConnectionId}, correlationId: {@event.CorrelationId}, operationContext: {@event.OperationContext}");
            callerCallConnectionId = @event.CallConnectionId;

            if ("firstCallConnected".Equals(callConnected.OperationContext))
            {
                logger.LogInformation($"First Call Connected. Starting recognize to get phone number");

                var recognizeOptions = new CallMediaRecognizeDtmfOptions(new PhoneNumberIdentifier("+14258298235"), 11)
                {
                    Prompt = new TextSource("Hello welcome to multi language calling sample. Please enter your 10 digit US phone number followed by Pound sign.", "en-US", VoiceKind.Male),
                    StopTones = new List<DtmfTone> { DtmfTone.Pound },
                    InterruptPrompt = true
                };

                await callMedia.StartRecognizingAsync(recognizeOptions);
            }
        }

        if (@event is RecognizeCompleted recognizeCompleted)
        {
            logger.LogInformation($"[CALLER CALL] Received Recognize Completed Event: for connection id: {@event.CallConnectionId}, correlationId: {@event.CorrelationId}, operationContext: {@event.OperationContext}");
            var agentPhoneNumber = "";
            switch (recognizeCompleted.RecognizeResult)
            {
                case DtmfResult dtmfResult:
                    agentPhoneNumber = dtmfResult.ConvertToString();
                    logger.LogInformation($"[CALLER CALL] Recognize completed succesfully, agentPhoneNumber={agentPhoneNumber}");
                    break;

            }

            if(agentPhoneNumber.Length != 10 && agentPhoneNumber.Length != 0)
            {
                logger.LogInformation($"[CALLER CALL] Invalid phone number entered by caller. Disconnecting the call.");
                await callConnection.HangUpAsync(forEveryone: true);
                return Results.Ok();
            }

            if (agentPhoneNumber.Length != 0)
            {
                noAgentCall = false;
                PhoneNumberIdentifier caller = new PhoneNumberIdentifier(acsPhoneNumber);
                PhoneNumberIdentifier target = new PhoneNumberIdentifier($"+1{agentPhoneNumber}");
                CallInvite callInvite = new CallInvite(target, caller);
                var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/agentCall/{Guid.NewGuid()}?callerId={callerId}");

                var createCallOptions = new CreateCallOptions(callInvite, callbackUri)
                {
                    MediaStreamingOptions = new MediaStreamingOptions(
                                                new Uri(agentTransportUrl),
                                                MediaStreamingContent.Audio,
                                                MediaStreamingAudioChannel.Mixed,
                                                MediaStreamingTransport.Websocket,
                                                true)
                    {
                        EnableBidirectional = true,
                        AudioFormat = AudioFormat.Pcm16KMono
                    }
                };

                CreateCallResult createCallResult = await client.CreateCallAsync(createCallOptions);
                logger.LogInformation($"[AGENT CALL] Created call with connection id: {createCallResult.CallConnectionProperties.CallConnectionId}");
                agentCallConnectionId = createCallResult.CallConnectionProperties.CallConnectionId;
                agentCorrelationId = createCallResult.CallConnectionProperties.CorrelationId;
  
            }
            else
            {
                noAgentCall = true;
                if (callMedia != null)
                {
                    //Start media streaming on on the caller side
                    StartMediaStreamingOptions options = new StartMediaStreamingOptions()
                    {
                        OperationContext = "startMediaStreamingContext"
                    };

                    await callMedia.StartMediaStreamingAsync();
                }
            }
        }

        if (@event is MediaStreamingStarted)
        {
            logger.LogInformation($"[CALLER CALL] Received Media Streaming Started Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");
        }

        if (@event is MediaStreamingFailed)
        {
            logger.LogInformation(
                    $"[CALLER CALL] Received media streaming event: {@event.GetType()}, " +
                    $"SubCode: {@event?.ResultInformation?.SubCode}, " +
                    $"Message: {@event?.ResultInformation?.Message}");
        }

        if (@event is MediaStreamingStopped)
        {
            logger.LogInformation($"[CALLER CALL] Received Media Streaming Stopped Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");
        }

        if (@event is CallDisconnected callDisconnected)
        {
            logger.LogInformation($"[CALLER CALL] Received Call Disconnected Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");

            callerCallConnectionId = null;
            if (!string.IsNullOrEmpty(agentCallConnectionId))
            {
                var agentCall = client.GetCallConnection(agentCallConnectionId);
                await agentCall.HangUpAsync(forEveryone: true);
                agentCorrelationId = null;
            }
        }
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/agentCall/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{

    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"[AGENT CALL] Event received: {JsonConvert.SerializeObject(cloudEvent)}");

        var callConnection = client.GetCallConnection(@event.CallConnectionId);
        var callMedia = callConnection?.GetCallMedia();

        if (@event is CallConnected callConnected)
        {
            logger.LogInformation($"[AGENT CALL] Received Call Connected Event: for connection id: {@event.CallConnectionId}, correlationId: {@event.CorrelationId}, operationContext: {@event.OperationContext}");

            var callerCall = client.GetCallConnection(callerCallConnectionId);
            await callerCall.GetCallMedia().StartMediaStreamingAsync();
        }

        if (@event is MediaStreamingStarted)
        {
            logger.LogInformation($"[AGENT CALL] Received Media Streaming Started Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");
        }

        if (@event is MediaStreamingFailed)
        {
            logger.LogInformation(
                    $"[AGENT CALL]  Received media streaming event: {@event.GetType()}, " +
                    $"SubCode: {@event?.ResultInformation?.SubCode}, " +
                    $"Message: {@event?.ResultInformation?.Message}");
        }

        if (@event is MediaStreamingStopped)
        {
            logger.LogInformation($"[AGENT CALL] Received Media Streaming Stopped Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");
        }

        if (@event is CallDisconnected callDisconnected)
        {
            logger.LogInformation($"[AGENT CALL] Received Call Disconnected Event: for connection id: {@event.CallConnectionId} and correlationId: {@event.CorrelationId}");

            if (!string.IsNullOrEmpty(callerCallConnectionId))
            {
                var callerCall = client.GetCallConnection(callerCallConnectionId);
                await callerCall.HangUpAsync(forEveryone: true);
                callerCallConnectionId = null;
            }
        }
    }
    return Results.Ok();
});

// setup web socket for stream in
app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            callerWebsocket = await context.WebSockets.AcceptWebSocketAsync();

            if (noAgentCall == false)
            { 
                Debug.WriteLine("Waiting for agentWebSocket ...");
                while (agentWebSocket == null)
                {
                    await Task.Delay(500);
                }
                Debug.WriteLine("agentWebSocket connected ...");
                // send translation from caller to agent
                var callerTranslator = new SpeechTranslator(speechSubscriptionKey, speechRegion, callerWebsocket, agentWebSocket, "any", "en", "en-US-AvaMultilingualNeural", true);
                await callerTranslator.ProcessWebSocketAsync();
            }
            else
            {
                Debug.WriteLine("No agent call, starting media streaming ...");
                // echo caller translation back on the caller side. 
                var callerTranslator = new SpeechTranslator(speechSubscriptionKey, speechRegion, callerWebsocket, callerWebsocket, "any", "en", "en-US-AvaMultilingualNeural", false);
                await callerTranslator.ProcessWebSocketAsync();
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else if (context.Request.Path == "/ws2")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            agentWebSocket = await context.WebSockets.AcceptWebSocketAsync();

            Debug.WriteLine("Waiting for callerWebSocket ...");
            while (callerWebsocket == null)
            {
                await Task.Delay(500);
            }
            Debug.WriteLine("callerWebSocket connected ...");

            //send translation from agent to caller. Only turn on SDK logging once on the caller side.
            var agentTranslator = new SpeechTranslator(speechSubscriptionKey, speechRegion, agentWebSocket, callerWebsocket, "any", "de", "en-US-AvaMultilingualNeural", false);
            await agentTranslator.ProcessWebSocketAsync();
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

app.Run();
