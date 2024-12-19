using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using System;


namespace CallAutomation_AzureAI_Speech_Translation
{
    public class SpeechTranslation
    {
        private static AudioConfig? audioConfigCaller;
        private static TranslationRecognizer? translatorCaller;
        private static SpeechTranslationConfig? translationConfigCaller;
        private static AudioConfig? audioConfigAgent;
        private static string? callerLanguage;
        private static TranslationRecognizer? translatorAgent;
        private static SpeechTranslationConfig? translationConfigAgent;
        private static byte[]? callerAudioOut;
        private static byte[]? agentAudioOut;
        private static string? agentLanguage;
        private static bool done = false;


        public static void ConfigureSpeechTranslation(string subscriptionKey, string region, string callerLanguage, string agentLanguage,
                                                    AudioInputStream callerAudioIn, AudioInputStream agentAudioIn,
                                                    byte[] callerAudioOut_, byte[] agentAudioOut_)
        {

            // This enables multi-lingual translation with auto detection of the source language among a large set of input languages.
            var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromOpenRange();

            // Configure the callers translation recognizer
            // Audio comes from a push stream
            audioConfigCaller = AudioConfig.FromStreamInput(callerAudioIn);
            // Use the v2 endpoint to get the new translation features
            string v2Endpoint = $"wss://{region}.stt.speech.microsoft.com/speech/universal/v2";
            var v2EndpointUrl = new Uri(v2Endpoint);
            translationConfigCaller = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, subscriptionKey);
            // Set the target language for translation to be the language of the agent
            translationConfigCaller.AddTargetLanguage(agentLanguage);
            //This setting makes the partial translation results more stable, but it also makes the translation results a bit slower.
            translationConfigCaller.SetProperty(PropertyId.SpeechServiceResponse_TranslationRequestStablePartialResult, "true");
            //Set the voice name for the caller translation output
            translationConfigCaller.VoiceName = "en-US-AndrewMultilingualNeural";
            //Set the speech synthesis output format to raw 8Khz 16bit mono PCM
            translationConfigCaller.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw8Khz16BitMonoPcm);

            // Create the translation recognizer for the caller
            translatorCaller = new TranslationRecognizer(translationConfigCaller, autoDetectSourceLanguageConfig, audioConfigCaller);

