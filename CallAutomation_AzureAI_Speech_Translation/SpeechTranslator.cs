using Azure.Communication.CallAutomation;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using System.Net.WebSockets;
using System.Text;

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
        
        public string FromLanguage;
        public string ToLanguage;
        public string VoiceName;

        public SpeechTranslator(
            string speechSubscriptionKey,
            string speechRegion) 
        {
            m_speechConfig = SpeechTranslationConfig.FromSubscription(speechSubscriptionKey, speechRegion);

            m_speechConfig.SpeechRecognitionLanguage = FromLanguage;
            m_speechConfig.AddTargetLanguage(ToLanguage);
            m_speechConfig.VoiceName = VoiceName;

            m_cts = new CancellationTokenSource();
            m_audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            m_speechRecognizer = new TranslationRecognizer(m_speechConfig, AudioConfig.FromStreamInput(m_audioInputStream));

            SubscribeToRecognizeEvents();
        }

        public void SubscribeToRecognizeEvents()
        {
            // Subscribes to events.
            m_speechRecognizer.Recognizing += (s, e) =>
            {
                Console.WriteLine($"RECOGNIZING in '{FromLanguage}': Text={e.Result.Text}");
                foreach (var element in e.Result.Translations)
                {
                    Console.WriteLine($"    TRANSLATING into '{element.Key}': {element.Value}");
                }
            };

            m_speechRecognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    Console.WriteLine($"\nFinal result: Reason: {e.Result.Reason.ToString()}, recognized text in {FromLanguage}: {e.Result.Text}.");
                    foreach (var element in e.Result.Translations)
                    {
                        Console.WriteLine($"    TRANSLATING into '{element.Key}': {element.Value}");
                    }
                }
            };

            m_speechRecognizer.Synthesizing += (s, e) =>
            {
                if(OutputWebSocket != null)
                {
                    var audio = e.Result.GetAudio();
                    if (audio.Length > 0)
                    {
                        Console.WriteLine($"AudioSize: {audio.Length}");

                        // Create a ServerAudioData object for this chunk
                        var audioData = OutStreamingData.GetAudioDataForOutbound(audio);
                        byte[] jsonBytes = Encoding.UTF8.GetBytes(audioData);
                        OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, false, new CancellationToken()).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Console.WriteLine($"AudioSize: {audio.Length} (end of synthesis data)");
                    }
                }
            };

            m_speechRecognizer.Canceled += (s, e) =>
            {
                Console.WriteLine($"\nRecognition canceled. Reason: {e.Reason}; ErrorDetails: {e.ErrorDetails}");
            };

            m_speechRecognizer.SessionStarted += (s, e) =>
            {
                Console.WriteLine("\nSession started event.");
            };

            m_speechRecognizer.SessionStopped += (s, e) =>
            {
                Console.WriteLine("\nSession stopped event.");
            };
        }

        private void WriteToSpeechConfigStream(string data)
        {
            var input = StreamingData.Parse(data);
            if (input is AudioData audioData)
            {
                if (!audioData.IsSilent)
                {
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

                    if (receiveResult.MessageType != WebSocketMessageType.Close)
                    {
                        string data = Encoding.UTF8.GetString(receiveBuffer).TrimEnd('\0');
                        WriteToSpeechConfigStream(data);
                        //Console.WriteLine("-----------: " + data);                
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception -> {ex}");
            }
            finally
            {
                await m_speechRecognizer.StopContinuousRecognitionAsync();
            }
        }

    }
}
