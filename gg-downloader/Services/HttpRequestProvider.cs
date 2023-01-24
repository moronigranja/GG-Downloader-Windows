using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;

namespace gg_downloader.Services
{
    internal class HttpRequest
    {

        static readonly HttpClient client;
        static readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

        static HttpRequest()
        {
            client = new HttpClient();
            _retryPolicy = Policy.HandleResult<HttpResponseMessage>(res => !res.IsSuccessStatusCode)
                     .Or<Exception>()
                     .RetryAsync(10, onRetry: (delegateResult, retryCount) =>
                            {
                                Console.Out.WriteLine($"Error: {delegateResult?.Exception?.Message ?? "No exception"}, retrying: {retryCount}/10");
                                Thread.Sleep(500);
                            });
        }

        public static async Task<HttpResponseMessage> Get(Uri uri)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");
            
            HttpResponseMessage response = await _retryPolicy.ExecuteAsync(async () =>
                await client.GetAsync(uri)
            );

            return response;
        }

        public static async Task<HttpResponseMessage> Get(Uri uri, string username, string password)
        {

            byte[] byteArray = Encoding.ASCII.GetBytes($"{username}:{password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

            HttpResponseMessage response = await _retryPolicy.ExecuteAsync(async () =>
                await client.GetAsync(uri)
            );

            return response;
        }

    }
}
