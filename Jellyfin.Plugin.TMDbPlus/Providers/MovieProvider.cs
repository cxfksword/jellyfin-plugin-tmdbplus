using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Core;
using Jellyfin.Plugin.TMDbPlus.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.Search;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>
    {
        public MovieProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<MovieProvider>(), libraryManager, httpContextAccessor, tmdbApi, aiTranslationApi)
        {
        }

        public string Name => Plugin.PluginName;

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info, CancellationToken cancellationToken)
        {
            this.Log("GetSearchResults of [name]: {0}", info.Name);
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(info.Name))
            {
                return result;
            }

            var tmdbList = await this._tmdbApi.SearchMovieAsync(info.Name, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
            result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAX_SEARCH_RESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) } },
                    Name = string.Format(CultureInfo.InvariantCulture, "{0}", x.Title ?? x.OriginalTitle),
                    ImageUrl = this._tmdbApi.GetPosterUrl(x.PosterPath),
                    Overview = x.Overview,
                    ProductionYear = x.ReleaseDate?.Year,
                };
            }));

            return result;
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var fileName = this.GetOriginalFileName(info);
            var result = new MetadataResult<Movie>();

            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            this.Log("GetMovieMetadata of [name]: {0} [fileName]: {1} [tmdbId]: {2}", info.Name, fileName, tmdbId);
            if (string.IsNullOrEmpty(tmdbId))
            {
                var extraResult = this.HandleExtraType(info);
                if (extraResult != null)
                {
                    return extraResult;
                }

                tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(tmdbId))
            {
                this.Log("Match failed for movie [name]: {0} [year]: {1}", info.Name, info.Year);
                return result;
            }

            return await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
        }

        private async Task<MetadataResult<Movie>> GetMetadataByTmdb(string tmdbId, MovieInfo info, CancellationToken cancellationToken)
        {
            this.Log("GetMovieMetadata of tmdb [id]: {0}", tmdbId);
            var result = new MetadataResult<Movie>();
            var movieResult = await this._tmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);

            if (movieResult == null)
            {
                return result;
            }

            var movie = new Movie
            {
                Name = movieResult.Title ?? movieResult.OriginalTitle,
                OriginalTitle = movieResult.OriginalTitle,
                Overview = movieResult.Overview?.Replace("\n\n", "\n", StringComparison.InvariantCulture),
                Tagline = movieResult.Tagline,
                ProductionLocations = movieResult.ProductionCountries.Select(pc => pc.Name).ToArray(),
            };
            result = new MetadataResult<Movie>
            {
                QueriedById = true,
                HasMetadata = true,
                ResultLanguage = info.MetadataLanguage,
                Item = movie,
            };

            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            movie.SetProviderId(MetadataProvider.Imdb, movieResult.ImdbId);

            if (movieResult.BelongsToCollection != null)
            {
                movie.CollectionName = movieResult.BelongsToCollection.Name;
            }

            movie.CommunityRating = (float)System.Math.Round(movieResult.VoteAverage, 2);
            movie.OfficialRating = this.GetTmdbOfficialRatingByData(movieResult, info.MetadataCountryCode);
            movie.PremiereDate = movieResult.ReleaseDate;
            movie.ProductionYear = movieResult.ReleaseDate?.Year;

            if (movieResult.ProductionCompanies != null)
            {
                movie.SetStudios(movieResult.ProductionCompanies.Select(c => c.Name));
            }

            foreach (var genre in movieResult.Genres.Select(g => g.Name))
            {
                movie.AddGenre(genre);
            }

            var persons = GetPersons(movieResult).ToList();
            persons = await TranslateCharacterRolesAsync(persons, movie.Name ?? string.Empty, movie.ProductionYear, cancellationToken).ConfigureAwait(false);
            foreach (var person in persons)
            {
                result.AddPerson(person);
            }

            return result;
        }

        private MetadataResult<Movie>? HandleExtraType(MovieInfo info)
        {
            var fileName = Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
            var parseResult = NameParser.Parse(fileName);
            if (parseResult.IsExtra)
            {
                this.Log("Found extra of [name]: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            if (NameParser.IsSpecialDirectory(info.Path) || NameParser.IsExtraDirectory(info.Path))
            {
                this.Log("Found extra of [name]: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            return null;
        }

        private IEnumerable<PersonInfo> GetPersons(TMDbLib.Objects.Movies.Movie item)
        {
            if (item.Credits?.Cast != null)
            {
                foreach (var actor in item.Credits.Cast.OrderBy(a => a.Order).Take(Configuration.PluginConfiguration.MAX_CAST_MEMBERS))
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

            if (item.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer,
                };

                foreach (var person in item.Credits.Crew)
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

        private string? GetTmdbOfficialRatingByData(TMDbLib.Objects.Movies.Movie? movieResult, string preferredCountryCode)
        {
            if (movieResult == null || movieResult.Releases?.Countries == null)
            {
                return null;
            }

            var releases = movieResult.Releases.Countries.Where(i => !string.IsNullOrWhiteSpace(i.Certification)).ToList();

            var ourRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
            var usRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
            var minimumRelease = releases.FirstOrDefault();

            if (ourRelease != null)
            {
                var ratingPrefix = string.Equals(preferredCountryCode, "us", StringComparison.OrdinalIgnoreCase) ? string.Empty : preferredCountryCode + "-";
                var newRating = ratingPrefix + ourRelease.Certification;
                newRating = newRating.Replace("de-", "FSK-", StringComparison.OrdinalIgnoreCase);
                return newRating;
            }

            if (usRelease != null)
            {
                return usRelease.Certification;
            }

            if (minimumRelease != null)
            {
                return minimumRelease.Certification;
            }

            return null;
        }
    }
}
