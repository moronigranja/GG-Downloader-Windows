using System.Net.Http.Headers;

namespace gg_downloader.Models
{
    internal class RangeHeaders
    {
        public long? ContentLength { get; set; }
        public HttpHeaderValueCollection<string> AcceptRanges { get; set; }
    }
}
