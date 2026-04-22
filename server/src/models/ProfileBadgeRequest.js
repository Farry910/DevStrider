import mongoose from 'mongoose';

const profileBadgeRequestSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    badgeKey: { type: String, required: true, trim: true },
    status: {
      type: String,
      enum: ['pending', 'approved', 'rejected'],
      default: 'pending',
      index: true,
    },
    reviewedByUserId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', default: null },
    reviewedAt: { type: Date, default: null },
    reviewNote: { type: String, trim: true, default: '' },
  },
  { timestamps: true }
);

profileBadgeRequestSchema.index({ groupId: 1, status: 1 });
profileBadgeRequestSchema.index({ userId: 1, groupId: 1, badgeKey: 1, status: 1 });

export const ProfileBadgeRequest =
  mongoose.models.ProfileBadgeRequest || mongoose.model('ProfileBadgeRequest', profileBadgeRequestSchema);
