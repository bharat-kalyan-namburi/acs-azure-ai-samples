using Azure.Communication.CallAutomation;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using System.Net.WebSockets;
using System.Text;
using System.Diagnostics;
using Microsoft.AspNetCore.Identity;

namespace CallAutomation_AzureAI_Speech_Translation
{
    public class SpeechTranslator : IDisposable
    {
        private TranslationRecognizer recognizer;
        public WebSocket InputWebSocket { get; set; }
        public WebSocket OutputWebSocket { get; set; }
        private readonly PushAudioInputStream m_audioInputStream;
        private readonly TranslationRecognizer m_speechRecognizer;
        private readonly SpeechTranslationConfig m_speechConfig;
        private readonly SpeechConfig m_speechOutConfig;
        private readonly SpeechSynthesizer m_speechSynthesizer;
        private CancellationTokenSource m_cts;
        private FileStream m_audioFileStream;
        private bool m_playingTranslation = false;
        private bool m_firstTranslation = true;
        private long m_audioBytesSent = 0;
        private long m_mediaStartTime = 0;
        private bool m_resumedEcho = false;

        public string FromLanguage;
        public string ToLanguage;
        public string VoiceName;
        public string role;
        public bool advTTS;

        private void DebugOut(string logtext)
        {
#if DEBUG
            string timestampedLog = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {logtext}";
            Debug.WriteLine(timestampedLog);
#endif
        }

        private void stripRiffHeader(ref byte[] audioData)
        {
            // Strip the RIFF header from the audio data
            // RIFF header is 44 bytes long in this case
            byte[] newAudioData = new byte[audioData.Length - 44];
            Array.Copy(audioData, 44, newAudioData, 0, newAudioData.Length);
            audioData = newAudioData;
        }

        private int StringCount(string str, List<string> targets)
        {
            int count = 0;
            foreach (var target in targets)
            {
                count += str.Split(new string[] { target }, StringSplitOptions.None).Length - 1;
            }
            return count;
        }

        private string CopyStringToLastOccurrence(string str, List<string> targets, int lastCount, bool isRecognized)
        {
            str += " ";
            int count = StringCount(str, targets);

            int index = -1;
            if (!isRecognized)//Find the last occurrence of any of the target strings
            {
                foreach (var target in targets)
                {
                    int idx = str.LastIndexOf(target) + target.Length;
                    if (idx > index)
                    {
                        index = idx;
                    }
                }
            }

            //Find the occurrences of the target strings we have already processed
            if (lastCount > 0)
            {
                string strTemp = str;
                //If this string comes from a recognized event then take the who string, otherwise just take the string up to the last target string occurance
                if (!isRecognized)
                {
                    strTemp = str.Substring(0, index);
                }
                for (int i = 0; i < lastCount; i++)
                {
                    int index_ = int.MaxValue;
                    foreach (var target in targets)
                    {
                        int idx_ = strTemp.IndexOf(target);
                        if (idx_ > -1)
                        {
                            idx_ = idx_ + target.Length;
                            if (idx_ < index_)
                            {
                                index_ = idx_;
                            }
                        }
                    }
                    strTemp = strTemp.Substring(index_);
                }
                DebugOut($"string: {str}, result string: {strTemp}, count: {count}, last count: {lastCount}");
                return strTemp;
            }
            else
            {
                string strTemp = str;
                //If this string comes from a recognized event then take the who string, otherwise just take the string up to the last target string occurance
                if (!isRecognized)
                {
                    strTemp = str.Substring(0, index);
                }
                DebugOut($"string: {str}, result string: {strTemp}, count: {count}, last count: {lastCount}");
                return strTemp;
            }
        }

        public SpeechTranslator(
            string speechSubscriptionKey,
            string speechRegion,
            WebSocket inboundWebSocket,
            WebSocket outboundWebSocket,
            string inboundLanguage,
            string outboundLanguage,
            string outboundVoice,
            string callerRole,
            bool logSDK,
            bool advancedTTS)
        {
            FromLanguage = inboundLanguage;
            ToLanguage = outboundLanguage;
            VoiceName = outboundVoice;
            InputWebSocket = inboundWebSocket;
            OutputWebSocket = outboundWebSocket;
            advTTS = advancedTTS;
            role = callerRole;

            // Initialize the log file stream
            string logFilename = $"E:\\Logs\\SDK-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

            // Initialize the audio file stream
            //string audioFilename = $"E:\\Logs\\audio-{DateTime.Now:yyyyMMdd-HHmmss}.raw";
            //m_audioFileStream = new FileStream(audioFilename, FileMode.Create, FileAccess.Write);

            // Use the v2 endpoint to get the new translation features
            string v2Endpoint = $"wss://{speechRegion}.stt.speech.microsoft.com/speech/universal/v2";
            var v2EndpointUrl = new Uri(v2Endpoint);
            m_speechConfig = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, speechSubscriptionKey);

