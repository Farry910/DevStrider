import mongoose from 'mongoose';

export const ACHIEVEMENT_KINDS = ['daily_bids', 'weekly_interviews', 'monthly_offers'];

/**
 * Transient achievement record per user-group-kind-period (UTC). Unique on the 4-tuple — the
 * detection service relies on duplicate-key inserts as the idempotency guard so concurrent
 * writes never produce two badges for the same period.
 */
const achievementSchema = new mongoose.Schema(
  {
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },
    kind: { type: String, enum: ACHIEVEMENT_KINDS, required: true },
    /** Period key in UTC: 'YYYY-MM-DD' for daily, 'YYYY-Www' (ISO) for weekly, 'YYYY-MM' for monthly. */
    periodKey: { type: String, required: true },
    achievedAt: { type: Date, default: Date.now },
    metricValue: { type: Number, default: 0 },
    target: { type: Number, default: 0 },
  },
  { timestamps: true }
);

achievementSchema.index(
  { userId: 1, groupId: 1, kind: 1, periodKey: 1 },
  { unique: true }
);
achievementSchema.index({ groupId: 1, achievedAt: -1 });

export const Achievement =
  mongoose.models.Achievement || mongoose.model('Achievement', achievementSchema);
