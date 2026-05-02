using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TMDbPlus.Api;
using Jellyfin.Plugin.TMDbPlus.Configuration;
using Jellyfin.Plugin.TMDbPlus.Core;
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
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Languages;

namespace Jellyfin.Plugin.TMDbPlus.Providers
{
    public abstract class BaseProvider
    {
        public const string TmdbProviderName = "TheMovieDb";

        protected readonly ILogger _logger;
        protected readonly IHttpClientFactory _httpClientFactory;
        protected readonly TmdbApi _tmdbApi;
        protected readonly ILibraryManager _libraryManager;
        protected readonly IHttpContextAccessor _httpContextAccessor;
        protected readonly AiTranslationApi _aiTranslationApi;

        protected Regex regSeasonNameSuffix = new Regex(@"\s第[0-9一二三四五六七八九十]+?季$|\sSeason\s\d+?$|(?<![0-9a-zA-Z])\d$", RegexOptions.Compiled);
        protected Regex regTmdbIdAttribute = new Regex(@"\[(?:tmdb|tmdbid)-(\d+?)\]", RegexOptions.Compiled);

        protected PluginConfiguration config
        {
            get
            {
                return Plugin.Instance?.Configuration ?? new PluginConfiguration();
            }
        }

        protected BaseProvider(IHttpClientFactory httpClientFactory, ILogger logger, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, TmdbApi tmdbApi, AiTranslationApi aiTranslationApi)
        {
            this._tmdbApi = tmdbApi;
            this._libraryManager = libraryManager;
            this._logger = logger;
            this._httpClientFactory = httpClientFactory;
            this._httpContextAccessor = httpContextAccessor;
            this._aiTranslationApi = aiTranslationApi;
        }

        protected async Task<TMDbLib.Objects.Search.TvSeasonEpisode?> GetEpisodeAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? language, string? imageLanguages, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(displayOrder))
            {
                var group = await this._tmdbApi.GetSeriesGroupAsync(seriesTmdbId, displayOrder, language, imageLanguages, cancellationToken).ConfigureAwait(false);
                if (group != null)
                {
                    var season = group.Groups.Find(s => s.Order == seasonNumber);
                    var ep = season?.Episodes.Find(e => e.Order == episodeNumber - 1);
                    if (ep is not null)
                    {
                        var result = await this._tmdbApi
                            .GetSeasonAsync(seriesTmdbId, ep.SeasonNumber, language, imageLanguages, cancellationToken)
                            .ConfigureAwait(false);
                        if (result == null || result.Episodes == null)
                        {
                            return null;
                        }

                        if (ep.EpisodeNumber > result.Episodes.Count)
                        {
                            return null;
                        }

                        return result.Episodes[ep.EpisodeNumber - 1];
                    }
                }
            }

            var seasonResult = await this._tmdbApi
                .GetSeasonAsync(seriesTmdbId, seasonNumber.Value, language, imageLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (seasonResult == null || seasonResult.Episodes == null)
            {
                return null;
            }

            if (episodeNumber.Value > seasonResult.Episodes.Count)
            {
                return null;
            }

            return seasonResult.Episodes[episodeNumber.Value - 1];
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            this.Log("GetImageResponse url: {0}", url);
            return await this._httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        }

        protected async Task<string?> GuestByTmdbAsync(ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var fileName = GetOriginalFileName(info);
            var tmdbId = this.regTmdbIdAttribute.FirstMatchGroup(fileName);
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                this.Log("Found tmdb [id] by attr: {0}", tmdbId);
                return tmdbId;
            }

            var parseResult = NameParser.Parse(fileName);
            var searchName = !string.IsNullOrEmpty(parseResult.ChineseName) ? parseResult.ChineseName : parseResult.Name;
            info.Year = parseResult.Year;