            if (logSDK)
            {
                // Enable Speech SDK logs for debugging purposes
                m_speechConfig.SetProperty(PropertyId.Speech_LogFilename, logFilename);
            }

            // Enable server side audio logs for this translation session
            //m_speechConfig.EnableAudioLogging();

            //m_speechConfig.SpeechRecognitionLanguage = FromLanguage;
            m_speechConfig.AddTargetLanguage(ToLanguage);

            if (!advTTS)
                m_speechConfig.VoiceName = VoiceName;
            else
            {
                m_speechOutConfig = SpeechConfig.FromSubscription(speechSubscriptionKey, speechRegion);
                m_speechOutConfig.SpeechSynthesisVoiceName = VoiceName;
                m_speechOutConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
                m_speechSynthesizer = new SpeechSynthesizer(m_speechOutConfig, null);

                SubscribeToSynthEvents();

                //Pre-connect to the TTS service to remove initial latency when the first TTS is requested
                using (var TTSconnection = Connection.FromSpeechSynthesizer(m_speechSynthesizer))
                {
                    DebugOut("TTS: Pre-connect to the speech service.");
                    TTSconnection.Open(true);
                }
            }

            //Enable semantic segmentation for shorter translation turns - this only seems to work right now if the input language is "en-US"
            //m_speechConfig.SetProperty(PropertyId.Speech_SegmentationStrategy, "Semantic");
            //This setting makes the partial translation results more stable, but it also makes the translation results a bit slower.
            m_speechConfig.SetProperty(PropertyId.SpeechServiceResponse_TranslationRequestStablePartialResult, "true");

            m_cts = new CancellationTokenSource();
            m_audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            if (FromLanguage.ToLower() == "any")
            {
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
            SendTranslationText("<clear>").Wait();

            var connection = Connection.FromRecognizer(m_speechRecognizer);
            try
            {
                DebugOut("STT: Pre-connect to the speech service.");
                connection.Open(true);
            }
            catch (ApplicationException ex)
            {
                DebugOut($"Couldn't pre-connect. Details: {ex.Message}");
            }

        }

