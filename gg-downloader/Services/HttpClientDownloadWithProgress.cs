using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Force.Crc32;
using Polly;
using Polly.Retry;

namespace gg_downloader.Services
{
    public class HttpClientDownloadWithProgress : IDisposable
    {
        private readonly string _downloadUrl;
        private readonly string _destinationFilePath;
        private readonly string _username;
        private readonly string _password;
        private readonly AsyncRetryPolicy<uint> _retryPolicy;

        private HttpClient _httpClient;

        public delegate void ProgressChangedHandler(long? totalFileSize, long totalBytesDownloaded, double? progressPercentage);

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
        }

        public async Task<uint> StartDownload()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromDays(1) };
            byte[] byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");


            var checksum = await _retryPolicy.ExecuteAsync(async () =>
            {
                using (var response = await _httpClient.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    return await DownloadFileFromHttpResponseMessageWithCrc32(response);
            });

            return checksum;

        }

        private async Task<uint> DownloadFileFromHttpResponseMessageWithCrc32(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            using (var contentStream = await response.Content.ReadAsStreamAsync())
                return await ProcessContentStream(totalBytes, contentStream);
        }

        private async Task<uint> ProcessContentStream(long? totalDownloadSize, Stream contentStream)
        {
            var totalBytesRead = 0L;
            var readCount = 0L;
            var buffer = new byte[8192];
            var isMoreToRead = true;
            uint? checksum = null;

            using (var fileStream = new FileStream(_destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                do
                {
                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length);

                    // calculate the CRC32 on what we downloaded.
                    checksum = !checksum.HasValue
                        ? Crc32Algorithm.Compute(buffer, 0, bytesRead)
                        : Crc32Algorithm.Append(checksum.Value, buffer, 0, bytesRead);

                    if (bytesRead == 0)
                    {
                        isMoreToRead = false;
                        TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                        continue;
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead);

                    totalBytesRead += bytesRead;
                    readCount += 1;

                    if (readCount % 100 == 0)
                        TriggerProgressChanged(totalDownloadSize, totalBytesRead);
                }
                while (isMoreToRead);
            }

            return checksum.Value;
        }

        private void TriggerProgressChanged(long? totalDownloadSize, long totalBytesRead)
        {
            if (ProgressChanged == null)
                return;

            double? progressPercentage = null;
            if (totalDownloadSize.HasValue)
                progressPercentage = Math.Round((double)totalBytesRead / totalDownloadSize.Value * 100, 2);

            ProgressChanged(totalDownloadSize, totalBytesRead, progressPercentage);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