            return await GuestByTmdbAsync(searchName, info.Year, info, cancellationToken).ConfigureAwait(false);
        }

        protected async Task<string?> GuestByTmdbAsync(string name, int? year, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            this.Log("GuestByTmdb of [name]: {0} [year]: {1}", name, year);
            switch (info)
            {
                case MovieInfo:
                    var movieResults = await this._tmdbApi.SearchMovieAsync(name, year ?? 0, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    var movieItem = movieResults.Where(x => x.Title == name || x.OriginalTitle == name).FirstOrDefault();
                    if (movieItem != null)
                    {
                        this.Log("Found tmdb [id]: {0}({1})", movieItem.Title, movieItem.Id);
                        return movieItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    movieItem = movieResults.FirstOrDefault();
                    if (movieItem != null)
                    {
                        this.Log("Found tmdb [id]: {0}({1})", movieItem.Title, movieItem.Id);
                        return movieItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
                case SeriesInfo:
                    var seriesResults = await this._tmdbApi.SearchSeriesAsync(name, info.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    var seriesItem = seriesResults.Where(x => (x.Name == name || x.OriginalName == name) && x.FirstAirDate?.Year == year).FirstOrDefault();
                    if (seriesItem != null)
                    {
                        this.Log("Found tmdb [id]: -> {0}({1})", seriesItem.Name, seriesItem.Id);
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    seriesItem = seriesResults.Where(x => x.FirstAirDate?.Year == year).FirstOrDefault();
                    if (seriesItem != null)
                    {
                        this.Log("Found tmdb [id]: -> {0}({1})", seriesItem.Name, seriesItem.Id);
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    seriesItem = seriesResults.Where(x => x.Name == name || x.OriginalName == name).FirstOrDefault();
                    if (seriesItem != null)
                    {
                        this.Log("Found tmdb [id]: -> {0}({1})", seriesItem.Name, seriesItem.Id);
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    seriesItem = seriesResults.FirstOrDefault();
                    if (seriesItem != null)
                    {
                        this.Log("Found tmdb [id]: -> {0}({1})", seriesItem.Name, seriesItem.Id);
                        return seriesItem.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
                default:
                    break;
            }

            this.Log("Not found tmdb id by [name]: {0} [year]: {1}", name, year);
            return null;
        }

        protected async Task<string?> GetTmdbIdByImdbAsync(string imdb, string language, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(imdb))
            {
                return null;
            }

            var findResult = await this._tmdbApi.FindByExternalIdAsync(imdb, TMDbLib.Objects.Find.FindExternalSource.Imdb, language, cancellationToken).ConfigureAwait(false);

            switch (info)
            {
                case MovieInfo:
                    if (findResult?.MovieResults != null && findResult.MovieResults.Count > 0)
                    {
                        var tmdbId = findResult.MovieResults[0].Id;
                        this.Log("Found tmdb [id]: {0} by imdb id: {1}", tmdbId, imdb);
                        return $"{tmdbId}";
                    }

                    break;
                case SeriesInfo:
                    if (findResult?.TvResults != null && findResult.TvResults.Count > 0)
                    {
                        var tmdbId = findResult.TvResults[0].Id;
                        this.Log("Found tmdb [id]: {0} by imdb id: {1}", tmdbId, imdb);
                        return $"{tmdbId}";
                    }

                    if (findResult?.TvEpisode != null && findResult.TvEpisode.Count > 0)
                    {
                        var tmdbId = findResult.TvEpisode[0].ShowId;
                        this.Log("Found tmdb [id]: {0} by imdb id: {1}", tmdbId, imdb);
                        return $"{tmdbId}";
                    }

                    if (findResult?.TvSeason != null && findResult.TvSeason.Count > 0)
                    {
                        var tmdbId = findResult.TvSeason[0].ShowId;
                        this.Log("Found tmdb [id]: {0} by imdb id: {1}", tmdbId, imdb);
                        return $"{tmdbId}";
                    }

                    break;
                default:
                    break;
            }

            this.Log("Not found tmdb id by imdb id: {0}", imdb);
            return null;
        }

        public int? GuessSeasonNumberByDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                this.Log("Season path is empty!");
                return null;
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var regSeason = new Regex(@"第([0-9零一二三四五六七八九]+?)(季|部)", RegexOptions.Compiled);
            var match = regSeason.Match(fileName);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber <= 0)
                {
                    seasonNumber = Utils.ChineseNumberToInt(match.Groups[1].Value) ?? 0;
                }

                if (seasonNumber > 0)
                {
                    this.Log("Found season number of filename: {0} seasonNumber: {1}", fileName, seasonNumber);
                    return seasonNumber;
                }
            }

            regSeason = new Regex(@"(?<![a-z])S(\d\d?)(?![0-9a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            match = regSeason.Match(fileName);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber > 0)
                {
                    this.Log("Found season number of filename: {0} seasonNumber: {1}", fileName, seasonNumber);
                    return seasonNumber;
                }
            }

            var seasonNameMap = new Dictionary<string, int>() {
                {@"[ ._](I|1st)[ ._]", 1},
                {@"[ ._](II|2nd)[ ._]", 2},
                {@"[ ._](III|3rd)[ ._]", 3},
                {@"[ ._](IIII|4th)[ ._]", 3},
            };

            foreach (var entry in seasonNameMap)
            {
                if (Regex.IsMatch(fileName, entry.Key))
                {
                    this.Log("Found season number of filename: {0} seasonNumber: {1}", fileName, entry.Value);
                    return entry.Value;
                }
            }

            return null;
        }

        public int? ParseChineseSeasonNumberByName(string name)
        {
            var regSeason = new Regex(@"\s第([0-9零一二三四五六七八九]+?)(季|部)", RegexOptions.Compiled);
            var match = regSeason.Match(name);
            if (match.Success && match.Groups.Count > 1)
            {
                var seasonNumber = match.Groups[1].Value.ToInt();
                if (seasonNumber <= 0)
                {
                    seasonNumber = Utils.ChineseNumberToInt(match.Groups[1].Value) ?? 0;
                }

                if (seasonNumber > 0)
                {
                    return seasonNumber;
                }
            }

            return null;
        }

        protected void Log(string? message, params object?[] args)
        {
            this._logger.LogInformation($"[TMDbPlus] {message}", args);
        }

        protected string AdjustImageLanguage(string imageLanguage, string requestLanguage)
        {
            if (!string.IsNullOrEmpty(imageLanguage)
                && !string.IsNullOrEmpty(requestLanguage)
                && requestLanguage.Length > 2
                && imageLanguage.Length == 2
                && requestLanguage.StartsWith(imageLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return requestLanguage;
            }

            return imageLanguage;
        }

        protected List<RemoteImageInfo> AdjustImageLanguagePriority(IList<RemoteImageInfo> images, string preferLanguage, string alternativeLanguage)
        {
            var imagesOrdered = images.OrderByLanguageDescending(preferLanguage, alternativeLanguage).ToList();

            if (alternativeLanguage == "ja" && imagesOrdered.Where(x => x.Language == preferLanguage).Count() == 0)
            {
                var idx = imagesOrdered.FindIndex(x => x.Language == alternativeLanguage);
                if (idx >= 0)
                {
                    imagesOrdered[idx].Language = null;
                }
            }

            return imagesOrdered;
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1309: Use ordinal StringComparison", Justification = "AFAIK we WANT InvariantCulture comparisons here and not Ordinal")]
        public string MapCrewToPersonType(Crew crew)
        {
            if (crew.Department.Equals("production", StringComparison.InvariantCultureIgnoreCase)
                && crew.Job.Contains("director", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Director;
            }

            if (crew.Department.Equals("production", StringComparison.InvariantCultureIgnoreCase)
                && crew.Job.Contains("producer", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Producer;
            }

            if (crew.Department.Equals("writing", StringComparison.InvariantCultureIgnoreCase))
            {
                return PersonType.Writer;
            }

            return string.Empty;
        }

        protected string GetOriginalFileName(ItemLookupInfo info)
        {
            switch (info)
            {
                case MovieInfo:
                    var directoryName = Path.GetFileName(Path.GetDirectoryName(info.Path));
                    if (!string.IsNullOrEmpty(directoryName) && directoryName.Contains(info.Name))
                    {
                        return directoryName;
                    }

                    return Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
                case EpisodeInfo:
                    return Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
                default:
                    return Path.GetFileName(info.Path) ?? info.Name;
            }
        }

        protected string GetImageLanguageParam(string preferredLanguage, string? originalLanguage = null)
        {
            var languageCodeMap = new Dictionary<string, string>() {
                { "法语", "fr" },
                { "德语", "de" },
                { "日语", "ja" },
                { "俄语", "ru" },
                { "韩语", "ko" },
                { "泰语", "th" },
            };
            if (!string.IsNullOrEmpty(originalLanguage))
            {
                if (languageCodeMap.TryGetValue(originalLanguage, out var lang) && lang != preferredLanguage)
                {
                    return $"{preferredLanguage},{lang}";
                }
            }

            return preferredLanguage;
        }

        protected string? GetOriginalSeasonPath(EpisodeInfo info)
        {
            if (info.Path == null)
            {
                return null;
            }

            var seasonPath = Path.GetDirectoryName(info.Path);
            var item = this._libraryManager.FindByPath(seasonPath, true);
            if (item is Series)
            {
                return null;
            }

            return seasonPath;
        }

        protected bool IsVirtualSeason(EpisodeInfo info)
        {
            if (info.Path == null)
            {
                return false;
            }

            var seasonPath = Path.GetDirectoryName(info.Path);
            var parent = this._libraryManager.FindByPath(seasonPath, true);
            if (parent is Series)
            {
                return true;
            }

            var seriesPath = Path.GetDirectoryName(seasonPath);
            var series = this._libraryManager.FindByPath(seriesPath, true);
            if (series is Series && parent is not Season)
            {
                return true;
            }

            return false;
        }

        protected string RemoveSeasonSuffix(string name)
        {
            return regSeasonNameSuffix.Replace(name, "");
        }

        protected async Task<List<PersonInfo>> TranslateCharacterRolesAsync(
            List<PersonInfo> persons,
            string title,
            int? year,
            CancellationToken cancellationToken)
        {
            if (!config.EnableAiTranslateCharacter)
            {
                return persons;
            }

            var actorsWithRole = persons
                .Where(p => p.Type == PersonKind.Actor && !string.IsNullOrWhiteSpace(p.Role))
                .ToList();

            if (actorsWithRole.Count == 0)
            {
                return persons;
            }

            var roleNames = actorsWithRole.Select(p => p.Role!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var translations = await _aiTranslationApi.TranslateCharacterNamesAsync(title, year, roleNames, cancellationToken).ConfigureAwait(false);

            if (translations.Count == 0)
            {
                return persons;
            }

            foreach (var person in actorsWithRole)
            {
                if (!string.IsNullOrWhiteSpace(person.Role) && translations.TryGetValue(person.Role, out var chineseName))
                {
                    person.Role = chineseName;
                }
            }

            return persons;
        }
    }
}
