using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Force.Crc32;
using gg_downloader.Models;
using System.Linq;
using Polly;
using Polly.Retry;
using System.Threading;

namespace gg_downloader.Services
{

    public class DownloadManager : IDisposable
    {
        private readonly string _downloadUrl;
        private readonly string _destinationFilePath;
        private readonly string _username;
        private readonly string _password;
        private const long minChunkSize = 100 * 1024 * 1024;
        private const int _maxThreads = 4;
        private readonly int _threads;
        private readonly DownloadProgressTracker _progressManager;
        private HttpClient _httpClient;
        private Dictionary<int, long> _downloadedBytes;
        private DateTime _startTime;
        private DateTime _endTime;
        private readonly Dictionary<string, string> _sfvDictionary;
        private readonly object objectLock = new object();
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

        public DownloadManager(string downloadUrl, string destinationFilePath, string username, string password, Dictionary<string, string> sfvDictionary, int threads)
        {
            _downloadUrl = downloadUrl;
            _destinationFilePath = destinationFilePath;
            _username = username;
            _password = password;
            _downloadedBytes = new Dictionary<int, long>();
            _sfvDictionary = sfvDictionary;
            _threads = threads > _maxThreads ? _maxThreads : threads;

            _progressManager = new DownloadProgressTracker();

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);


            _retryPolicy = Policy.HandleResult<HttpResponseMessage>(res => !res.IsSuccessStatusCode)
                     .Or<Exception>()
                     .RetryAsync(10, onRetry: (delegateResult, retryCount) =>
                     {
                         Console.Out.WriteLine($"Error: {delegateResult?.Exception?.Message ?? "No exception"}, retrying: {retryCount}/10");
                         Thread.Sleep(500);
                     });
        }

        private HttpClient CleanHttpClient
        {
            get
            {
                _httpClient.DefaultRequestHeaders.Clear();

                byte[] byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

                return _httpClient;
            }
        }

        public async Task<uint> StartDownload()
        {
            var range = await getFileSize();

            uint checksum = await CheckIfDownloadAlreadyDone();
            if (checksum > 0) return checksum;

            _progressManager.Reset();
            _progressManager.TotalDownloadSize = range.ContentLength;

            if (range.AcceptRanges != null && range.AcceptRanges.Contains("bytes") && range.ContentLength.HasValue)
                checksum = await MultiThreadDownload(range.ContentLength.Value);
            else
                checksum = await SingleThreadDownload(range.ContentLength);

            _progressManager.Stop();

            PrintTotalDownloadTime(_endTime - _startTime, range.ContentLength);
            return checksum;
        }

        private async Task<uint> CheckIfDownloadAlreadyDone()
        {
            string filename = Path.GetFileName(_destinationFilePath);

            if (_sfvDictionary != null && !string.IsNullOrEmpty(filename) && File.Exists(_destinationFilePath) && _sfvDictionary.ContainsKey(filename))
            {
                Console.WriteLine($"{filename} found in destination path.");

                var crc32CheckSum = await Crc32FromFile();

                if (crc32CheckSum.ToString("X8").ToLower() == _sfvDictionary[filename])
                {
                    return crc32CheckSum;
                }
                Console.WriteLine($"{filename} failed SFV validation. Will download again.");
            }

            return 0;
        }

        private void PrintTotalDownloadTime(TimeSpan downloadTime, long? contentLength)
        {
            var filename = Path.GetFileName(_destinationFilePath);
            var speed = (contentLength / downloadTime.TotalSeconds) / (1024 * 1024);
            var filesize = (double)contentLength / (1024 * 1024);
            Console.WriteLine($"\rDownloaded {filename}. {Math.Round(filesize, 2)} MiB in {downloadTime.TotalSeconds} seconds ({Math.Round(speed.Value, 2)} MB/s).");
        }

        private async Task<uint> SingleThreadDownload(long? contentLength)
        {

            var downloadTask = new HttpClientDownloadWithProgress(_downloadUrl, _destinationFilePath, CleanHttpClient, 0, contentLength, contentLength, 0);
            downloadTask.DownloadedBytesChanged += UpdateDownloadedBytes;

            _startTime = DateTime.Now;
            var checkSum = await downloadTask.ThreadedDownload();
            _endTime = DateTime.Now;

            return checkSum;
        }

        private async Task<uint> MultiThreadDownload(long contentLength)
        {
            var chunks = (contentLength / minChunkSize);
            if (chunks > _threads) chunks = _threads;

            if (chunks < 2) return await SingleThreadDownload(contentLength);

            var chunkSize = contentLength / chunks;
            var downloadTasks = new List<HttpClientDownloadWithProgress>();

            for (int i = 0; i < chunks; i++)
            {
                var startByte = chunkSize * i;
                var endByte = (chunkSize * (i + 1)) - 1;
                if (i == chunks - 1) endByte = contentLength; //Sets the last byte of last chunk

                var task = new HttpClientDownloadWithProgress(_downloadUrl, _destinationFilePath, CleanHttpClient, startByte, endByte, contentLength, i);
                task.DownloadedBytesChanged += UpdateDownloadedBytes;

                downloadTasks.Add(task);
            }

            _startTime = DateTime.Now;

            //start chunks of download, save to separate files
            var result = await Task.WhenAll(downloadTasks.Select(async x => await x.ThreadedDownload()));

            _endTime = DateTime.Now;
            _progressManager.Stop();

            //return complete checksum for all parts 
            var checkSumFromFile = await Crc32FromFile();

            return checkSumFromFile;
        }

        private void UpdateDownloadedBytes(int chunkNumber, long totalBytesRead)
        {
            long totalBytes = 0;

            _downloadedBytes[chunkNumber] = totalBytesRead;
            totalBytes = _downloadedBytes.Sum(x => x.Value);

            if (totalBytes > 0) _progressManager.Start();

            _progressManager.CurrentBytesRead = totalBytes;
        }

        private async Task<RangeHeaders> getFileSize()
        {
            var client = CleanHttpClient;

            var message = new HttpRequestMessage(HttpMethod.Head, _downloadUrl);

            var response = await _retryPolicy.ExecuteAsync(async () =>
                        await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead)
            );

            var headers = new RangeHeaders()
            {
                ContentLength = response.Content.Headers.ContentLength,
                AcceptRanges = response.Headers.AcceptRanges
            };

            return headers;
        }

        private async Task<uint> Crc32FromFile(bool reportSpeed = true)
        {
            int bufferSize = 65536;
            var totalBytesRead = 0L;
            var buffer = new byte[bufferSize];
            var isMoreToRead = true;
            uint checksum = 0;
            double lastPercent = -1;

            if (!File.Exists(_destinationFilePath)) return 0;

            using (var fileStream = new FileStream(_destinationFilePath, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize, true))
            {
                Console.Write("\n");
                do
                {
                    var bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);

                    // calculate the CRC32 on what we downloaded.
                    checksum = Crc32Algorithm.Append(checksum, buffer, 0, bytesRead);

                    if (bytesRead == 0)
                    {
                        isMoreToRead = false;
                        continue;
                    }

                    totalBytesRead += bytesRead;

                    if (reportSpeed)
                    {
                        var percent = Math.Round((double)totalBytesRead / fileStream.Length * 100, 0);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            var percentString = percent.ToString().PadLeft(3, ' ');
                            Console.Write($"\rCalculating CRC32 {percentString}% ...");
                        }
                    }
                }
                while (isMoreToRead);
            }

            Console.WriteLine("complete.");

            return checksum;
        }


        public void Dispose()
        {
            _progressManager?.Dispose();
            _httpClient?.Dispose();
        }
    }
}
