using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Force.Crc32;
using gg_downloader.Models;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using System.Linq;
using Timer = System.Timers.Timer;

namespace gg_downloader.Services
{
    public class HttpClientDownloadWithProgress : IDisposable
    {
        private readonly string _downloadUrl;
        private readonly string _destinationFilePath;
        private readonly string _username;
        private readonly string _password;
        private readonly AsyncRetryPolicy<uint> _retryPolicy;
        private readonly AsyncTimeoutPolicy _timeoutPolicy;
        private long totalBytesRead { get; set; }
        private long? totalDownloadSize { get; set; }
        private long readCount { get; set; }
        private const long minChunkSize = 4 * 1024 * 1024;
        private const int maxThreads = 4;
        private static ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly Timer _downloadTimer;

        //private HttpClient _httpClient;

        public delegate void ProgressChangedHandler(double? totalFileSize, double totalBytesDownloaded, double? progressPercentage, string unit);

        public event ProgressChangedHandler ProgressChanged;

        public HttpClientDownloadWithProgress(string downloadUrl, string destinationFilePath, string username, string password)
        {
            _downloadUrl = downloadUrl;
            _destinationFilePath = destinationFilePath;
            _username = username;
            _password = password;
            _retryPolicy = Policy.HandleResult<uint>(res => res < 0)
                                 .Or<Exception>()
                                 .RetryAsync(10, onRetry: (delegateResult, retryCount) =>
                                 {
                                     Console.Out.WriteLine($"Error: {delegateResult?.Exception?.Message ?? "No exception"}");
                                     Console.Out.WriteLine($"Retrying: {retryCount}");
                                     Thread.Sleep(500);
                                 });
            _timeoutPolicy = Policy.TimeoutAsync(10);
            _retryPolicy.WrapAsync(_timeoutPolicy);
            _downloadTimer = new Timer(200);
            _downloadTimer.Elapsed += updateDownloadStatus;
        }

        public async Task<uint> StartDownload()
        {
            var range = await getFileSize();

            totalBytesRead = 0;
            totalDownloadSize = range.ContentLength;
            readCount = 0;
            uint checksum = 0;

            if (range.AcceptRanges != null && range.AcceptRanges.Contains("bytes") && range.ContentLength.HasValue)
                checksum = await MultiThreadDownload(range.ContentLength.Value);
            else
                checksum = await SingleThreadDownload(range.ContentLength);

            TriggerProgressChanged(totalDownloadSize, totalBytesRead);
            _downloadTimer.Stop();
            return checksum;
        }

        private async Task<uint> MultiThreadDownload(long contentLength)
        {
            var chunks = (contentLength / minChunkSize);

            if (chunks < 2) return await SingleThreadDownload(contentLength);
            else if (chunks > 4) chunks = 4;

            var chunkSize = contentLength / chunks;
            var threadRanges = new List<DownloadRange>();

            for (int i = 0; i < chunks; i++)
            {
                var startByte = chunkSize * i;
                var endByte = (chunkSize * (i + 1)) - 1;
                var range = new DownloadRange(startByte, endByte, $"{_destinationFilePath}.{i}");
                //var range = new DownloadRange(startByte, endByte, _destinationFilePath);
                threadRanges.Add(range);
            }

            threadRanges.LastOrDefault().endByte = contentLength;

            //start chunks of download, save to separate files
            var result = await Task.WhenAll(threadRanges.Select(async x => await ThreadedDownload(x)));

            //Merge files
            await mergeFiles(threadRanges);

            //return complete checksum for all parts 
            return await Crc32FromFile();
        }

        private async Task<uint> ThreadedDownload(DownloadRange range)
        {
            using (var _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                byte[] byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

                if (range.endByte.HasValue) _httpClient.DefaultRequestHeaders.Range = new RangeHeaderValue(range.startByte, range.endByte);

                var checksum = await _retryPolicy.ExecuteAsync(async () =>
                {
                    using (var cts = new CancellationTokenSource())
                    using (var response = await _httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        return await DownloadFileFromHttpResponseMessageWithCrc32(range.destinationFilePath, response, cts.Token);

                });

                return checksum;
            }
        }

        private async Task<uint> SingleThreadDownload(long? contentLength)
        {
            return await ThreadedDownload(new DownloadRange(0, contentLength, _destinationFilePath));
        }


        private async Task<uint> DownloadFileFromHttpResponseMessageWithCrc32(string filePath, HttpResponseMessage response, CancellationToken cts)
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var contentRange = response.Content.Headers.ContentRange;

            using (var contentStream = await response.Content.ReadAsStreamAsync())
                return await ProcessContentStream(filePath, contentStream, contentRange, cts);
        }

