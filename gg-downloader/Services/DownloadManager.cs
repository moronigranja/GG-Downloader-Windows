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
        private readonly DownloadProgressTracker _progressManager;
        private HttpClient _httpClient;
        private Dictionary<int, long> _downloadedBytes;
        private DateTime _startTime;
        private DateTime _endTime;
        private readonly Dictionary<string, string> _sfvDictionary;

        public delegate void ProgressChangedHandler(double? totalFileSize, double totalBytesDownloaded, double? progressPercentage, string unit);

        public event ProgressChangedHandler ProgressChanged;

        public DownloadManager(string downloadUrl, string destinationFilePath, string username, string password, Dictionary<string, string> sfvDictionary)
        {
            _downloadUrl = downloadUrl;
            _destinationFilePath = destinationFilePath;
            _username = username;
            _password = password;
            _downloadedBytes = new Dictionary<int, long>();
            _sfvDictionary = sfvDictionary;

            _progressManager = new DownloadProgressTracker();
            _progressManager.ProgressChanged += UpdateDownloadProgress;

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        private void UpdateDownloadProgress(double? totalFileSize, double totalBytesDownloaded, double? progressPercentage, string unit, string speed)
        {
            Console.Write($"\r{progressPercentage}% ({totalBytesDownloaded} {unit}/{totalFileSize} {unit}) {speed}       ");
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
                var crc32CheckSum = await Crc32FromFile();
                if (crc32CheckSum.ToString("X8").ToLower() == _sfvDictionary[filename])
                {
                    Console.WriteLine($"{filename} found in destination path.");
                    return crc32CheckSum;
                }
            }

            return 0;
        }

        private void PrintTotalDownloadTime(TimeSpan downloadTime, long? contentLength)
        {
            var filename = Path.GetFileName(_destinationFilePath);
            var speed = ((contentLength / downloadTime.TotalSeconds) * 8) / (1024 * 1024);
            Console.WriteLine($"\rDownloaded {filename}. {contentLength} bytes in {downloadTime.TotalSeconds} seconds ({Math.Round(speed.Value, 2)} Mbps).");
        }

        private async Task<uint> MultiThreadDownload(long contentLength)
        {
            var chunks = (contentLength / minChunkSize);
            if (chunks > _maxThreads) chunks = _maxThreads;

            if (chunks < 2) return await SingleThreadDownload(contentLength);

            var chunkSize = contentLength / chunks;
            var downloadTasks = new List<HttpClientDownloadWithProgress>();

            for (int i = 0; i < chunks; i++)
            {
                var startByte = chunkSize * i;
                var endByte = (chunkSize * (i + 1)) - 1;
                if (i == chunks - 1) endByte = contentLength;

                //var task = new HttpClientDownloadWithProgress(_downloadUrl, $"{_destinationFilePath}.{i}", _httpClient, startByte, endByte, contentLength, i);
                var task = new HttpClientDownloadWithProgress(_downloadUrl, _destinationFilePath, CleanHttpClient, startByte, endByte, contentLength, i);
                task.DownloadedBytesChanged += UpdateDownloadedBytes;

                downloadTasks.Add(task);
            }

            _startTime = DateTime.Now;

            //start chunks of download, save to separate files
            var result = await Task.WhenAll(downloadTasks.Select(async x => await x.ThreadedDownload()));

            _endTime = DateTime.Now;
            _progressManager.Stop();

            //Merge files
            //await mergeFiles(downloadTasks.Select(x => x.FilePath).ToArray());

            //Merge CRCs
            // uint compositeCheckSum = result[0];
            // for (int i = 1; i < chunks; i++)
            // {
            //     compositeCheckSum = compositeCheckSum ^ result[i];
            // }

            //return complete checksum for all parts 
            var checkSumFromFile = await Crc32FromFile();

            return checkSumFromFile;
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

        private void UpdateDownloadedBytes(int chunkNumber, long totalBytesRead)
        {
            if (_downloadedBytes.ContainsKey(chunkNumber))
                _downloadedBytes[chunkNumber] = totalBytesRead;
            else
                _downloadedBytes.Add(chunkNumber, totalBytesRead);

            long totalBytes = 0;
            foreach (var item in _downloadedBytes)
            {
                totalBytes += item.Value;
            }

            if (totalBytes > 0) _progressManager.Start();

            _progressManager.CurrentBytesRead = totalBytes;
        }

        private async Task mergeFiles(string[] inputFilePaths)
        {

            //var inputFilePaths = ranges.Select(x => x.destinationFilePath).ToArray();
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
            var client = CleanHttpClient;

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
            _progressManager?.Dispose();
            _httpClient?.Dispose();
        }
    }
}
