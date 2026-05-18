import mongoose from 'mongoose';

const profileBadgeGrantSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true },
    badgeKey: { type: String, required: true, trim: true },
  },
  { _id: false }
);

const educationSchema = new mongoose.Schema(
  {
    degree: { type: String, default: '', trim: true },
    school: { type: String, default: '', trim: true },
    location: { type: String, default: '', trim: true },
    startYear: { type: Number, default: null },
    endYear: { type: Number, default: null },
  },
  { _id: false }
);

const certificationSchema = new mongoose.Schema(
  {
    name: { type: String, default: '', trim: true },
    issuer: { type: String, default: '', trim: true },
    year: { type: Number, default: null },
  },
  { _id: false }
);

/**
 * Per-user daily/weekly/monthly targets. Counted against UTC period boundaries (see achievementService).
 * 0 means "no goal set" — never triggers achievements for that metric.
 */
const goalsSchema = new mongoose.Schema(
  {
    bidsPerDay: { type: Number, default: 0, min: 0, max: 1000 },
    interviewsPerWeek: { type: Number, default: 0, min: 0, max: 1000 },
    offersPerMonth: { type: Number, default: 0, min: 0, max: 1000 },
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

    /** Resume header — owner-only on the bid board, never shown to teammates. */
    displayName: { type: String, default: '', trim: true },
    headline: { type: String, default: '', trim: true },
    location: { type: String, default: '', trim: true },
    phone: { type: String, default: '', trim: true },
    personalEmail: { type: String, default: '', trim: true, lowercase: true },
    linkedinUrl: { type: String, default: '', trim: true },
    education: { type: [educationSchema], default: [] },
    certifications: { type: [certificationSchema], default: [] },

    goals: { type: goalsSchema, default: () => ({}) },
    /** When false, the user is rendered as anonymous on the group leaderboard. Always sees their own row. */
    showOnLeaderboard: { type: Boolean, default: true },

    /**
     * Cross-group platform role. 'admin' = seeded platform admin who approves group creation,
     * transfers ownership, and views total storage. Everyone else is 'user'.
     */
    platformRole: { type: String, enum: ['user', 'admin'], default: 'user' },
  },
  { timestamps: true }
);

export const User = mongoose.models.User || mongoose.model('User', userSchema);
