namespace FileMonitoringService
{
    using System.IO;
    public class FileMonitorService
    {
        private string _textContent;
        private readonly object _lock = new();

        public FileMonitorService()
        {
            _textContent = " "; // Default message
        }

        // Method to update the text content
        public void UpdateText(string newText)
        {
            lock (_lock)
            {
                _textContent = newText;
            }
        }

        // Method to retrieve the latest text content
        public string GetText()
        {
            lock (_lock)
            {
                return _textContent;
            }
        }
    }
}
