using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DevStrider.Desktop.Services;

/// <summary>
/// One-time pull from the legacy web-app schema (collections: <c>users</c>, <c>groups</c>,
/// <c>grouplinks</c>, <c>userbids</c>, <c>interviews</c>) into this machine's local Mongo,
/// mapping each legacy group to its own local <see cref="Profile"/>.
///
/// <para>
/// Idempotent: every upsert uses the legacy <c>_id</c>, so re-running the tool with the
/// same email never duplicates. Re-runs pick up any data the user added in the web app
/// between migrations.
/// </para>
/// </summary>
public sealed class LegacyMigrationService
{
    private readonly AtlasContext _atlas;
    private readonly MongoContext _local;
    private readonly ProfilesService _profiles;
    private readonly ProfileContext _profileContext;
    private readonly ProfileService _userProfile;
    private readonly SettingsService _settings;
    private readonly ActivityLogService _activity;

    public LegacyMigrationService(
        AtlasContext atlas,
        MongoContext local,
        ProfilesService profiles,
        ProfileContext profileContext,
        ProfileService userProfile,
        SettingsService settings,
        ActivityLogService activity)
    {
        _atlas = atlas;
        _local = local;
        _profiles = profiles;
        _profileContext = profileContext;
        _userProfile = userProfile;
        _settings = settings;
        _activity = activity;
    }

    public class Result
    {
        public bool Success { get; set; }
        public string Summary { get; set; } = "";
        public int ProfilesTouched { get; set; }
        public int BidsImported { get; set; }
        public int InterviewsImported { get; set; }
        public int LinksImported { get; set; }
    }

