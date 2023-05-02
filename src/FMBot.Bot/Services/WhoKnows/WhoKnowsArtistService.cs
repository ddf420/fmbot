using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Dapper;
using Discord;
using Discord.Commands;
using FMBot.Bot.Models;
using FMBot.Domain.Models;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FMBot.Bot.Services.WhoKnows;

public class WhoKnowsArtistService
{
    private readonly IMemoryCache _cache;
    private readonly IDbContextFactory<FMBotDbContext> _contextFactory;
    private readonly BotSettings _botSettings;
    private readonly GenreService _genreService;
    private readonly CountryService _countryService;

    public WhoKnowsArtistService(IMemoryCache cache, IDbContextFactory<FMBotDbContext> contextFactory, IOptions<BotSettings> botSettings, GenreService genreService, CountryService countryService)
    {
        this._cache = cache;
        this._contextFactory = contextFactory;
        this._genreService = genreService;
        this._countryService = countryService;
        this._botSettings = botSettings.Value;
    }

    public async Task<IList<WhoKnowsObjectWithUser>> GetIndexedUsersForArtist(IGuild discordGuild, int guildId, string artistName)
    {
        const string sql = "SELECT ua.user_id, " +
                           "ua.name, " +
                           "ua.playcount, " +
                           "u.user_name_last_fm, " +
                           "u.discord_user_id, " +
                           "u.last_used, " +
                           "gu.user_name, " +
                           "gu.who_knows_whitelisted " +
                           "FROM user_artists AS ua " +
                           "FULL OUTER JOIN users AS u ON ua.user_id = u.user_id " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = ua.user_id " +
                           "WHERE gu.guild_id = @guildId AND UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT)) " +
                           "ORDER BY ua.playcount DESC ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var userArtists = (await connection.QueryAsync<WhoKnowsArtistDto>(sql, new
        {
            guildId,
            artistName
        })).ToList();

        var whoKnowsArtistList = new List<WhoKnowsObjectWithUser>();

        for (var i = 0; i < userArtists.Count; i++)
        {
            var userArtist = userArtists[i];

            var userName = userArtist.UserName ?? userArtist.UserNameLastFm;

            if (i < 15)
            {
                var discordUser = await discordGuild.GetUserAsync(userArtist.DiscordUserId, CacheMode.CacheOnly);
                if (discordUser != null)
                {
                    userName = discordUser.Nickname ?? discordUser.Username;
                }
            }

            whoKnowsArtistList.Add(new WhoKnowsObjectWithUser
            {
                Name = userArtist.Name,
                DiscordName = userName,
                Playcount = userArtist.Playcount,
                LastFMUsername = userArtist.UserNameLastFm,
                UserId = userArtist.UserId,
                LastUsed = userArtist.LastUsed,
                WhoKnowsWhitelisted = userArtist.WhoKnowsWhitelisted,
            });
        }

        return whoKnowsArtistList;
    }

    public static async Task<IList<WhoKnowsObjectWithUser>> GetBasicUsersForArtist(NpgsqlConnection connection, int guildId, string artistName)
    {
        const string sql = "SELECT ua.user_id, " +
                           "ua.playcount " +
                           "FROM user_artists AS ua " +
                           "FULL OUTER JOIN users AS u ON ua.user_id = u.user_id " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = ua.user_id " +
                           "INNER JOIN guilds AS guild ON guild.guild_id = @guildId " +
                           "WHERE gu.guild_id = @guildId AND UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT))  " +
                           "AND NOT ua.user_id = ANY(SELECT user_id FROM guild_blocked_users WHERE blocked_from_who_knows = true AND guild_id = @guildId) " +
                           "AND (gu.who_knows_whitelisted OR gu.who_knows_whitelisted IS NULL) " +
                           "AND (guild.activity_threshold_days IS NULL OR u.last_used IS NOT NULL AND u.last_used > now()::DATE - guild.activity_threshold_days) " +
                           "ORDER BY ua.playcount DESC ";

        var userArtists = (await connection.QueryAsync<WhoKnowsArtistDto>(sql, new
        {
            guildId,
            artistName
        })).ToList();

        return userArtists.Select(s => new WhoKnowsObjectWithUser
        {
            UserId = s.UserId,
            Playcount = s.Playcount
        }).ToList();
    }

    public async Task<IList<WhoKnowsObjectWithUser>> GetGlobalUsersForArtists(IGuild discordGuild, string artistName)
    {
        const string sql = "SELECT * " +
                           "FROM (SELECT DISTINCT ON(UPPER(u.user_name_last_fm)) " +
                           "ua.user_id, " +
                           "ua.name, " +
                           "ua.playcount, " +
                           "u.user_name_last_fm, " +
                           "u.discord_user_id, " +
                           "u.registered_last_fm, " +
                           "u.privacy_level " +
                           "FROM user_artists AS ua " +
                           "FULL OUTER JOIN users AS u ON ua.user_id = u.user_id " +
                           "WHERE UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT)) " +
                           "ORDER BY UPPER(u.user_name_last_fm) DESC, ua.playcount DESC) ua " +
                           "ORDER BY playcount DESC";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var userArtists = (await connection.QueryAsync<WhoKnowsGlobalArtistDto>(sql, new
        {
            artistName
        })).ToList();

        var whoKnowsArtistList = new List<WhoKnowsObjectWithUser>();

        for (var i = 0; i < userArtists.Count; i++)
        {
            var userArtist = userArtists[i];

            var userName = userArtist.UserNameLastFm;

            if (i < 15)
            {
                if (discordGuild != null)
                {
                    var discordUser = await discordGuild.GetUserAsync(userArtist.DiscordUserId, CacheMode.CacheOnly);
                    if (discordUser != null)
                    {
                        userName = discordUser.Nickname ?? discordUser.Username;
                    }
                }
            }

            whoKnowsArtistList.Add(new WhoKnowsObjectWithUser
            {
                Name = userArtist.Name,
                DiscordName = userName,
                Playcount = userArtist.Playcount,
                LastFMUsername = userArtist.UserNameLastFm,
                UserId = userArtist.UserId,
                RegisteredLastFm = userArtist.RegisteredLastFm,
                PrivacyLevel = userArtist.PrivacyLevel
            });
        }

        return whoKnowsArtistList;
    }

    public static async Task<IList<WhoKnowsObjectWithUser>> GetBasicGlobalUsersForArtists(NpgsqlConnection connection, string artistName)
    {
        const string sql = "SELECT * " +
                           "FROM (SELECT DISTINCT ON(UPPER(u.user_name_last_fm)) " +
                           "ua.user_id, " +
                           "ua.playcount " +
                           "FROM user_artists AS ua " +
                           "FULL OUTER JOIN users AS u ON ua.user_id = u.user_id " +
                           "WHERE UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT)) " +
                           "AND NOT UPPER(u.user_name_last_fm) = ANY(SELECT UPPER(user_name_last_fm) FROM botted_users WHERE ban_active = true) " +
                           "ORDER BY UPPER(u.user_name_last_fm) DESC, ua.playcount DESC) ua " +
                           "ORDER BY playcount DESC";

        var userArtists = (await connection.QueryAsync<WhoKnowsGlobalArtistDto>(sql, new
        {
            artistName
        })).ToList();

        return userArtists.Select(s => new WhoKnowsObjectWithUser
        {
            UserId = s.UserId,
            Playcount = s.Playcount
        }).ToList();
    }

    public async Task<IList<WhoKnowsObjectWithUser>> GetFriendUsersForArtists(IGuild discordGuild, int guildId, int userId, string artistName)
    {
        const string sql = "SELECT * " +
                           "FROM (SELECT DISTINCT ON(UPPER(u.user_name_last_fm)) " +
                           "ua.user_id, " +
                           "ua.name, " +
                           "ua.playcount, " +
                           "u.user_name_last_fm, " +
                           "u.discord_user_id, " +
                           "gu.user_name, " +
                           "gu.who_knows_whitelisted " +
                           "FROM user_artists AS ua " +
                           "FULL OUTER JOIN users AS u ON ua.user_id = u.user_id " +
                           "INNER JOIN friends AS fr ON fr.friend_user_id = ua.user_id " +
                           "LEFT JOIN guild_users AS gu ON gu.user_id = u.user_id AND gu.guild_id = @guildId " +
                           "WHERE fr.user_id = @userId AND UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT)) " +
                           "ORDER BY UPPER(u.user_name_last_fm) DESC, ua.playcount DESC) ua " +
                           "ORDER BY playcount DESC";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var userArtists = (await connection.QueryAsync<WhoKnowsArtistDto>(sql, new
        {
            artistName,
            guildId,
            userId
        })).ToList();

        var whoKnowsArtistList = new List<WhoKnowsObjectWithUser>();

        foreach (var userArtist in userArtists)
        {
            var userName = userArtist.UserName ?? userArtist.UserNameLastFm;

            if (discordGuild != null)
            {
                var guildUser = await discordGuild.GetUserAsync(userArtist.DiscordUserId, CacheMode.CacheOnly);
                if (guildUser != null)
                {
                    userName = guildUser.DisplayName;
                }
            }

            whoKnowsArtistList.Add(new WhoKnowsObjectWithUser
            {
                Name = userArtist.Name,
                DiscordName = userName,
                Playcount = userArtist.Playcount,
                LastFMUsername = userArtist.UserNameLastFm,
                UserId = userArtist.UserId,
            });
        }

        return whoKnowsArtistList;
    }

    public async Task<ICollection<GuildArtist>> GetTopAllTimeArtistsForGuild(int guildId,
        OrderType orderType, int? limit = 120)
    {
        var cacheKey = $"guild-alltime-top-artists-{guildId}-{orderType}";

        var cachedArtistsAvailable = this._cache.TryGetValue(cacheKey, out ICollection<GuildArtist> guildArtists);
        if (cachedArtistsAvailable)
        {
            return guildArtists;
        }

        var sql = "SELECT ua.name AS artist_name, " +
                  "SUM(ua.playcount) AS total_playcount, " +
                  "COUNT(ua.user_id) AS listener_count " +
                  "FROM user_artists AS ua   " +
                  "INNER JOIN guild_users AS gu ON gu.user_id = ua.user_id  " +
                  "WHERE gu.guild_id = @guildId  AND gu.bot != true " +
                  "AND NOT ua.user_id = ANY(SELECT user_id FROM guild_blocked_users WHERE blocked_from_who_knows = true AND guild_id = @guildId) " +
                  "AND (gu.who_knows_whitelisted OR gu.who_knows_whitelisted IS NULL) " +
                  "GROUP BY ua.name ";

        sql += orderType == OrderType.Playcount ?
            "ORDER BY total_playcount DESC, listener_count DESC " :
            "ORDER BY listener_count DESC, total_playcount DESC ";

        if (limit.HasValue)
        {
            sql += $"LIMIT {limit}";
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        guildArtists = (await connection.QueryAsync<GuildArtist>(sql, new
        {
            guildId
        })).ToList();

        this._cache.Set(cacheKey, guildArtists, TimeSpan.FromMinutes(10));

        return guildArtists;
    }

    private async Task<IEnumerable<UserArtist>> GetGuildUserArtists(int guildId, int minPlaycount = 0)
    {
        const string sql = "SELECT ua.* " +
                           "FROM user_artists AS ua " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = ua.user_id " +
                           "WHERE gu.guild_id = @guildId  AND gu.bot != true " +
                           "AND ua.playcount > @minPlaycount " +
                           "AND NOT ua.user_id = ANY(SELECT user_id FROM guild_blocked_users WHERE blocked_from_who_knows = true AND guild_id = @guildId) " +
                           "AND (gu.who_knows_whitelisted OR gu.who_knows_whitelisted IS NULL) " +
                           "AND LOWER(ua.name) = ANY(SELECT LOWER(artists.name) AS artist_name " +
                           "FROM public.artist_genres AS ag " +
                           "INNER JOIN artists ON artists.id = ag.artist_id) ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await connection.QueryAsync<UserArtist>(sql, new
        {
            guildId,
            minPlaycount
        });
    }

    public async Task<ICollection<GuildArtist>> GetTopAllTimeArtistsForGuildWithListeners(int guildId,
        OrderType orderType)
    {
        var userArtists = await GetGuildUserArtists(guildId);

        var guildArtists = userArtists
            .GroupBy(g => g.Name)
            .Select(s => new GuildArtist
            {
                ArtistName = s.Key,
                ListenerCount = s.Select(se => se.UserId).Distinct().Count(),
                TotalPlaycount = s.Sum(se => se.Playcount),
                ListenerUserIds = s.Select(se => se.UserId).ToList()
            });

        return guildArtists
            .OrderByDescending(o => orderType == OrderType.Listeners ? o.ListenerCount : o.TotalPlaycount)
            .ToList();
    }

    public async Task<int?> GetArtistPlayCountForUser(string artistName, int userId)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await GetArtistPlayCountForUser(connection, artistName, userId);
    }

    public static async Task<int?> GetArtistPlayCountForUser(NpgsqlConnection connection, string artistName, int userId)
    {
        const string sql = "SELECT ua.playcount " +
                           "FROM user_artists AS ua " +
                           "WHERE ua.user_id = @userId AND UPPER(ua.name) = UPPER(CAST(@artistName AS CITEXT)) " +
                           "ORDER BY playcount DESC";

        return await connection.QueryFirstOrDefaultAsync<int?>(sql, new
        {
            userId,
            artistName
        });
    }

    public async Task<ICollection<AffinityItemDto>> GetAllTimeTopArtistForGuild(int guildId)
    {
        var cacheKey = $"guild-affinity-top-artist-alltime-{guildId}";

        var cachedArtistsAvailable = this._cache.TryGetValue(cacheKey, out ICollection<AffinityItemDto> guildArtists);
        if (cachedArtistsAvailable)
        {
            return guildArtists;
        }

        const string sql = "SELECT * " +
                           "FROM ( " +
                               "SELECT ua.user_id, name, playcount, user_artist_id, " +
                                    "ROW_NUMBER() OVER (PARTITION BY ua.user_id ORDER BY playcount DESC) as pos " +
                               "FROM public.user_artists AS ua  " +
                               "INNER JOIN guild_users AS gu ON gu.user_id = ua.user_id  " +
                               "WHERE gu.guild_id = @guildId " +
                           ") as subquery " +
                           "WHERE pos <= 300; ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        guildArtists = (await connection.QueryAsync<AffinityItemDto>(sql, new
        {
            guildId
        })).ToList();

        this._cache.Set(cacheKey, guildArtists, TimeSpan.FromMinutes(10));

        return guildArtists;
    }

    public async Task<ICollection<AffinityItemDto>> GetQuarterlyTopArtistForGuild(int guildId)
    {
        var cacheKey = $"guild-affinity-top-artist-quarterly-{guildId}";

        var cachedArtistsAvailable = this._cache.TryGetValue(cacheKey, out ICollection<AffinityItemDto> guildArtists);
        if (cachedArtistsAvailable)
        {
            return guildArtists;
        }

        const string sql = "SELECT * " +
                           "FROM ( " +
                               "SELECT up.user_id, artist_name AS name, COUNT(*) as playcount, " +
                                    " ROW_NUMBER() OVER (PARTITION BY up.user_id ORDER BY COUNT(*) DESC) as pos " +
                               "FROM user_play_ts AS up " +
                               "INNER JOIN guild_users AS gu ON gu.user_id = up.user_id  " +
                               "WHERE gu.guild_id = @guildId AND time_played > current_date - interval '90' day " +
                               "GROUP BY up.user_id, artist_name " +
                           ") as subquery " +
                           "WHERE pos <= 100; ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        guildArtists = (await connection.QueryAsync<AffinityItemDto>(sql, new
        {
            guildId
        })).ToList();

        this._cache.Set(cacheKey, guildArtists, TimeSpan.FromMinutes(10));

        return guildArtists;
    }

    public async Task<Dictionary<int, AffinityUser>> GetAffinity(int userId,
        IEnumerable<AffinityItemDto> allTimeArtists,
        List<AffinityItemDto> ownAllTime,
        IEnumerable<AffinityItemDto> quarterlyArtists,
        List<AffinityItemDto> ownQuarterly)
    {
        var ownAllTimeTopArtists = ownAllTime
            .GroupBy(g => g.Name)
            .ToDictionary(d => d.First().Name, d => d.First().Position);

        var ownAllTimeTopGenres = (await this._genreService.GetTopGenresWithPositionForTopArtists(ownAllTime))
            .ToDictionary(d => d.Name, d => d.Position);

        var ownAllTimeTopCountries = (await this._countryService.GetTopCountriesForTopArtists(ownAllTime))
            .ToDictionary(d => d.Name, d => d.Position);

        var results = new Dictionary<int, AffinityUser>();
        foreach (var userTopArtists in allTimeArtists.GroupBy(g => g.UserId))
        {
            var result = await GetAffinityUser(userTopArtists.Key, ownAllTimeTopArtists, ownAllTimeTopGenres, ownAllTimeTopCountries, userTopArtists.ToList());
            results.Add(result.UserId, result);
        }

        var ownQuarterlyTopArtists = ownAllTime
            .GroupBy(g => g.Name)
            .ToDictionary(d => d.First().Name, d => d.First().Position);

        var ownQuarterlyTopGenres = (await this._genreService.GetTopGenresWithPositionForTopArtists(ownQuarterly))
            .ToDictionary(d => d.Name, d => d.Position);

        var ownQuarterlyTopCountries = (await this._countryService.GetTopCountriesForTopArtists(ownQuarterly))
            .ToDictionary(d => d.Name, d => d.Position);

        foreach (var userTopArtists in quarterlyArtists.GroupBy(g => g.UserId))
        {
            var result = await GetAffinityUser(userTopArtists.Key, ownQuarterlyTopArtists, ownQuarterlyTopGenres, ownQuarterlyTopCountries, userTopArtists.ToList());

            if (results.TryGetValue(result.UserId, out var value))
            {
                value.ArtistPoints += result.ArtistPoints;
                value.GenrePoints += result.GenrePoints;
                value.CountryPoints += result.CountryPoints;
                value.TotalPoints += result.TotalPoints;
            }
            else
            {
                results.Add(result.UserId, result);
            }
        }

        return results;
    }

    private async Task<AffinityUser> GetAffinityUser(int userId,
        IReadOnlyDictionary<string, int> artistDictionary,
        IReadOnlyDictionary<string, int> genreDictionary,
        IReadOnlyDictionary<string, int> countryDictionary,
        ICollection<AffinityItemDto> otherTopArtists)
    {
        try
        {
            var artistPoints = 0;
            var genrePoints = 0;
            var countryPoints = 0;

            foreach (var otherArtist in otherTopArtists)
            {
                if (artistDictionary.TryGetValue(otherArtist.Name, out var value))
                {
                    artistPoints += AddPoints(value, otherArtist.Position);
                }
            }

            var otherTopGenres = await this._genreService.GetTopGenresWithPositionForTopArtists(otherTopArtists);

            foreach (var otherTopGenre in otherTopGenres)
            {
                if (genreDictionary.TryGetValue(otherTopGenre.Name, out var value))
                {
                    genrePoints += AddPoints(value, otherTopGenre.Position);
                }
            }

            var otherTopCountries = await this._countryService.GetTopCountriesForTopArtists(otherTopArtists);

            foreach (var otherTopCountry in otherTopCountries)
            {
                if (countryDictionary.TryGetValue(otherTopCountry.Name, out var value))
                {
                    countryPoints += AddPoints(value, otherTopCountry.Position);
                }
            }

            return new AffinityUser
            {
                ArtistPoints = artistPoints,
                GenrePoints = genrePoints,
                CountryPoints = countryPoints,
                TotalPoints = artistPoints * 0.42 + genrePoints * 0.42 + countryPoints * 0.16,
                UserId = userId
            };

        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private static int AddPoints(int ownPosition, int otherPosition)
    {
        return otherPosition switch
        {
            <= 5 => ownPosition switch
            {
                <= 5 => 32,
                <= 10 => 18,
                <= 25 => 12,
                <= 40 => 6,
                <= 60 => 3,
                <= 120 => 2,
                _ => 1
            },
            <= 10 => ownPosition switch
            {
                <= 10 => 18,
                <= 25 => 12,
                <= 40 => 6,
                <= 60 => 4,
                <= 120 => 2,
                _ => 1
            },
            <= 25 => ownPosition switch
            {
                <= 25 => 12,
                <= 40 => 6,
                <= 60 => 4,
                <= 120 => 2,
                _ => 1
            },
            <= 40 => ownPosition switch
            {
                <= 40 => 6,
                <= 60 => 4,
                <= 120 => 2,
                _ => 1
            },
            <= 60 => ownPosition switch
            {
                <= 60 => 4,
                <= 120 => 2,
                _ => 1
            },
            <= 120 => ownPosition switch
            {
                <= 120 => 2,
                _ => 1
            },
            _ => 1
        };
    }
}
