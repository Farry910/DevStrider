import mongoose from 'mongoose';

const profileBadgeGrantSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true },
    badgeKey: { type: String, required: true, trim: true },
  },
  { _id: false }
);

const userSchema = new mongoose.Schema(
  {
    email: { type: String, required: true, unique: true, lowercase: true, trim: true },
    passwordHash: { type: String, required: true },
    nickname: { type: String, required: true, trim: true },
    /** Display on bid board link rows; `initial` = first letter of nickname only. */
    avatarId: { type: String, default: 'initial', trim: true },
    /** Profile badges approved by the group creator, scoped per group. */
    profileBadgeGrants: { type: [profileBadgeGrantSchema], default: [] },
  },
  { timestamps: true }
);

export const User = mongoose.models.User || mongoose.model('User', userSchema);
