using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class SeriesImageProvider : BaseProvider, IRemoteImageProvider
    {
        public SeriesImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesImageProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public bool Supports(BaseItem item) => item is Series;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new List<ImageType>
        {
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Logo,
        };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            this.Log("GetImages for item: {0} tmdbId: {1}", item.Name, tmdbId);
            if (string.IsNullOrEmpty(tmdbId))
            {
                return new List<RemoteImageInfo>();
            }

            var language = item.GetPreferredMetadataLanguage();
            var series = await this._tmdbApi.GetSeriesAsync(tmdbId.ToInt(), language, language, cancellationToken).ConfigureAwait(false);
            var images = await this._tmdbApi.GetSeriesImagesAsync(tmdbId.ToInt(), string.Empty, string.Empty, cancellationToken).ConfigureAwait(false);

            if (series == null || images == null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var remoteImages = new List<RemoteImageInfo>();
            remoteImages.AddRange(images.Posters.Where(x => x.FilePath == series.PosterPath).Select(x => new RemoteImageInfo {
                ProviderName = this.Name,
                Url = this._tmdbApi.GetPosterUrl(x.FilePath),
                Type = ImageType.Primary,
                CommunityRating = x.VoteAverage,
                VoteCount = x.VoteCount,
                Width = x.Width,
                Height = x.Height,
                Language = language,
                RatingType = RatingType.Score,
            }));

            remoteImages.AddRange(images.Backdrops.Where(x => x.FilePath == series.BackdropPath).Select(x => new RemoteImageInfo {
                ProviderName = this.Name,
                Url = this._tmdbApi.GetBackdropUrl(x.FilePath),
                Type = ImageType.Backdrop,
                CommunityRating = x.VoteAverage,
                VoteCount = x.VoteCount,
                Width = x.Width,
                Height = x.Height,
                Language = language,
                RatingType = RatingType.Score,
            }));

            remoteImages.AddRange(images.Logos.Select(x => new RemoteImageInfo {
                ProviderName = this.Name,
                Url = this._tmdbApi.GetLogoUrl(x.FilePath),
                Type = ImageType.Logo,
                CommunityRating = x.VoteAverage,
                VoteCount = x.VoteCount,
                Width = x.Width,
                Height = x.Height,
                Language = this.AdjustImageLanguage(x.Iso_639_1, language),
                RatingType = RatingType.Score,
            }));

            return remoteImages.OrderByLanguageDescending(language);
        }
    }
}
