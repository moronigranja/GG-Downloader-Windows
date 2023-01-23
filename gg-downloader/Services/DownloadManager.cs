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

namespace gg_downloader.Services
{
    public class DownloadResult
    {
        public long startByte { get; set; }
        public long endByte { get; set; }
        public uint CheckSum { get; set; }
    }

    public class DownloadManager : IDisposable
    {
        private readonly string _downloadUrl;
        private readonly string _destinationFilePath;
        private readonly string _username;
        private readonly string _password;
        private const long minChunkSize = 1 * 1024 * 1024;
        private const int _maxThreads = 4;
        private readonly int _threads;
        private readonly DownloadProgressTracker _progressManager;
        private HttpClient _httpClient;
        private Dictionary<int, long> _downloadedBytes;
        private DateTime _startTime;
        private DateTime _endTime;
        private readonly Dictionary<string, string> _sfvDictionary;
        private readonly object objectLock = new object();

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
            //_progressManager.ProgressChanged += UpdateDownloadProgress;

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
            uint compositeCheckSum = mergeCRCs(downloadTasks);

            //return complete checksum for all parts 
            var checkSumFromFile = await Crc32FromFile();

            return checkSumFromFile;
        }

        private uint mergeCRCs(List<HttpClientDownloadWithProgress> results)
        {
            var resultsArray = results.ToArray();
            long contentLength = resultsArray[resultsArray.Length - 1].EndByte.Value;

            uint compositeCheckSum = 0;

            for (int i = 0; i < resultsArray.Length; i++)
            {
                var paddedChecksum = resultsArray[i].Checksum;
                long bytesToAdd = contentLength - (resultsArray[i].EndByte.Value - 7); //+1

                while (bytesToAdd > 0)
                {
                    long addNow = bytesToAdd > 65536 ? 65536 : bytesToAdd;
                    bytesToAdd = bytesToAdd - addNow;

                    byte[] endPadding = new byte[addNow];
                    Console.WriteLine($"Before appending {addNow} bytes:: {paddedChecksum.ToString("X8")}");
                    paddedChecksum = Crc32Algorithm.Append(paddedChecksum, endPadding, 0, endPadding.Length);
                    Console.WriteLine($"After appending  {addNow} bytes:: {paddedChecksum.ToString("X8")}");
                }
                Console.WriteLine($"Singles:");
                for (int j = 0; j < 16; j++)
                {
                    byte[] endPadding = new byte[1];
                    Console.WriteLine($"Before appending 1 bytes:: {paddedChecksum.ToString("X8")}");
                    paddedChecksum = Crc32Algorithm.Append(paddedChecksum, endPadding, 0, 1);
                    Console.WriteLine($"After appending  1 bytes:: {paddedChecksum.ToString("X8")}");
                }
                Console.WriteLine($"{i}");
                Console.WriteLine($"compositeCheckSum before: {compositeCheckSum.ToString("X8")}");
                Console.WriteLine($"paddedChecksum before   : {paddedChecksum.ToString("X8")}");
                compositeCheckSum = compositeCheckSum ^ paddedChecksum;
                Console.WriteLine($"compositeCheckSum after : {compositeCheckSum.ToString("X8")}");
            }

            return compositeCheckSum;
        }

        private void UpdateDownloadedBytes(int chunkNumber, long totalBytesRead)
        {
            long totalBytes = 0;

            lock (objectLock)
            {
                if (_downloadedBytes.ContainsKey(chunkNumber))
                    _downloadedBytes[chunkNumber] = totalBytesRead;
                else
                    _downloadedBytes.Add(chunkNumber, totalBytesRead);

                foreach (var item in _downloadedBytes)
                {
                    totalBytes += item.Value;
                }
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

        private async Task<uint> Crc32FromFile(bool reportSpeed = true)
        {
            int bufferSize = 65536;
            DateTime start = DateTime.Now;
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
            DateTime end = DateTime.Now;
            Console.WriteLine($"Checked in {(end - start).TotalSeconds} seconds {checksum.ToString("X8")}");

            return checksum;
        }


        public void Dispose()
        {
            _progressManager?.Dispose();
            _httpClient?.Dispose();
        }
    }
}
