using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Force.Crc32;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace gg_downloader.Services
{
    public class HttpClientDownloadWithProgress
    {
        private readonly string _downloadUrl;
        private readonly string _destinationFilePath;
        private readonly AsyncRetryPolicy<uint> _retryPolicy;
        private readonly AsyncTimeoutPolicy _timeoutPolicy;
        private HttpClient _httpClient;
        private readonly long? _contentLength;
        private readonly long _startByte;
        private readonly long? _endByte;
        private long _totalBytesRead;
        private uint _checksum;


        public readonly int ChunkNumber;
        public string FilePath { get { return _destinationFilePath; } }
        public long StartByte { get { return _startByte; } }
        public long? EndByte { get { return _endByte; } }
        public uint Checksum { get { return _checksum; } }

        public delegate void DownloadedBytesChangedHandler(int chunkNumber, long totalBytesDownloaded);
        public event DownloadedBytesChangedHandler DownloadedBytesChanged;

        public HttpClientDownloadWithProgress(string downloadUrl, string destinationFilePath, HttpClient httpClient, long startByte, long? endByte, long? contentLength, int chunkNumber)
        {
            _downloadUrl = downloadUrl;
            _destinationFilePath = destinationFilePath;
            _httpClient = httpClient;
            _startByte = startByte;
            _endByte = endByte;
            _contentLength = contentLength;
            ChunkNumber = chunkNumber;

            _retryPolicy = Policy.HandleResult<uint>(res => res < 0)
                                 .Or<Exception>()
                                 .RetryAsync(10, onRetry: (delegateResult, retryCount) =>
                                 {
                                     if (delegateResult.Exception.GetType() != typeof(TimeoutRejectedException))
                                     {
                                         Console.Out.WriteLine($" - Error: {delegateResult?.Exception?.Message ?? "No exception"}, retrying: {retryCount}/10");
                                         //  if (delegateResult?.Exception != null)
                                         //  {
                                         //      Console.WriteLine(delegateResult?.Exception?.StackTrace);
                                         //  }
                                     }
                                     else
                                     {
                                         Console.Out.WriteLine($" - Download thread {ChunkNumber} stalled, retrying: {retryCount}/10");
                                     }

                                     Thread.Sleep(500);
                                 });
            _timeoutPolicy = Policy.TimeoutAsync(10);
        }

        public async Task<uint> ThreadedDownload()
        {
            _totalBytesRead = 0;
            _checksum = 0;

            var checksum = await _retryPolicy.ExecuteAsync(async () =>
            {
                if (_endByte.HasValue) _httpClient.DefaultRequestHeaders.Range = new RangeHeaderValue(_startByte + _totalBytesRead, _endByte);
                //Console.WriteLine($"startByte: {_startByte}, TotalBytesRead: {_totalBytesRead}, EndByte: {_endByte}");

                using (var cts = new CancellationTokenSource())
                using (var response = await _httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    return await DownloadFileFromHttpResponseMessageWithCrc32(_destinationFilePath, response, cts.Token);
            });

            return checksum;
        }

        private async Task<uint> DownloadFileFromHttpResponseMessageWithCrc32(string filePath, HttpResponseMessage response, CancellationToken cts)
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var contentRange = response.Content.Headers.ContentRange;

            if (contentRange == null)
            {
                Console.WriteLine("Range header null");
            }

            using (var contentStream = await response.Content.ReadAsStreamAsync())
                return await ProcessContentStream(filePath, contentStream, contentRange, cts);
        }

        private async Task<uint> ProcessContentStream(string filePath, Stream contentStream, ContentRangeHeaderValue rangeHeaderValue, CancellationToken cts)
        {
            int bufferSize = 65536;
            var fileWritePosition = rangeHeaderValue?.From ?? 0;
            var buffer = new byte[bufferSize];
            var isMoreToRead = true;

            using (var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, bufferSize, true))
            //using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                fileStream.Seek(fileWritePosition, SeekOrigin.Begin);

                do
                {
                    using (cts.Register(() => contentStream.Close()))
                    {
                        await _timeoutPolicy.ExecuteAsync(async token =>
                        {
                            var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, token);

                            //calculate the CRC32 on what we downloaded.s
                            _checksum = Crc32Algorithm.Append(_checksum, buffer, 0, bytesRead);

                            if (bytesRead == 0)
                            {
                                isMoreToRead = false;
                            }

                            await fileStream.WriteAsync(buffer, 0, bytesRead);

                            _totalBytesRead += bytesRead;
                            TriggerProgressChanged(_totalBytesRead);

                        }, cts);
                    }
                }
                while (isMoreToRead);
            }

            return _checksum;
        }

        private void TriggerProgressChanged(long totalBytesRead)
        {
            if (DownloadedBytesChanged != null)
                DownloadedBytesChanged(ChunkNumber, totalBytesRead);
        }
    }
}
