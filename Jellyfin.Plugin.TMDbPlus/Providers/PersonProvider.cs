using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class PersonProvider : BaseProvider, IRemoteMetadataProvider<Person, PersonLookupInfo>
    {
        public PersonProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<PersonProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(PersonLookupInfo searchInfo, CancellationToken cancellationToken)
        {
            this.Log("GetPersonSearchResults of [name]: {0}", searchInfo.Name);
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                return result;
            }

            var res = await this._tmdbApi.SearchPersonAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(Configuration.PluginConfiguration.MAX_SEARCH_RESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    SearchProviderName = TmdbProviderName,
                    ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) } },
                    ImageUrl = this._tmdbApi.GetProfileUrl(x.ProfilePath),
                    Name = x.Name,
                };
            }));

            return result;
        }

        public async Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>();
            var personTmdbId = info.GetProviderId(MetadataProvider.Tmdb);

            if (string.IsNullOrEmpty(personTmdbId) && !string.IsNullOrWhiteSpace(info.Name))
            {
                var list = await this._tmdbApi.SearchPersonAsync(info.Name, cancellationToken).ConfigureAwait(false);
                personTmdbId = list.FirstOrDefault()?.Id.ToString(CultureInfo.InvariantCulture);
            }

            this.Log("GetPersonMetadata of [personTmdbId]: {0}", personTmdbId);
            if (string.IsNullOrEmpty(personTmdbId))
            {
                return result;
            }

            return await this.GetMetadataByTmdb(personTmdbId.ToInt(), cancellationToken).ConfigureAwait(false);
        }

        private async Task<MetadataResult<Person>> GetMetadataByTmdb(int personTmdbId, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>();
            var person = await this._tmdbApi.GetPersonAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
            if (person == null)
            {
                return result;
            }

            var item = new Person
            {
                HomePageUrl = person.Homepage,
                Overview = person.Biography,
                PremiereDate = person.Birthday?.ToUniversalTime(),
                EndDate = person.Deathday?.ToUniversalTime(),
            };

            if (!string.IsNullOrWhiteSpace(person.PlaceOfBirth))
            {
                item.ProductionLocations = new[] { person.PlaceOfBirth };
            }

            item.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(person.ImdbId))
            {
                item.SetProviderId(MetadataProvider.Imdb, person.ImdbId);
            }

            result.HasMetadata = true;
            result.QueriedById = true;
            result.Item = item;
            return result;
        }
    }
}
