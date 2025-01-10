using Microsoft.CognitiveServices.Speech.Audio;
using System.IO;

namespace CallAutomation_AzureAI_Speech_Translation
{
    public class PcmAudioStreamWriter : PushAudioOutputStreamCallback
    {
        private readonly MemoryStream _memoryStream;

        public PcmAudioStreamWriter(MemoryStream memoryStream)
        {
            _memoryStream = memoryStream;
        }

        public override uint Write(byte[] dataBuffer)
        {
            _memoryStream.Write(dataBuffer, 0, dataBuffer.Length);
            return (uint)dataBuffer.Length; // Return the number of bytes written
        }

        public override void Close()
        {
            _memoryStream.Dispose();
            base.Close();
        }
    }
}
