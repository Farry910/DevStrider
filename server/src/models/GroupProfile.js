import mongoose from 'mongoose';

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
 * Resume experience entry — one per company. The role/title for each entry lives in the bid
 * body's `[Subtitle N]` placeholder (sourced from GPT output) rather than the profile, so the
 * composer pairs profile.experiences[N-1] with the body's roleN.
 */
const experienceSchema = new mongoose.Schema(
  {
    company: { type: String, default: '', trim: true },
    location: { type: String, default: '', trim: true },
    startYear: { type: Number, default: null },
    endYear: { type: Number, default: null },
  },
  { _id: false }
);

/**
 * Per-group resume profile. A user can tune a different resume header per group (different
 * headline, experiences, etc.) and the bid-assistant resume composer pulls from this rather than
 * the user-level template. First read auto-seeds from the user's current profile fields.
 *
 * Auth/identity fields (email, password, nickname, avatar, timezone, goals, leaderboard opt-in)
 * stay on User and are never duplicated here.
 */
const groupProfileSchema = new mongoose.Schema(
  {
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },

    displayName: { type: String, default: '', trim: true },
    headline: { type: String, default: '', trim: true },
    location: { type: String, default: '', trim: true },
    phone: { type: String, default: '', trim: true },
    personalEmail: { type: String, default: '', trim: true, lowercase: true },
    linkedinUrl: { type: String, default: '', trim: true },

    education: { type: [educationSchema], default: [] },
    certifications: { type: [certificationSchema], default: [] },
    experiences: { type: [experienceSchema], default: [] },
  },
  { timestamps: true }
);

groupProfileSchema.index({ userId: 1, groupId: 1 }, { unique: true });

export const GroupProfile =
  mongoose.models.GroupProfile || mongoose.model('GroupProfile', groupProfileSchema);
