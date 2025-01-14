using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers
{
    [ExcludeFromCodeCoverage]
    public class TorrentLatino2 : BaseWebIndexer
    {
        private const int MaxItemsPerPage = 15;
        private const int MaxSearchPageLimit = 6; // 15 items per page * 6 pages = 90
        private string _language;
        
        public TorrentLatino2(IIndexerConfigurationService configurationService, WebClient webClient, Logger logger, IProtectionService protectionService, ICacheService cacheService)
            : base(id: "torrentlatino2",
                   name: "TorrentLatino2",
                   description: "Las Mejores Peliculas y Series Latino por Torrent GRATIS...",
                   link: "https://www.torrentlatino2.net/",
                   caps: new TorznabCapabilities {
                       MovieSearchParams = new List<MovieSearchParam> { MovieSearchParam.Q }
                   },
                   configService: configurationService,
                   client: webClient,
                   logger: logger,
                   p: protectionService,
                   cacheService: cacheService,
                   configData: new ConfigurationData())
        {
            Encoding = Encoding.UTF8;
            Language = "es-419";
            Type = "public";

            var language = new SingleSelectConfigurationItem("Select language", new Dictionary<string, string>
                {
                    {"latino", "Latin American Spanish"}
                })
            {
                Value = "latino"
            };

            configData.AddDynamic("language", language);

            AddCategoryMapping(1, TorznabCatType.MoviesHD);
        }

        public override void LoadValuesFromJson(JToken jsonConfig, bool useProtectionService = false)
        {
            base.LoadValuesFromJson(jsonConfig, useProtectionService);
            var language = (SingleSelectConfigurationItem)configData.GetDynamic("language");
            _language = language?.Value ?? "latino";
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                                    throw new Exception("Could not find release from this URL."));

            return IndexerConfigurationStatus.Completed;                        
        }
        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var templateUrl = SiteLink + "{0}";

            var maxPages = 2;

            if(!string.IsNullOrWhiteSpace(query.GetQueryString()))
            {
                templateUrl += "?s=" + WebUtilityHelpers.UrlEncode(query.GetQueryString(), Encoding.UTF8);
                maxPages = MaxSearchPageLimit;
            }

            var lastPublishDate = DateTime.Now;
            for (var page = 1; page <= maxPages; page ++)
            {
                var pageParam = page > 1 ? $"page/{page}" : "";
                var searchUrl = string.Format(templateUrl, pageParam);
                var response = await RequestWithCookiesAndRetryAsync(searchUrl);
                var pageReleases = await ParseReleases(response, query);

                foreach (var release in pageReleases)
                {
                    release.PublishDate = lastPublishDate;
                    lastPublishDate = lastPublishDate.AddMinutes(-1);
                }
                releases.AddRange(pageReleases);

                if(pageReleases.Count < MaxItemsPerPage)
                    break;
            }

            return releases;
        }

        public async Task<List<ReleaseInfo>> ParseMovie(Uri link, Uri poster, string title)
        {
            var releases = new List<ReleaseInfo>();

            var results = await RequestWithCookiesAsync(link.ToString());

            try
            {
                var parser = new HtmlParser();
                var dom = parser.ParseDocument(results.ContentString);

                // Try to search rows for 1080p if possible
                var rows = dom.QuerySelectorAll("div.TPTblCn table tbody tr");
                foreach (var row in rows)
                {
                    var quality = row.Children[4].TextContent.Trim();

                    var language = row.Children[3].TextContent.Trim();
                    
                    var releaseTitle = $"{title} - {language} {quality}"; 

                    var protectedLink = row.QuerySelector("a").GetAttribute("href");
                    protectedLink = protectedLink.Replace("1/#", "op/?");

                    results = await RequestWithCookiesAndRetryAsync(protectedLink);
                    var torrentUrl = new Uri(results.RedirectingTo);

                    var release = new ReleaseInfo
                    {
                        Title = releaseTitle,
                        Link = torrentUrl,
                        Details = link,
                        Guid = link,
                        Category = new List<int> { TorznabCatType.MoviesHD.ID },
                        Poster = poster,
                        Size = 2147483648, // 2 GB
                        Files = 1,
                        Seeders = 1,
                        Peers = 2,
                        DownloadVolumeFactor = 0,
                        UploadVolumeFactor = 1
                    };

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.ContentString, ex);
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> ParseReleases(WebResult response, TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            try
            {
                var parser = new HtmlParser();
                var dom = parser.ParseDocument(response.ContentString);

                // 1st level: Per Movie
                var rows = dom.QuerySelectorAll("li.TPostMv");
                foreach (var row in rows)
                {
                    var qImg = row.QuerySelector("img");
                    if (qImg == null)
                        continue; // skip results without image

                    var title = row.QuerySelector("div.Title").TextContent;
                    if (!CheckTitleMatchWords(query.GetQueryString(), title))
                        continue; // skip if it doesn't contain all words
                    
                    var poster = new Uri(GetAbsoluteUrl(qImg.GetAttribute("src")));
                    var href = row.QuerySelector("article a").GetAttribute("href");
                    var movieLink = new Uri(GetAbsoluteUrl(href));

                    var movieReleases = await ParseMovie(movieLink, poster, title);

                    releases.AddRange(movieReleases);
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.ContentString, ex);
            }

            return releases;
        }

        private static bool CheckTitleMatchWords(string queryStr, string title)
        {
            // this code split the words, remove words with 2 letters or less, remove accents and lowercase
            var queryMatches = Regex.Matches(queryStr, @"\b[\w']*\b");
            var queryWords = from m in queryMatches.Cast<Match>()
                             where !string.IsNullOrEmpty(m.Value) && m.Value.Length > 2
                             select Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-8").GetBytes(m.Value.ToLower()));

            var titleMatches = Regex.Matches(title, @"\b[\w']*\b");
            var titleWords = from m in titleMatches.Cast<Match>()
                             where !string.IsNullOrEmpty(m.Value) && m.Value.Length > 2
                             select Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-8").GetBytes(m.Value.ToLower()));
            titleWords = titleWords.ToArray();

            return queryWords.All(word => titleWords.Contains(word));
        }

        private string GetAbsoluteUrl(string url)
        {
            url = url.Trim();
            if (!url.StartsWith("http"))
                return SiteLink + url.TrimStart('/');
            return url;
        }
    }
}