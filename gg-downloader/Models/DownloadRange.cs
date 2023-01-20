using System.Net.Http.Headers;

namespace gg_downloader.Models
{
    internal class DownloadRange
    {
        public DownloadRange(long start, long? end)
        {
            startByte = start;
            endByte = end;
        }

        public DownloadRange(long start, long? end, string filePath)
        {
            startByte = start;
            endByte = end;
            destinationFilePath = filePath;
        }

        public long startByte { get; set; }
        public long? endByte { get; set; }
        public string destinationFilePath { get; set; }
    }
}
