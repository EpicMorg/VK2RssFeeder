using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using kasthack.vksharp;
using kasthack.vksharp.DataTypes.Entities;
using kasthack.vksharp.DataTypes.Enums;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json;
using WilderMinds.RssSyndication;
using ContentType = kasthack.vksharp.DataTypes.Enums.ContentType;

namespace FiveLoavesAndTwoFish {
    internal class Program {
        private Config _config;
        private Api _api;
        private TimeSpan _delay;
        private HashSet<Post> _posts;
        private CultureInfo _formatProvider;
        private HttpClient _client;
        private List<Item> _feedItems;
        public static string Rss { get; private set; }

        private static void Main(string[] args) => new Program().MainAsync( args ).Wait();

        private class PostEqualityComparer : IEqualityComparer<Post> {
            public bool Equals(Post x, Post y) => x.Id == y.Id && x.OwnerId == y.OwnerId;
            public int GetHashCode(Post obj) => $"{obj.Id}_{obj.OwnerId}".GetHashCode();
        }

        private async Task MainAsync(string[] args) {
            Console.WriteLine( "Starting up..." );
            //load config
            var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "config.json");
            _config = JsonConvert.DeserializeObject<Config>( File.ReadAllText( path ) );
            //setup 
            _feedItems = new List<Item>();
            _client = new HttpClient();
            _api = new Api();
            if ( _config.Token != null ) _api.AddToken( Token.FromRedirectUrl( _config.Token ) );
            _delay = TimeSpan.FromMinutes( _config.CheckInterval );
            _posts = new HashSet<Post>( new PostEqualityComparer() );
            _formatProvider = CultureInfo.GetCultureInfo( "ru-RU" );

            Console.WriteLine( "Building history..." );

            //fetch history
            _config.Groups = _config.Groups.Select(a =>
            {
                var v = _api.Utils.ResolveScreenNameSync(a);
                return (v.Type == ResolvedItemType.Group ? -v.ObjectId : v.ObjectId).ToString();
            }).ToArray();
            await AddPosts(_config.HistoryPosts);

            using (WebApp.Start<Startup>( _config.ListenAddress )) {
                Console.WriteLine( $"Starting RSS server on {_config.ListenAddress}" );
                while ( true ) {
                    Console.WriteLine( "Refreshing..." );
                    await AddPosts( _config.RefreshPosts );
                    Console.WriteLine( "Building feed" );

                    Rss = new Feed {
                        Title = "Import Feed",
                        Description = "Yo ho ho!",
                        Link = new Uri( "http://vk.com" ),
                        Copyright = "(c) 2017",
                        Items = _feedItems.AsEnumerable().Reverse().ToList()
                    }.Serialize();

                    Console.WriteLine( "Waiting for the next update" );
                    await Task.Delay( _delay ).ConfigureAwait( false ); 
                } 
            }
        }

        private async Task<string> BuildPostText(Post post) {
            var sb = new StringBuilder();

            sb.AppendLine( post.Text ?? "" );

            /*photos as base64*/
            foreach ( var photo in post.Attachments?.Where( a => a.Type == ContentType.Photo )?.Select( a => a.Photo ) ?? Enumerable.Empty<Photo>() ) {
                try {
                    var previewLink = photo.Photo604 ?? photo.Photo130 ?? photo.Photo75;
                    var fullLink = photo.Photo2560 ?? photo.Photo1280 ?? photo.Photo807 ?? previewLink;

                    var preview = _config.FetchPics ? Convert.ToBase64String( await _client.GetByteArrayAsync( previewLink ).ConfigureAwait( false ) ): previewLink;
                    var full = _config.FetchPics ? Convert.ToBase64String( await _client.GetByteArrayAsync( fullLink ).ConfigureAwait( false ) ) : fullLink;

                    sb.Append( "<post href=\"" );
                    if (_config.FetchPics) sb.Append("data:image;base64,");
                    sb.Append(full);
                    sb.Append( $"\"><img src=\"" );
                    if (_config.FetchPics) sb.Append("data:image;base64,");
                    sb.Append( preview );
                    sb.AppendLine( "\" /></post>" );
                }
                catch ( Exception ) { }
            }
            //links + base64 previews
            foreach ( var link in post.Attachments?.Where( a => a.Type == ContentType.Link )?.Select( a => a.Link ) ?? Enumerable.Empty<Link>() ) {
                try {
                    var preview = _config.FetchPics ? (link.ImageSrc != null ? Convert.ToBase64String( await _client.GetByteArrayAsync( link.ImageSrc ).ConfigureAwait( false ) ) : "") : link.ImageSrc ?? "";
                    sb.AppendFormat( "<div class=\"link\"><post href=\"{0}\"<div class=\"link_title\">{1}</div><div class=\"link_descr\">{2}</div><img src=\"", link.Url, WebUtility.HtmlEncode( link.Title ), WebUtility.HtmlEncode( link.Description ) );
                    if (_config.FetchPics) sb.Append("data:image;base64,");
                    sb.Append( preview );
                    sb.AppendLine( "\"/></post></div>" );
                }
                catch ( Exception ) { }
            }
            /*videos*/
            foreach ( var video in post.Attachments?.Where( a => a.Type == ContentType.Video )?.Select( a => a.Video ) ?? Enumerable.Empty<Video>() ) {
                try {
                    sb.AppendLine( $"<iframe src=\"//vk.com/video_ext.php?oid={video.OwnerId}&id={video.Id}&hash={video.AccessKey}&hd=2\" width=853 height=480 frameborder=0 allowfullscreen></iframe>" ); //
                }
                catch ( Exception ) { }
            }
            return sb.ToString();
        }

        private async Task AddPosts(int count) {
            const int maxChunkSize = 100;
            var newPosts = new HashSet<Post>();
            /*download posts + find new*/
            Task t = Task.FromResult(1);
            foreach ( var group in _config.Groups ) {
                int i = 0, chunk = Math.Min( maxChunkSize, count - i );
                for ( i = 0; i < count; chunk = Math.Min( maxChunkSize, count - i ), i += chunk ) {
                    try {
                        await t;
                        t = Task.Delay(300);
                        var resp = await _api.Wall.Get(ownerId: int.Parse(@group)/*terrible but fuck that*/, offset: i, count: chunk , filter: _config.AdminOnly ? WallPostFilter.Owner : WallPostFilter.All).ConfigureAwait( false );
                        if ( !resp.Items.Any() ) break;
                        newPosts.UnionWith( resp.Items.Where( a => !_posts.Contains( a ) ) );
                    }
                    catch ( Exception ex ) {
                        Console.WriteLine( ex.Message );
                    }
                }
            }
            _posts.UnionWith(newPosts);

            /*build new posts*/
            var v = newPosts.OrderBy( a=>a.Date ).Select(async a => new Item
            {
                Body = await BuildPostText(a),
                Title = $"Новость от {a.Date.LocalDateTime.ToString("f", _formatProvider)}",
                PublishDate = a.Date.LocalDateTime,
                Link = new Uri($"https://vk.com/wall{a.OwnerId}_{a.Id}"),
                Permalink = $"https://vk.com/wall{a.OwnerId}_{a.Id}",
                Author = new Author
                {
                    Email = "durov@vk.com",
                    Name = "Pavel Durov"
                }
            }).ToArray();
            try
            {
                await Task.WhenAll(v).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            _feedItems.AddRange( v.Where( a=>a.Status == TaskStatus.RanToCompletion ).Select( a=>a.Result ) );
        }
    }
}