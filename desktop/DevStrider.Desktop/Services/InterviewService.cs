using DevStrider.Desktop.Data;
using DevStrider.Desktop.Models;
using MongoDB.Bson;

namespace DevStrider.Desktop.Services;

public class InterviewService
{
    private readonly IInterviewRepository _interviews;
    private readonly ProfileContext _profileContext;

    public InterviewService(IInterviewRepository interviews, ProfileContext profileContext)
    {
        _interviews = interviews;
        _profileContext = profileContext;
    }

    private ObjectId ActiveProfileId => _profileContext.Current?.Id ?? ObjectId.Empty;

    public Task<List<Interview>> ListAsync(DateTime fromUtc, DateTime toUtc)
    {
        var profileId = ActiveProfileId;
        if (profileId == ObjectId.Empty) return Task.FromResult(new List<Interview>());
        return _interviews.ListByProfileScheduledBetweenAsync(profileId, fromUtc, toUtc);
    }

    public async Task<Interview> CreateAsync(Interview iv)
    {
        if (iv.ProfileId == ObjectId.Empty) iv.ProfileId = ActiveProfileId;
        if (iv.ProfileId == ObjectId.Empty)
            throw new InvalidOperationException("No active profile — create one in the Profiles tab first.");
        // Every interview belongs to a process. A next round arrives with its parent's ProcessId
        // already set (see InterviewPanelViewModel); anything else starts a new one.
        if (iv.ProcessId == ObjectId.Empty) iv.ProcessId = ObjectId.GenerateNewId();
        iv.CreatedAt = DateTime.UtcNow;
        iv.UpdatedAt = iv.CreatedAt;
        await _interviews.UpsertAsync(iv);
        return iv;
    }

    public async Task UpdateAsync(Interview iv)
    {
        iv.UpdatedAt = DateTime.UtcNow;
        await _interviews.UpsertAsync(iv);
    }

    public Task DeleteAsync(ObjectId id) => _interviews.DeleteAsync(id);

    /// <summary>True when at least one interview is attached to the given bid.</summary>
    public Task<bool> HasForBidAsync(ObjectId bidId) => _interviews.AnyForBidAsync(bidId);

    /// <summary>
    /// Companies this profile is already interviewing at — feeds the bid board's warning column.
    /// Only the scheduled/completed/passed statuses count: a failed or cancelled round is not a
    /// reason to warn someone off bidding there again.
    /// </summary>
    public Task<List<string>> ActiveInterviewCompaniesAsync(ObjectId profileId) =>
        _interviews.ListCompaniesByProfileWithStatusAsync(profileId, new[]
        {
            InterviewStatuses.Scheduled,
            InterviewStatuses.Completed,
            InterviewStatuses.Passed,
        });
}
