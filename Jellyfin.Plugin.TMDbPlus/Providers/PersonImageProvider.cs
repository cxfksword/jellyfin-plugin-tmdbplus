using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class PersonImageProvider : BaseProvider, IRemoteImageProvider
    {
        public PersonImageProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<PersonImageProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public bool Supports(BaseItem item) => item is Person;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();
            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrEmpty(tmdbId))
            {
                this.Log("Got images failed because tmdb id of \"{0}\" is empty!", item.Name);
                return list;
            }

            var person = await this._tmdbApi.GetPersonAsync(tmdbId.ToInt(), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(person?.ProfilePath))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = this.Name,
                    Url = this._tmdbApi.GetProfileUrl(person.ProfilePath),
                    Type = ImageType.Primary,
                });
            }

            return list;
        }
    }
}
