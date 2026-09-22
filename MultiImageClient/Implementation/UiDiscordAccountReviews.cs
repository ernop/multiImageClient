#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed partial class UiDiscordAccountRequests
    {
        public sealed record ReviewItem(string Id, string Username, string State, long CreatedAt, long? ExpiresAt,
            bool CanTest, bool CanSend, bool CanReject);

        public IReadOnlyList<ReviewItem> Reviews()
        {
            var now = _now().ToUnixTimeMilliseconds();
            return Read().Tickets.Where(t => t.ReviewRequired && (HasReusableLink(t) || t.ExpiresAt > now) && t.State != "used")
                .OrderBy(t => t.State is "sent" or "failed" or "rejected" ? 1 : 0).ThenBy(t => t.CreatedAt).Take(100)
                .Select(t => new ReviewItem(t.Id, t.Username, t.State, t.CreatedAt, HasReusableLink(t) ? null : t.ExpiresAt,
                    Enabled && CanTest(t, now), Enabled && CanSend(t, now), Enabled && IsReview(t))).ToArray();
        }

        public string PreviewUrl => PublicUrl(_settings, _auth, _environments)
            is { } url ? url + "/preview" : throw new InvalidOperationException("Discord account requests are disabled.");

        private string ReviewerId()
        {
            var id = _settings.DiscordAccountReviewerId;
            if (!Regex.IsMatch(id ?? "", @"\A[1-9][0-9]{0,19}\z") || !ulong.TryParse(id, out _))
                throw new InvalidOperationException("Configure Brouhahaha's verified Discord account before sending test copies.");
            return id!;
        }
        private bool RecentTest(Ticket ticket, long now) => ticket.TestedAt > now - (long)TestLifetime.TotalMilliseconds
            && ticket.TestedAt <= now && ticket.ReviewerId == _settings.DiscordAccountReviewerId;
        private bool CanTest(Ticket ticket, long now) => ticket.State is "review" or "test-failed"
            || ticket.State == "tested" && !RecentTest(ticket, now);
        private bool CanSend(Ticket ticket, long now) => ticket.State == "tested" && RecentTest(ticket, now);
        private static bool IsReview(Ticket ticket) => ticket.State is "review" or "test-pending" or "tested" or "test-failed";
        private Ticket ReviewTicket(Document doc, string id)
        {
            if (!Enabled) throw new InvalidOperationException("Discord account requests are disabled.");
            var ticket = doc.Tickets.SingleOrDefault(t => t.Id == id && t.ReviewRequired);
            if (ticket == null || ticket.ExpiresAt <= _now().ToUnixTimeMilliseconds())
                throw new InvalidOperationException("This request expired or no longer exists. Refresh the request list.");
            return ticket;
        }
        private async Task CheckRecipientAsync(DiscordAccountRequestsClient client, Ticket ticket, CancellationToken ct)
        {
            var member = await client.CheckMemberAsync(ticket.UserId, ticket.GuildId, ticket.ChannelId, ct);
            if (member.Username != ticket.Username)
                throw new InvalidOperationException("The recipient's Discord username changed. Ask them to request another link.");
            var existing = _auth.LoginLinks!.List().SingleOrDefault(a => a.DiscordUserId == ticket.UserId);
            if (existing != null && (existing.Revoked || !_environments.CanEnter(EnvironmentId, existing.Login)))
                throw new InvalidOperationException("This account's access is disabled. No link was sent.");
        }

        public async Task<Delivery> TestAsync(string id, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another account request is in progress. Try again shortly.");
            try
            {
                var doc = Read(); var ticket = ReviewTicket(doc, id);
                if (!CanTest(ticket, _now().ToUnixTimeMilliseconds()))
                    throw new InvalidOperationException("This request was already tested or needs a delivery check.");
                var reviewer = ReviewerId(); var client = _client();
                await CheckRecipientAsync(client, ticket, ct);
                await client.CheckMemberAsync(reviewer, ticket.GuildId, ticket.ChannelId, ct);
                _ = ReviewTicket(doc, id);
                var preview = PreviewUrl;
                ticket.State = "test-pending"; ticket.ReviewerId = reviewer; ticket.TestedAt = 0;
                Write(doc);
                try
                {
                    await client.SendPreviewAsync(reviewer, ticket.Username, preview, ct);
                    ticket.State = "tested"; ticket.TestedAt = _now().ToUnixTimeMilliseconds();
                    Write(doc);
                    return new("tested", "Test copy sent to Brouhahaha. Check the DM, then select Confirmed, send to user.");
                }
                catch (DiscordDmRejectedException ex)
                {
                    ticket.State = "test-failed"; Write(doc); return new("test-failed", ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new("test-pending", "Test delivery is unconfirmed. Sending to the user remains blocked. No automatic retry will occur.");
                }
            }
            finally { _gate.Release(); }
        }

        public async Task<Delivery> SendReviewedAsync(string id, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another account request is in progress. Try again shortly.");
            try
            {
                var doc = Read(); var ticket = ReviewTicket(doc, id);
                if (!CanSend(ticket, _now().ToUnixTimeMilliseconds()))
                    throw new InvalidOperationException("Send a successful test copy to Brouhahaha before confirming delivery to the user.");
                var client = _client(); await CheckRecipientAsync(client, ticket, ct);
                _ = ReviewTicket(doc, id);
                if (!CanSend(ticket, _now().ToUnixTimeMilliseconds()))
                    throw new InvalidOperationException("The test expired. Send another test copy before confirming delivery.");
                var url = PublicUrl(_settings, _auth, _environments)!;
                var secret = NewSecret(); var now = _now().ToUnixTimeMilliseconds();
                ticket.TokenHash = Hash(secret); ticket.ApprovedAt = now;
                var existing = _auth.LoginLinks!.List().SingleOrDefault(account => account.DiscordUserId == ticket.UserId);
                if (existing != null)
                {
                    ticket.AccountId = existing.Id; ticket.Login = existing.Login; ticket.AccountTokenHash = existing.TokenHash;
                }
                ticket.ExpiresAt = 0; ticket.State = "pending";
                Write(doc); // Create the reusable secret only after the owner's confirmed send.
                try
                {
                    await client.SendLinkAsync(ticket.UserId, url + "/claim#" + ticket.Id + "." + secret, ct);
                    ticket.State = "sent"; Write(doc);
                    return new("sent", "Account link sent to @" + ticket.Username + ". Keep it for future logins. It does not expire.");
                }
                catch (DiscordDmRejectedException ex)
                {
                    ticket.State = "failed"; ticket.ExpiresAt = now + (long)TestLifetime.TotalMilliseconds;
                    Write(doc); return new("failed", ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new("pending", "Discord delivery is unconfirmed. Check delivery before another request. No automatic retry will occur.");
                }
            }
            finally { _gate.Release(); }
        }

        public async Task RejectAsync(string id, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another account request is in progress. Try again shortly.");
            try
            {
                var doc = Read(); var ticket = ReviewTicket(doc, id);
                if (!IsReview(ticket)) throw new InvalidOperationException("This request has already reached delivery.");
                ticket.State = "rejected"; Write(doc);
            }
            finally { _gate.Release(); }
        }
    }
}
