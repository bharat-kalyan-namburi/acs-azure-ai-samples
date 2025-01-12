using Azure.Communication.CallAutomation;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using System.Net.WebSockets;
using System.Text;
using System.Diagnostics;

namespace CallAutomation_AzureAI_Speech_Translation
{
    public class SpeechTranslator
    {
        private TranslationRecognizer recognizer;
        public WebSocket InputWebSocket { get; set; }
        public WebSocket OutputWebSocket { get; set; }
        private readonly PushAudioInputStream m_audioInputStream;
        private readonly TranslationRecognizer m_speechRecognizer;
        private readonly SpeechTranslationConfig m_speechConfig;
        private CancellationTokenSource m_cts;
        //private ILogger<Program> m_logger;
        
        public string FromLanguage;
        public string ToLanguage;
        public string VoiceName;

        private void DebugOut(string logtext)
        {
            Debug.WriteLine(logtext);
            //m_logger.LogInformation(logtext);
        }

        private void stripRiffHeader(ref byte[] audioData)
        {
            // Strip the RIFF header from the audio data
            // RIFF header is 44 bytes long in this case
            byte[] newAudioData = new byte[audioData.Length - 44];
            Array.Copy(audioData, 44, newAudioData, 0, newAudioData.Length);
            audioData = newAudioData;
        }


        public SpeechTranslator(
            string speechSubscriptionKey,
            string speechRegion,
            WebSocket inboundWebSocket,
            WebSocket outboundWebSocket,
            string inboundLanguage,
            string outboundLanguage,
            string outboundVoice,
            bool logSDK)
        {
            FromLanguage = inboundLanguage;
            ToLanguage = outboundLanguage;
            VoiceName = outboundVoice;
            InputWebSocket = inboundWebSocket;
            OutputWebSocket = outboundWebSocket;
            // Use the v2 endpoint to get the new translation features
            string v2Endpoint = $"wss://{speechRegion}.stt.speech.microsoft.com/speech/universal/v2";
            var v2EndpointUrl = new Uri(v2Endpoint);
            m_speechConfig = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, speechSubscriptionKey);
            //m_speechConfig = SpeechTranslationConfig.FromSubscription(speechSubscriptionKey, speechRegion);

            if (logSDK)
            { 
                // Generate log filename with date and timestamp
                string logFilename = $"E:\\Logs\\SDK-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
                // Enable Speech SDK logs for debugging purposes
                m_speechConfig.SetProperty(PropertyId.Speech_LogFilename, logFilename);
            }

            // Enable server side audio logs for this translation session
            m_speechConfig.EnableAudioLogging();

            //m_speechConfig.SpeechRecognitionLanguage = FromLanguage;
            m_speechConfig.AddTargetLanguage(ToLanguage);
            m_speechConfig.VoiceName = VoiceName;
            //This doesn't seem to work for speech translation
            //m_speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm);

            //Enable semantic segmentation for shorter translation turns
            m_speechConfig.SetProperty(PropertyId.Speech_SegmentationStrategy, "Semantic");

            m_cts = new CancellationTokenSource();
            m_audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            if (FromLanguage.ToLower() == "any") {
                // This enables multi-lingual translation with auto detection of the source language among a large set of input languages.
                var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromOpenRange();
                m_speechRecognizer = new TranslationRecognizer(m_speechConfig, autoDetectSourceLanguageConfig, AudioConfig.FromStreamInput(m_audioInputStream));
            }
            else
            {
                m_speechConfig.SpeechRecognitionLanguage = FromLanguage;
                m_speechRecognizer = new TranslationRecognizer(m_speechConfig, AudioConfig.FromStreamInput(m_audioInputStream));
            }

            SubscribeToRecognizeEvents();
        }

        public void SubscribeToRecognizeEvents()
        {
            // Subscribes to events.
            m_speechRecognizer.Recognizing += (s, e) =>
            {
                DebugOut($"RECOGNIZING in '{FromLanguage}': Text={e.Result.Text}");
                foreach (var element in e.Result.Translations)
                {
                    DebugOut($"TRANSLATING into '{element.Key}': {element.Value}");
                }
            };

            m_speechRecognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    DebugOut($"\nFinal result: Reason: {e.Result.Reason.ToString()}, recognized text in {FromLanguage}: {e.Result.Text}.");
                    foreach (var element in e.Result.Translations)
                    {
                        DebugOut($"    TRANSLATING into '{element.Key}': {element.Value}");
                    }
                }
            };

            m_speechRecognizer.Synthesizing += async (s, e) =>
            {
                if (OutputWebSocket != null)
                {
                    var audio = e.Result.GetAudio();
                    if (audio.Length > 0)
                    {
                        DebugOut($"TTS out AudioSize: {audio.Length}");
                        // strip the RIFF header from the audio data
                        stripRiffHeader(ref audio);
                        // Log audio to binary file
                        // open binary file for appending
                        //using (var fileStream = new FileStream("E:\\Logs\\audio.raw", FileMode.Append))
                        //{
                        //    fileStream.Write(audio, 0, audio.Length);
                        //}
                        
                        // Create a ServerAudioData object for this chunk
                        var audioData = OutStreamingData.GetAudioDataForOutbound(audio);

                        byte[] jsonBytes = Encoding.UTF8.GetBytes(audioData);
                        //OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, false, new CancellationToken()).GetAwaiter().GetResult();
                        await OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    }
                    else
                    {
                        DebugOut($"TTS out AudioSize: {audio.Length} (end of synthesis data)");
                    }
                }
            };

            m_speechRecognizer.Canceled += (s, e) =>
            {
                DebugOut($"\nRecognition canceled. Reason: {e.Reason}; ErrorDetails: {e.ErrorDetails}");
            };

            m_speechRecognizer.SessionStarted += (s, e) =>
            {
                DebugOut("\nSession started event.");
            };

            m_speechRecognizer.SessionStopped += (s, e) =>
            {
                DebugOut("\nSession stopped event.");
            };
        }

        private void WriteToSpeechConfigStream(string data)
        {
            var input = StreamingData.Parse(data);
            if (input is AudioData audioData)
            {
                if (!audioData.IsSilent)
                {
                    //DebugOut($"Pushing non-silence audio data: {audioData.Data.Length}");
                    m_audioInputStream.Write(audioData.Data);
                }
            }
        }

        // Method to receive messages from WebSocket
        public async Task ProcessWebSocketAsync()
        {
            if (InputWebSocket == null)
            {
                return;
            }
            try
            {
                await m_speechRecognizer.StartContinuousRecognitionAsync();
                while (InputWebSocket.State == WebSocketState.Open || InputWebSocket.State == WebSocketState.Closed)
                {
                    byte[] receiveBuffer = new byte[2048];
                    WebSocketReceiveResult receiveResult = await InputWebSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), m_cts.Token);
                    //DebugOut($"Received message from WebSocket. MessageType: {receiveResult.MessageType}, EndOfMessage: {receiveResult.EndOfMessage}, CloseStatus: {InputWebSocket.CloseStatus}");

                    if (receiveResult.MessageType != WebSocketMessageType.Close)
                    {
                        string data = Encoding.UTF8.GetString(receiveBuffer).TrimEnd('\0');
                        WriteToSpeechConfigStream(data);
                        //DebugOut($"Received data: {receiveBuffer.Length}");
                        //Console.WriteLine("-----------: " + data);                
                    }
                }
            }
            catch (Exception ex)
            {
                DebugOut($"Exception -> {ex}");
            }
            finally
            {
                await m_speechRecognizer.StopContinuousRecognitionAsync();
            }
        }

    }
}