            // Configure the agent translation recognizer
            // Audio comes from a push stream
            audioConfigAgent = AudioConfig.FromStreamInput(agentAudioIn);
            // Use the v2 endpoint to get the new translation features
            translationConfigAgent = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, subscriptionKey);
            // Set the target language for translation to be the language of the caller
            translationConfigAgent.AddTargetLanguage(callerLanguage);
            //This setting makes the partial translation results more stable, but it also makes the translation results a bit slower.
            translationConfigAgent.SetProperty(PropertyId.SpeechServiceResponse_TranslationRequestStablePartialResult, "true");
            //Set the voice name for the agent translation output
            translationConfigAgent.VoiceName = "en-US-AvaMultilingualNeural";

            // Create the translation recognizer for the agent
            translatorAgent = new TranslationRecognizer(translationConfigAgent, autoDetectSourceLanguageConfig, audioConfigAgent);

            // Set the audio output streams
            callerAudioOut = callerAudioOut_;
            agentAudioOut = agentAudioOut_;
        }

        public static async Task StartTranslation()
        {
            if (translatorCaller == null || translatorAgent == null)
            {
                throw new Exception("Translator not configured");
            }

            //Events for the Callers Translation Recognizer
            translatorCaller.Recognizing += (s, e) =>
            {
                var transText = e.Result.Translations[agentLanguage];
                Console.WriteLine($"Recognizing:\nRecognition result: {e.Result.Text}\n{agentLanguage} translation: {transText}");                              
            };

            translatorCaller.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    var transText = e.Result.Translations[agentLanguage];
                    if (!string.IsNullOrEmpty(e.Result.Text))
                    {
                        Console.WriteLine($"Recognized:\nRecognition result: {e.Result.Text}\n{agentLanguage} translation: {transText}");
                    }
                }
                else if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    Console.WriteLine($"Recognized:\n {e.Result.Text}");
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Console.WriteLine($"No speech could be recognized");
                }
                else if (e.Result.Reason == ResultReason.Canceled)
                {
                    var cancellation = CancellationDetails.FromResult(e.Result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                    }
                }
            };

            //Audio synthesis event for the Callers Translation Recognizer
            translatorCaller.Synthesizing += (_, e) =>
            {
                var audio = e.Result.GetAudio();
                Console.WriteLine($"Audio synthesized: {audio.Length:#,0} byte(s) {(audio.Length == 0 ? "(Complete)" : "")}");

                if (audio.Length > 0 && callerAudioOut != null)
                {
                   lock (callerAudioOut)
                   {
                            Array.Copy(audio, callerAudioOut, audio.Length);
                   }
                }
            };

            translatorCaller.SessionStarted += (s, e) => Console.WriteLine($"SESSION STARTED: {e}");
            translatorCaller.SessionStopped += (s, e) => Console.WriteLine($"SESSION STOPPED {e}");
            translatorCaller.Canceled += (s, e) => Console.WriteLine($"CANCELED: {e} ({e.Reason})");

            translatorCaller.SessionStopped += (s, e) => done = true;
            translatorCaller.Canceled += (s, e) => done = true;

            //Events for the Callers Translation Recognizer
            translatorCaller.Recognizing += (s, e) =>
            {
                var transText = e.Result.Translations[agentLanguage];
                Console.WriteLine($"Recognizing:\nRecognition result: {e.Result.Text}\n{agentLanguage} translation: {transText}");
            };

            translatorCaller.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    var transText = e.Result.Translations[agentLanguage];
                    if (!string.IsNullOrEmpty(e.Result.Text))
                    {
                        Console.WriteLine($"Recognized:\nRecognition result: {e.Result.Text}\n{agentLanguage} translation: {transText}");
                    }
                }
                else if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    Console.WriteLine($"Recognized:\n {e.Result.Text}");
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Console.WriteLine($"No speech could be recognized");
                }
                else if (e.Result.Reason == ResultReason.Canceled)
                {
                    var cancellation = CancellationDetails.FromResult(e.Result);
                    Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                        Console.WriteLine($"CANCELED: ErrorDetails={cancellation.ErrorDetails}");
                        Console.WriteLine($"CANCELED: Did you set the speech resource key and region values?");
                    }
                }
            };

            //Audio synthesis event for the Agents Translation Recognizer
            translatorAgent.Synthesizing += (_, e) =>
            {
                var audio = e.Result.GetAudio();
                Console.WriteLine($"Audio synthesized: {audio.Length:#,0} byte(s) {(audio.Length == 0 ? "(Complete)" : "")}");

                if (audio.Length > 0 && agentAudioOut != null)
                {
                    lock (agentAudioOut)
                    {
                        Array.Copy(audio, agentAudioOut, audio.Length);
                    }
                }
            };

            translatorAgent.SessionStarted += (s, e) => Console.WriteLine($"SESSION STARTED: {e}");
            translatorAgent.SessionStopped += (s, e) => Console.WriteLine($"SESSION STOPPED {e}");
            translatorAgent.Canceled += (s, e) => Console.WriteLine($"CANCELED: {e} ({e.Reason})");

            translatorAgent.SessionStopped += (s, e) => done = true;
            translatorAgent.Canceled += (s, e) => done = true;

            // Start the translation recognizers
            await translatorCaller.StartContinuousRecognitionAsync();
            await translatorAgent.StartContinuousRecognitionAsync();

            while (!done)
            {
                Thread.Sleep(500);
            }

            Console.WriteLine("Done translating.");
            await translatorCaller.StopContinuousRecognitionAsync();
            await translatorAgent.StopContinuousRecognitionAsync();
        }
    }
}
