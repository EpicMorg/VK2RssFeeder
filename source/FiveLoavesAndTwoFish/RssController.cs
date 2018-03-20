using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;

namespace FiveLoavesAndTwoFish {
    public class RssController : ApiController {
        public HttpResponseMessage Get() => new HttpResponseMessage { Content = new StringContent( Program.Rss ?? "w8, still starting up" ) { Headers = { ContentType = new MediaTypeHeaderValue( "application/rss+xml" ) } } };
    }
}