        // Helper method to send translation text to a local service
        private async Task SendTranslationText(string text)
        {
            const string updateTextUrl = "http://localhost:5000/update-text"; // URL of the update-text endpoint

            try
            {
                using (var httpClient = new HttpClient())
                {
                    text = "[" + role + "] " + text;
                    var content = new StringContent(text, Encoding.UTF8, "text/plain");
                    var response = await httpClient.PostAsync(updateTextUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        DebugOut($"Successfully sent translation text to {updateTextUrl}");
                    }
                    else
                    {
                        DebugOut($"Failed to send translation text. Status Code: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugOut($"Exception occurred while sending translation text: {ex.Message}");
            }
        }


        public void SubscribeToRecognizeEvents()
        {
            int segmentNumber = 0;

            //This is the list of target strings to trigger the TTS output. Includes English and Chinese punctuation. May need to add more target strings for other target languages.
            //var targets = new List<string> { ". ", "? ", ", ", "。", "？" };
            var targets = new List<string> { ". ", "? ", "。", "？" };

            // Subscribes to events.
            m_speechRecognizer.Recognizing += async (s, e) =>
            {
                var transText = e.Result.Translations[ToLanguage];
                DebugOut($"Recognizing translation to {ToLanguage}: {transText}");
                await SendTranslationText(transText);

                if (advTTS)
                {
                    int count = StringCount(transText, targets);
                    if (count > segmentNumber)
                    {
                        //Console.WriteLine("Intermediate Sent to Synth");
                        var transToSynth = CopyStringToLastOccurrence(transText, targets, segmentNumber, false);
                        segmentNumber = count;

                        DebugOut($"Speaking Recognizing: {transToSynth}");
                        //Output TTS here
                        await Task.Run(() => SendSynthesizedOutput(transToSynth));
                    }
                }
            };

            m_speechRecognizer.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    var transText = e.Result.Translations[ToLanguage];
                    if (!string.IsNullOrEmpty(e.Result.Text))
                    {
                        DebugOut($"Recognized translation to {ToLanguage}: {transText}");
                        await SendTranslationText(transText + "\n\n");

                        if (advTTS)
                        {
                            var transToSynth = CopyStringToLastOccurrence(transText, targets, segmentNumber, true);
                            segmentNumber = 0;
                            //Output TTS here
                            DebugOut($"Speaking Recognized: {transToSynth}");
                            await Task.Run(() => SendSynthesizedOutput(transToSynth));
                            m_firstTranslation = false;
                        }
                    }
                }
            };

            m_speechRecognizer.Synthesizing += async (s, e) =>
            {
                if (!advTTS)
                {
                    if (OutputWebSocket != null)
                    {
                        if (OutputWebSocket.State != WebSocketState.Open)
                        {
                            DebugOut($"OutputWebSocket is not open. State: {OutputWebSocket.State}");
                            return;
                        }

                        var audio = e.Result.GetAudio();
                        if (audio.Length > 0)
                        {
                            DebugOut($"TTS out AudioSize: {audio.Length}");
                            // strip the RIFF header from the audio data
                            stripRiffHeader(ref audio);

                            // Create a ServerAudioData object for this chunk
                            var audioData = OutStreamingData.GetAudioDataForOutbound(audio);

                            byte[] jsonBytes = Encoding.UTF8.GetBytes(audioData);
                            await OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                        }
                        else
                        {
                            DebugOut($"TTS out AudioSize: {audio.Length} (end of synthesis data)");
                        }
                    }
                }
            };

            m_speechRecognizer.Canceled += (s, e) =>
            {
                DebugOut($"Recognition canceled. Reason: {e.Reason}; ErrorDetails: {e.ErrorDetails}");
            };

            m_speechRecognizer.SessionStarted += (s, e) =>
            {
                DebugOut("Session started event.");
            };

            m_speechRecognizer.SessionStopped += (s, e) =>
            {
                DebugOut("Session stopped event.");
            };
        }

        public void SubscribeToSynthEvents()
        {
            m_speechSynthesizer.SynthesisCanceled += (s, e) =>
            {
                DebugOut("SynthesisCanceled event");
                var cancellationDetails = SpeechSynthesisCancellationDetails.FromResult(e.Result);
                DebugOut($"Speech synthesis canceled: {cancellationDetails.Reason}");
                if (cancellationDetails.Reason == CancellationReason.Error && cancellationDetails.ErrorDetails != null)
                {
                    DebugOut($"Error details: {cancellationDetails.ErrorDetails}");
                    DebugOut("Did you set the speech resource key and region values?");
                }
            };
            m_speechSynthesizer.SynthesisCompleted += (s, e) =>
            {
                DebugOut($"SynthesisCompleted event: - AudioData: {e.Result.AudioData.Length} bytes - AudioDuration: {e.Result.AudioDuration}");
            };
            m_speechSynthesizer.SynthesisStarted += (s, e) =>
            {
                DebugOut("SynthesisStarted event");
            };
            m_speechSynthesizer.Synthesizing += (s, e) =>
            {
                DebugOut($"Synthesizing event");
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
                    // Write audio to binary file
                    //m_audioFileStream.Write(audioData.Data);
                    EchoInputAudioToOutput(audioData.Data, audioData.Timestamp);                   
                }
            }
        }

        private async Task SendSynthesizedOutput(string transToSynth)
        {
            try
            {
                if (OutputWebSocket != null)
                {
                    if (OutputWebSocket.State != WebSocketState.Open)
                    {
                        DebugOut($"OutputWebSocket is not open. State: {OutputWebSocket.State}");
                        return;
                    }

                    //Block the playing of the Input audio while the TTS is playing
                    m_playingTranslation = true;
                    using (var result = await m_speechSynthesizer.StartSpeakingTextAsync(transToSynth))
                    {
                        using (var audioDataStream = AudioDataStream.FromResult(result))
                        {
                            byte[] audio = new byte[1600];
                            int filledSize = 0;
                            while ((filledSize = (int)audioDataStream.ReadData(audio)) > 0)
                            {

                                var audioData = OutStreamingData.GetAudioDataForOutbound(audio);

                                byte[] jsonBytes = Encoding.UTF8.GetBytes(audioData);
                                DebugOut($"TTS out {audio.Length} bytes");
                                await OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                                m_audioBytesSent += audio.Length;
                            }
                        }
                    }
                    //Unblock the playing of the Input audio
                    m_playingTranslation = false;
                    m_resumedEcho = false;
                    //DebugOut("Set m_resumedEcho to false");
                }
            }
            catch (Exception ex)
            {
                DebugOut($"Exception -> {ex}");
            }
        }