    public async Task<Result> MigrateAsync(string email)
    {
        var result = new Result();
        var normEmail = (email ?? "").Trim().ToLowerInvariant();
        if (normEmail.Length == 0)
        {
            result.Summary = "Email is required.";
            return result;
        }

        IMongoDatabase db;
        try { db = await _atlas.GetDatabaseAsync(); }
        catch (Exception ex)
        {
            result.Summary = $"Couldn't reach the shared cluster: {ex.Message}";
            _activity.Error("Migration", "Legacy import failed", result.Summary);
            return result;
        }

        // ---- Step 1: find the legacy user by email ----------------------------
        var usersCol = db.GetCollection<BsonDocument>("users");
        var userDoc = await usersCol.Find(Builders<BsonDocument>.Filter.Eq("email", normEmail)).FirstOrDefaultAsync();
        if (userDoc == null)
        {
            result.Summary = $"No user found with email '{normEmail}' in the legacy database.";
            _activity.Warning("Migration", "Legacy import: user not found", normEmail);
            return result;
        }
        var legacyUserId = userDoc["_id"].AsObjectId;
        var nickname = userDoc.GetValue("nickname", "").ToString() ?? "";

        // ---- Step 2: pull all bids + interviews owned by this user ------------
        var legacyBids = await db.GetCollection<BsonDocument>("userbids")
            .Find(Builders<BsonDocument>.Filter.Eq("userId", legacyUserId))
            .ToListAsync();
        var legacyIvs = await db.GetCollection<BsonDocument>("interviews")
            .Find(Builders<BsonDocument>.Filter.Eq("userId", legacyUserId))
            .ToListAsync();

        if (legacyBids.Count == 0 && legacyIvs.Count == 0)
        {
            result.Summary = $"User '{nickname}' has no bids or interviews in the legacy database.";
            _activity.Info("Migration", "Legacy import: nothing to migrate", normEmail);
            return result;
        }

        // ---- Step 3: collect groupIds the user has data in --------------------
        var groupIds = legacyBids.Select(b => b["groupId"].AsObjectId)
            .Concat(legacyIvs.Select(i => i["groupId"].AsObjectId))
            .Distinct()
            .ToList();

        // ---- Step 4: fetch groups (for names) + relevant grouplinks ----------
        var groupsById = (await db.GetCollection<BsonDocument>("groups")
            .Find(Builders<BsonDocument>.Filter.In("_id", groupIds))
            .ToListAsync())
            .ToDictionary(g => g["_id"].AsObjectId, g => g);

        var linkIds = legacyBids
            .Select(b => b.TryGetValue("groupLinkId", out var v) && v.IsObjectId ? v.AsObjectId : ObjectId.Empty)
            .Where(id => id != ObjectId.Empty)
            .Distinct()
            .ToList();
        var legacyLinks = await db.GetCollection<BsonDocument>("grouplinks")
            .Find(Builders<BsonDocument>.Filter.In("_id", linkIds))
            .ToListAsync();

        // ---- Step 5: create or find local profile per legacy group -----------
        var existing = await _profiles.ListAsync();
        var localProfileByGroup = new Dictionary<ObjectId, Profile>();
        var perProfile = new Dictionary<ObjectId, (string name, int bids, int ivs, int links)>();

        foreach (var gid in groupIds)
        {
            var groupName = groupsById.TryGetValue(gid, out var g)
                ? (g.GetValue("name", "").ToString() ?? "").Trim()
                : "";
            if (string.IsNullOrEmpty(groupName)) groupName = $"Imported {gid.ToString().Substring(0, 6)}";

            // Match local profile by name (case-insensitive). Create if missing.
            var profile = existing.FirstOrDefault(p =>
                string.Equals(p.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
            {
                profile = await _profiles.CreateAsync(groupName);
                existing.Add(profile);
            }
            localProfileByGroup[gid] = profile;
            perProfile[profile.Id] = (profile.Name, 0, 0, 0);
        }

        // ---- Step 6: upsert links into local mongo ----------------------------
        foreach (var link in legacyLinks)
        {
            var gid = link["groupId"].AsObjectId;
            if (!localProfileByGroup.TryGetValue(gid, out var prof)) continue;

            var local = new GroupLink
            {
                Id = link["_id"].AsObjectId,
                ProfileId = prof.Id,
                Url = AsString(link, "url"),
                UrlNorm = AsString(link, "urlNorm"),
                SharedJobDescription = AsString(link, "sharedJobDescription"),
                AppliedCompany = AsString(link, "appliedCompany"),
                AppliedRole = AsString(link, "appliedRole"),
                AppliedStacks = AsStringList(link, "appliedStacks"),
                CreatedAt = AsDate(link, "createdAt") ?? DateTime.UtcNow,
                UpdatedAt = AsDate(link, "updatedAt") ?? DateTime.UtcNow,
                AppliedAt = AsNullableDate(link, "appliedAt"),
                MarkedUselessAt = AsNullableDate(link, "markedUselessAt"),
            };
            await _local.Links.ReplaceOneAsync(
                Builders<GroupLink>.Filter.Eq(l => l.Id, local.Id),
                local,
                new ReplaceOptions { IsUpsert = true });
            var entry = perProfile[prof.Id];
            perProfile[prof.Id] = (entry.name, entry.bids, entry.ivs, entry.links + 1);
            result.LinksImported++;
        }

        // ---- Step 7: upsert bids ---------------------------------------------
        foreach (var bid in legacyBids)
        {
            var gid = bid["groupId"].AsObjectId;
            if (!localProfileByGroup.TryGetValue(gid, out var prof)) continue;

            var glink = bid.TryGetValue("groupLinkId", out var lv) && lv.IsObjectId
                ? lv.AsObjectId
                : ObjectId.Empty;

            var local = new UserBid
            {
                Id = bid["_id"].AsObjectId,
                ProfileId = prof.Id,
                GroupLinkId = glink,
                ResumeId = AsString(bid, "resumeId"),
                Company = AsString(bid, "company"),
                Role = AsString(bid, "role"),
                PrimaryStacks = AsStringList(bid, "primaryStacks"),
                Status = string.IsNullOrEmpty(AsString(bid, "status")) ? BidStatuses.Draft : AsString(bid, "status"),
                Origin = string.IsNullOrEmpty(AsString(bid, "origin")) ? "LinkedIn" : AsString(bid, "origin"),
                JobDescription = AsString(bid, "jobDescription"),
                GptResumeContent = AsString(bid, "gptResumeContent"),
                Comment = AsString(bid, "comment"),
                CreatedAt = AsDate(bid, "createdAt") ?? DateTime.UtcNow,
                UpdatedAt = AsDate(bid, "updatedAt") ?? DateTime.UtcNow,
                FirstCreatedAt = AsDate(bid, "firstCreatedAt") ?? AsDate(bid, "createdAt") ?? DateTime.UtcNow,
                AppliedAt = AsNullableDate(bid, "appliedAt"),
            };
            await _local.Bids.ReplaceOneAsync(
                Builders<UserBid>.Filter.Eq(b => b.Id, local.Id),
                local,
                new ReplaceOptions { IsUpsert = true });
            var entry = perProfile[prof.Id];
            perProfile[prof.Id] = (entry.name, entry.bids + 1, entry.ivs, entry.links);
            result.BidsImported++;
        }

        // ---- Step 8: upsert interviews ---------------------------------------
        foreach (var iv in legacyIvs)
        {
            var gid = iv["groupId"].AsObjectId;
            if (!localProfileByGroup.TryGetValue(gid, out var prof)) continue;

            var bidId = iv.TryGetValue("bidId", out var bv) && bv.IsObjectId
                ? bv.AsObjectId
                : ObjectId.Empty;

            var local = new Interview
            {
                Id = iv["_id"].AsObjectId,
                ProfileId = prof.Id,
                BidId = bidId,
                MeetingLink = AsString(iv, "meetingLink"),
                Origin = AsString(iv, "origin"),
                InterviewType = MapInterviewType(AsString(iv, "interviewType")),
                Company = AsString(iv, "company"),
                Role = AsString(iv, "role"),
                Recruiter = AsString(iv, "recruiter"),
                AdditionalAttendees = SplitAttendees(AsString(iv, "additionalAttendees")),
                ResumeId = "",
                ScheduledDate = AsNullableDate(iv, "scheduledDate"),
                ScheduledTime = AsString(iv, "scheduledTime"),
                DurationMinutes = iv.TryGetValue("durationMinutes", out var dm) && dm.IsInt32 ? dm.AsInt32 : 60,
                Status = string.IsNullOrEmpty(AsString(iv, "status")) ? InterviewStatuses.Scheduled : AsString(iv, "status"),
                UserComment = AsString(iv, "userComment"),
                AttachedJobDescription = AsString(iv, "attachedJobDescription"),
                AttachedResumeContent = AsString(iv, "attachedResumeContent"),
                CreatedAt = AsDate(iv, "createdAt") ?? DateTime.UtcNow,
                UpdatedAt = AsDate(iv, "updatedAt") ?? DateTime.UtcNow,
            };
            await _local.Interviews.ReplaceOneAsync(
                Builders<Interview>.Filter.Eq(i => i.Id, local.Id),
                local,
                new ReplaceOptions { IsUpsert = true });
            var entry = perProfile[prof.Id];
            perProfile[prof.Id] = (entry.name, entry.bids, entry.ivs + 1, entry.links);
            result.InterviewsImported++;
        }

        // ---- Step 9: refresh profile context + stamp settings + UserProfile --
        await _profileContext.RefreshListAsync();

        var localUserProfile = await _userProfile.GetAsync();
        var osDefault = ProfileService.DefaultUsername();
        if (!string.IsNullOrWhiteSpace(nickname) &&
            (string.Equals(localUserProfile.Username, "me", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(localUserProfile.Username, osDefault, StringComparison.OrdinalIgnoreCase)))
        {
            localUserProfile.Username = nickname;
            await _userProfile.SaveAsync(localUserProfile);
        }

        var settings = await _settings.GetAsync();
        settings.LegacyMigratedAt = DateTime.UtcNow;
        await _settings.SaveAsync(settings);

        // ---- Step 10: summary --------------------------------------------------
        result.Success = true;
        result.ProfilesTouched = perProfile.Count;
        var lines = perProfile.Values
            .OrderBy(v => v.name, StringComparer.OrdinalIgnoreCase)
            .Select(v => $"  • {v.name}: {v.bids} bid{(v.bids == 1 ? "" : "s")}, " +
                         $"{v.ivs} interview{(v.ivs == 1 ? "" : "s")}, " +
                         $"{v.links} link{(v.links == 1 ? "" : "s")}");
        result.Summary =
            $"Imported as '{nickname}' ({normEmail}) across {perProfile.Count} profile" +
            $"{(perProfile.Count == 1 ? "" : "s")}:\n" + string.Join("\n", lines);
        _activity.Success("Migration", "Legacy import complete", result.Summary);
        return result;
    }

    // ---- Field-extraction helpers -----------------------------------------
    // BsonValue.AsString throws on null/missing; these wrappers return safe defaults.

    private static string AsString(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var v) || v.IsBsonNull) return "";
        return v.IsString ? v.AsString : v.ToString() ?? "";
    }

    private static List<string> AsStringList(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var v) || !v.IsBsonArray) return new List<string>();
        return v.AsBsonArray
            .Where(x => x.IsString)
            .Select(x => x.AsString)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static DateTime? AsDate(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var v) || v.IsBsonNull || !v.IsBsonDateTime) return null;
        return v.ToUniversalTime();
    }

    private static DateTime? AsNullableDate(BsonDocument doc, string name) => AsDate(doc, name);

    /// <summary>Translate the legacy SCREAMING_SNAKE enum into the desktop's friendly-cased values.</summary>
    private static string MapInterviewType(string legacy) => legacy switch
    {
        "PHONE_SCREENING" => InterviewTypes.PhoneCall,
        "HR" => InterviewTypes.HR,
        "ASSESSMENT" => InterviewTypes.Assessment,
        "TECH_1" => InterviewTypes.Tech1,
        "TECH_2" => InterviewTypes.Tech2,
        "TECH_3" => InterviewTypes.Tech3,
        "CLIENT" => InterviewTypes.ClientInterview,
        "OFFER" => InterviewTypes.Offer,
        _ => string.IsNullOrWhiteSpace(legacy) ? InterviewTypes.HR : legacy,
    };

    /// <summary>Legacy stored attendees as one free-text string; the new model is a list.</summary>
    private static List<string> SplitAttendees(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
