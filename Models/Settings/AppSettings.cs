namespace GymApis.Models.Settings;

/// <summary>
/// Maps onto `app_settings` — operational configuration the owner can change
/// without a redeploy.
///
/// Deliberately NOT part of `site_settings`. That table is the public website's
/// CMS content; this is the switch that decides whether the gym's transactional
/// mail leaves the building. Merging them would put a marketing screen's Save
/// button in that path.
/// </summary>
public class AppSettings
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>
    /// The master switch. Off = no member mail of any kind.
    /// </summary>
    public bool EmailsEnabled { get; set; } = true;

    /// <summary>
    /// Read literally, "no one gets email" also locks every user out of
    /// password recovery — including the owner who flipped the switch. So this
    /// is a second, separate decision: while it is false, reset and
    /// password-changed mail still flows with the master switch off.
    /// </summary>
    public bool SuspendTransactional { get; set; }

    /// <summary>
    /// The vendor's daily ceiling minus headroom. Brevo's free tier is 300/day;
    /// 270 leaves room for anything sent outside this queue.
    /// </summary>
    public int DailyEmailLimit { get; set; } = 270;

    /// <summary>
    /// Held back from the normal pool so a password reset requested at 3pm
    /// still has budget after a morning of renewal reminders. This is a ceiling
    /// on normal-priority mail, NOT a sort order — see 009_messaging.sql.
    /// </summary>
    public int HighPriorityReserve { get; set; } = 30;

    /// <summary>
    /// Days before expiry to remind, e.g. {3} or {7,3,1}. A list rather than a
    /// scalar so a ladder is a settings change, not a code change.
    /// </summary>
    public int[] ReminderDaysBefore { get; set; } = [3];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
