using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.TvShows;
using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class SeriesProvider : BaseProvider, IRemoteMetadataProvider<Series, SeriesInfo>
    {
        public SeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo info, CancellationToken cancellationToken)
        {
            this.Log("GetSearchResults of [name]: {0}", info.Name);
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(info.Name))
            {
                return result;
            }

            var tmdbList = await this._tmdbApi.SearchSeriesAsync(info.Name, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAX_SEARCH_RESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) } },
                    Name = string.Format(CultureInfo.InvariantCulture, "{0}", x.Name ?? x.OriginalName),
                    ImageUrl = this._tmdbApi.GetPosterUrl(x.PosterPath),
                    Overview = x.Overview,
                    ProductionYear = x.FirstAirDate?.Year,
                };
            }));

            return result;
        }

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var fileName = this.GetOriginalFileName(info);
            var result = new MetadataResult<Series>();

            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            this.Log("GetSeriesMetadata of [name]: {0} [fileName]: {1} [tmdbId]: {2}", info.Name, fileName, tmdbId);
            if (string.IsNullOrEmpty(tmdbId))
            {
                tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(tmdbId))
            {
                this.Log("Match failed for series [name]: {0} [year]: {1}", info.Name, info.Year);
                return result;
            }

            return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
        }

        private async Task<MetadataResult<Series>> GetMetadataByTmdb(string tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();
            this.Log("GetSeriesMetadata of tmdb [id]: {0}", tmdbId);
            var tvShow = await this._tmdbApi
                .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (tvShow == null)
            {
                return result;
            }

            result = new MetadataResult<Series>
            {
                Item = MapTvShowToSeries(tvShow, info.MetadataCountryCode),
                ResultLanguage = info.MetadataLanguage ?? tvShow.OriginalLanguage,
                QueriedById = true,
                HasMetadata = true,
            };

            var persons = GetPersons(tvShow).ToList();
            persons = await TranslateCharacterRolesAsync(persons, result.Item.Name ?? string.Empty, result.Item.ProductionYear, cancellationToken).ConfigureAwait(false);
            foreach (var person in persons)
            {
                result.AddPerson(person);
            }

            return result;
        }

        private string? GetTmdbOfficialRatingByData(TvShow? tvShow, string preferredCountryCode)
        {
            if (tvShow != null)
            {
                var contentRatings = tvShow.ContentRatings.Results ?? new List<ContentRating>();

                var ourRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
                var usRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
                var minimumRelease = contentRatings.FirstOrDefault();

                if (ourRelease != null)
                {
                    return ourRelease.Rating;
                }

                if (usRelease != null)
                {
                    return usRelease.Rating;
                }

                if (minimumRelease != null)
                {
                    return minimumRelease.Rating;
                }
            }

            return null;
        }

        private Series MapTvShowToSeries(TvShow seriesResult, string preferredCountryCode)
        {
            var series = new Series
            {
                Name = seriesResult.Name,
                OriginalTitle = seriesResult.OriginalName,
            };

            series.SetProviderId(MetadataProvider.Tmdb, seriesResult.Id.ToString(CultureInfo.InvariantCulture));

            series.CommunityRating = (float)System.Math.Round(seriesResult.VoteAverage, 2);
            series.Overview = seriesResult.Overview;

            if (seriesResult.Networks != null)
            {
                series.Studios = seriesResult.Networks.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Genres != null)
            {
                series.Genres = seriesResult.Genres.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Keywords?.Results != null)
            {
                for (var i = 0; i < seriesResult.Keywords.Results.Count; i++)
                {
                    series.AddTag(seriesResult.Keywords.Results[i].Name);
                }
            }

            series.HomePageUrl = seriesResult.Homepage;
            series.RunTimeTicks = seriesResult.EpisodeRunTime.Select(i => TimeSpan.FromMinutes(i).Ticks).FirstOrDefault();

            if (string.Equals(seriesResult.Status, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                series.Status = SeriesStatus.Ended;
                series.EndDate = seriesResult.LastAirDate;
            }
            else
            {
                series.Status = SeriesStatus.Continuing;
            }

            series.PremiereDate = seriesResult.FirstAirDate;
            series.ProductionYear = seriesResult.FirstAirDate?.Year;

            var ids = seriesResult.ExternalIds;
            if (ids != null)
            {
                if (!string.IsNullOrWhiteSpace(ids.ImdbId))
                {
                    series.SetProviderId(MetadataProvider.Imdb, ids.ImdbId);
                }

                if (!string.IsNullOrEmpty(ids.TvrageId))
                {
                    series.SetProviderId(MetadataProvider.TvRage, ids.TvrageId);
                }

                if (!string.IsNullOrEmpty(ids.TvdbId))
                {
                    series.SetProviderId(MetadataProvider.Tvdb, ids.TvdbId);
                }
            }

            series.OfficialRating = this.GetTmdbOfficialRatingByData(seriesResult, preferredCountryCode);
            return series;
        }

        private IEnumerable<PersonInfo> GetPersons(TvShow seriesResult)
        {
            if (seriesResult.Credits?.Cast != null)
            {
                foreach (var actor in seriesResult.Credits.Cast.OrderBy(a => a.Order).Take(Configuration.PluginConfiguration.MAX_CAST_MEMBERS))
                {
                    var personInfo = new PersonInfo
                    {
                        Name = actor.Name.Trim(),
                        Role = actor.Character,
                        Type = PersonKind.Actor,
                        SortOrder = actor.Order,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this._tmdbApi.GetProfileUrl(actor.ProfilePath);
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    yield return personInfo;
                }
            }

            if (seriesResult.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer,
                };

                foreach (var person in seriesResult.Credits.Crew)
                {
                    var type = MapCrewToPersonType(person);

                    if (!keepTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                        && !keepTypes.Contains(person.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = person.Name.Trim(),
                        Role = person.Job,
                        Type = type == PersonType.Director ? PersonKind.Director : (type == PersonType.Producer ? PersonKind.Producer : PersonKind.Actor),
                    };

                    if (!string.IsNullOrWhiteSpace(person.ProfilePath))
                    {
                        personInfo.ImageUrl = this._tmdbApi.GetPosterUrl(person.ProfilePath);
                    }

                    if (person.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    yield return personInfo;
                }
            }
        }
    }
}