        private void lowerVolume(byte[] audioBytes)
        {
            // Convert byte array to an array of 16-bit values
            short[] audioSamples = new short[audioBytes.Length / 2];
            for (int i = 0; i < audioSamples.Length; i++)
            {
                audioSamples[i] = BitConverter.ToInt16(audioBytes, i * 2);
            }

            // Lower the volume by reducing the amplitude of each sample. Assumes 16-bit PCM audio.
            for (int i = 0; i < audioSamples.Length; i++)
            {
                audioSamples[i] = (short)(audioSamples[i] * 0.25); // Reduce volume by 75%
            }

            // Convert the array of 16-bit values back to a byte array
            for (int i = 0; i < audioSamples.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes(audioSamples[i]);
                audioBytes[i * 2] = bytes[0];
                audioBytes[i * 2 + 1] = bytes[1];
            }
        }

        private async Task EchoInputAudioToOutput(byte[] inputAudioData, DateTimeOffset timestamp)
        {
            try
            {
                if (OutputWebSocket != null && !m_playingTranslation)
                {
                    if (OutputWebSocket.State != WebSocketState.Open)
                    {
                        DebugOut($"OutputWebSocket is not open. State: {OutputWebSocket.State}");
                        return;
                    }
                    //if we already translated some audio then lower the volume of the rest of the echoed audio
                    if(!m_firstTranslation)
                    {
                        lowerVolume(inputAudioData);
                    }
                    
                    var audioData = OutStreamingData.GetAudioDataForOutbound(inputAudioData);

                    byte[] jsonBytes = Encoding.UTF8.GetBytes(audioData);
                    string timeString1 = $"{timestamp.Hour}:{timestamp.Minute}:{timestamp.Second}.{timestamp.Millisecond}";
                    if (m_mediaStartTime == 0)
                    {
                        m_mediaStartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    }
                    long timeDiff = DateTimeOffset.Now.ToUnixTimeMilliseconds() - m_mediaStartTime;
                    long expectedBytes = timeDiff * 32;
                    //absolute value of the difference between the expected bytes and the actual bytes sent should be less than 1600
                    if (expectedBytes > m_audioBytesSent)
                    {
                        DebugOut($"{ToLanguage} Wrote {audioData.Length} bytes of InputAudio to Output, Timestamp {timeString1}, AudioBytes {m_audioBytesSent}, ExpectedBytes {expectedBytes}");
                        await OutputWebSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                        m_audioBytesSent += inputAudioData.Length;
                        //if(!m_firstTranslation && !m_resumedEcho)
                        //{
                        //    await SendTranslationText("[Audio Sent]\n\n");
                        //    m_resumedEcho = true;
                        //    DebugOut("[" + role + "]: Set m_resumedEcho to true");
                        //}                       
                    }
                    else
                    {
                        DebugOut($"{ToLanguage} Skipped writing {audioData.Length} bytes of InputAudio to catch up, Timestamp {timeString1}, AudioBytes {m_audioBytesSent}, ExpectedBytes {expectedBytes}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugOut($"Exception -> {ex}");
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
                await SendSynthesizedOutput("Starting to translate your speech.");
                await m_speechRecognizer.StartContinuousRecognitionAsync();
                while (InputWebSocket.State == WebSocketState.Open || InputWebSocket.State == WebSocketState.Closed)
                {
                    byte[] receiveBuffer = new byte[1024];
                    //DebugOut($"Waiting for message from WebSocket {InputWebSocket.State}");
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
                DebugOut($"Done processing WebSocket");
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

        public void Dispose()
        {
            m_audioFileStream?.Dispose();
            m_cts?.Dispose();
            m_audioInputStream?.Dispose();
            m_speechRecognizer?.Dispose();
            m_speechSynthesizer?.Dispose();
        }
    }
}