        private async Task<uint> ProcessContentStream(string filePath, Stream contentStream, ContentRangeHeaderValue rangeHeaderValue, CancellationToken cts)
        {
            var fileWritePosition = rangeHeaderValue.From ?? 0;
            //var totalBytesRead = 0L;
            //var readCount = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;
            uint? checksum = null;

            //using (var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 8192, true))
            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                //fileStream.Seek(fileWritePosition, SeekOrigin.Begin);
                _downloadTimer.Enabled = true;

                do
                {
                    using (cts.Register(() => contentStream.Close()))
                    {
                        await _timeoutPolicy.ExecuteAsync(async () =>
                        {
                            var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cts);

                            //calculate the CRC32 on what we downloaded.
                            checksum = !checksum.HasValue
                                ? Crc32Algorithm.Compute(buffer, 0, bytesRead)
                                : Crc32Algorithm.Append(checksum.Value, buffer, 0, bytesRead);

                            if (bytesRead == 0)
                            {
                                isMoreToRead = false;
                                //TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                                //continue;
                            }

                            // _lock.TryEnterWriteLock(1000);
                            // try
                            // {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            // }
                            // finally
                            // {
                            //     if (_lock.IsWriteLockHeld)
                            //         _lock.ExitWriteLock();
                            // }

                            totalBytesRead += bytesRead;
                            readCount += 1;

                            // if (readCount % 100 == 0)
                            //     TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                        });
                    }
                }
                while (isMoreToRead);
            }

            return checksum.Value;
        }

        private async Task mergeFiles(List<DownloadRange> ranges)
        {

            var inputFilePaths = ranges.Select(x => x.destinationFilePath).ToArray();
            Console.WriteLine("Number of files: {0}.", inputFilePaths.Length);

            using (var outputStream = new FileStream(inputFilePaths[0], FileMode.Append, FileAccess.Write, FileShare.None, 8192, true))
            {
                for (int i = 1; i < inputFilePaths.Length; i++)
                {
                    using (var inputStream = File.OpenRead(inputFilePaths[i]))
                    {
                        // Buffer size can be passed as the second argument.
                        await inputStream.CopyToAsync(outputStream, 8192);
                    }
                    System.IO.File.Delete(inputFilePaths[i]);
                    Console.WriteLine("The file {0} has been processed.", inputFilePaths[i]);
                }
            }

            System.IO.File.Delete(_destinationFilePath);
            System.IO.File.Move(inputFilePaths[0], _destinationFilePath);
        }

        private void updateDownloadStatus(object sender, System.Timers.ElapsedEventArgs e)
        {
            TriggerProgressChanged(totalDownloadSize, totalBytesRead);
        }


        private void TriggerProgressChanged(double? dblDownloadSize, double dblBytesRead)
        {
            if (ProgressChanged == null)
                return;

            double? progressPercentage = null;

            if (dblDownloadSize.HasValue)
                progressPercentage = Math.Round((double)dblBytesRead / dblDownloadSize.Value * 100, 2);

            long gb = 1024 * 1024 * 870;
            long mb = 1024 * 870;
            long kb = 870;
            var unit = "bytes";

            if (dblBytesRead > gb)
            {
                dblBytesRead = dblBytesRead / gb;
                dblDownloadSize = dblDownloadSize / gb;
                unit = "GiB";
            }
            else if (dblBytesRead > mb)
            {
                dblBytesRead = dblBytesRead / mb;
                dblDownloadSize = dblDownloadSize / mb;
                unit = "MiB";
            }
            else if (dblBytesRead > kb)
            {
                dblBytesRead = dblBytesRead / kb;
                dblDownloadSize = dblDownloadSize / kb;
                unit = "Kib";
            }

            ProgressChanged(Math.Round(dblDownloadSize.Value, 2), Math.Round(dblBytesRead, 2), progressPercentage, unit);
            Thread.Sleep(50);
        }

        private async Task<RangeHeaders> getFileSize()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromDays(1) };
            byte[] byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

            var message = new HttpRequestMessage(HttpMethod.Head, _downloadUrl);

            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);

            var headers = new RangeHeaders()
            {
                ContentLength = response.Content.Headers.ContentLength,
                AcceptRanges = response.Headers.AcceptRanges
            };

            return headers;
        }

        private async Task<uint> Crc32FromFile()
        {
            var totalBytesRead = 0L;
            var readCount = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;
            uint? checksum = null;

            using (var fileStream = new FileStream(_destinationFilePath, FileMode.Open, FileAccess.Read, FileShare.None, 8192, true))
            {
                do
                {
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);

                    // calculate the CRC32 on what we downloaded.
                    checksum = !checksum.HasValue
                        ? Crc32Algorithm.Compute(buffer, 0, bytesRead)
                        : Crc32Algorithm.Append(checksum.Value, buffer, 0, bytesRead);

                    if (bytesRead == 0)
                    {
                        isMoreToRead = false;
                        continue;
                    }

                    totalBytesRead += bytesRead;
                    readCount += 1;
                }
                while (isMoreToRead);
            }

            return checksum.Value;
        }

        public void Dispose()
        {
            _downloadTimer.Dispose();
            //     _httpClient?.Dispose();
        }
    }
}
