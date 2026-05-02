using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Core;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class SeasonProvider : BaseProvider, IRemoteMetadataProvider<Season, SeasonInfo>
    {
        public SeasonProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeasonProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo info, CancellationToken cancellationToken)
        {
            this.Log("GetSeasonSearchResults of [name]: {0}", info.Name);
            return await Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
        }

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            info.SeriesProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var seriesTmdbId);
            var seasonNumber = info.IndexNumber;
            var fileName = Path.GetFileName(info.Path);
            this.Log("GetSeasonMetaData of [name]: {0} [fileName]: {1} number: {2} seriesTmdbId: {3}", info.Name, fileName, info.IndexNumber, seriesTmdbId);

            if (seasonNumber is null)
            {
                seasonNumber = this.GuessSeasonNumberByDirectoryName(info.Path);
            }

            if (string.IsNullOrWhiteSpace(seriesTmdbId) || !seasonNumber.HasValue || seasonNumber < 0)
            {
                return result;
            }

            return await this.GetMetadataByTmdb(info, seriesTmdbId, seasonNumber, cancellationToken).ConfigureAwait(false);
        }

        public async Task<MetadataResult<Season>> GetMetadataByTmdb(SeasonInfo info, string? seriesTmdbId, int? seasonNumber, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>();

            if (string.IsNullOrEmpty(seriesTmdbId))
            {
                return result;
            }

            if (seasonNumber is null or 0)
            {
                return result;
            }

            var seasonResult = await this._tmdbApi
                .GetSeasonAsync(seriesTmdbId.ToInt(), seasonNumber ?? 0, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);
            if (seasonResult == null)
            {
                this.Log("Not found season from TMDB. {0} seriesTmdbId: {1} seasonNumber: {2}", info.Name, seriesTmdbId, seasonNumber);
                return result;
            }

            result.HasMetadata = true;
            result.QueriedById = true;
            result.Item = new Season
            {
                Name = seasonResult.Name,
                IndexNumber = seasonNumber,
                Overview = seasonResult.Overview,
                PremiereDate = seasonResult.AirDate,
                ProductionYear = seasonResult.AirDate?.Year,
            };

            if (!string.IsNullOrEmpty(seasonResult.ExternalIds?.TvdbId))
            {
                result.Item.SetProviderId(MetadataProvider.Tvdb, seasonResult.ExternalIds.TvdbId);
            }

            return result;
        }
    }
}
