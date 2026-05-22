import mongoose from 'mongoose';
import { DEFAULT_GROUP_TIMERS } from '../constants/groupTimers.js';

export const GROUP_MEMBER_ROLES = ['bidder', 'caller', 'ops'];

const groupTimersSchema = new mongoose.Schema(
  {
    junkRemovalGraceMinutes: {
      type: Number,
      default: DEFAULT_GROUP_TIMERS.junkRemovalGraceMinutes,
      min: 1,
      max: 10080,
    },
    bidDuplicateLookbackDays: {
      type: Number,
      default: DEFAULT_GROUP_TIMERS.bidDuplicateLookbackDays,
      min: 1,
      max: 3650,
    },
    possibleTimerMinutes: {
      type: Number,
      default: DEFAULT_GROUP_TIMERS.possibleTimerMinutes,
      min: 0,
      max: 10080,
    },
  },
  { _id: false }
);

const groupMemberSchema = new mongoose.Schema(
  {
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    /** Per-group roles. Default ['ops']: read-only on watched users until the admin grants more. */
    roles: {
      type: [{ type: String, enum: GROUP_MEMBER_ROLES }],
      default: ['ops'],
    },
    /**
     * For CALLER: which bidders this caller sees interviews for.
     * For OPS: which users this ops watches (bid board + interviews, read-only).
     * Empty means no scope; the caller/ops can't see anything until the admin assigns.
     */
    watches: [{ type: mongoose.Schema.Types.ObjectId, ref: 'User' }],
    joinedAt: { type: Date, default: Date.now },
  },
  { _id: false }
);

const groupSchema = new mongoose.Schema(
  {
    name: { type: String, required: true, trim: true },
    /** Location label e.g. US, Mexico */
    locationKey: { type: String, required: true, trim: true, lowercase: true },
    creatorId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    /**
     * Legacy flat list — kept in sync with `members[]` for fast `$in` aggregations and any code
     * paths that haven't been migrated yet. Source of truth for membership is `members[]`.
     */
    memberIds: [{ type: mongoose.Schema.Types.ObjectId, ref: 'User' }],
    /** Per-user role + watch assignments. Always kept in sync with memberIds via group helpers. */
    members: { type: [groupMemberSchema], default: [] },
    /**
     * Lifecycle status. New groups start 'pending' and need a platform-admin approval before they
     * appear in normal listings. Existing groups created before this field landed are 'approved'.
     */
    status: { type: String, enum: ['pending', 'approved'], default: 'pending' },
    approvedAt: { type: Date, default: null },
    approvedByUserId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', default: null },
    /**
     * Optional second party for safe deletion: owner + this member must both POST /removal-request
     * before the group is removed. When null, the owner may delete alone.
     */
    removalAssisterId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', default: null },
    removalOwnerConfirmedAt: { type: Date, default: null },
    removalAssisterConfirmedAt: { type: Date, default: null },
    /** Linear overview score weights; only the group owner may update. Merged with defaults when read. */
    overviewScoreWeights: { type: mongoose.Schema.Types.Mixed, default: undefined },
    /** Owner-tunable timers (junk auto-removal grace, duplicate detection window, etc.). */
    timers: { type: groupTimersSchema, default: () => ({}) },
    /**
     * When true, members may add links / edit / delete / fast-feed on past-day boards (not just
     * today). When false (default), bid writes are restricted to the current UTC calendar day via
     * assertNowInWindow.
     */
    allowPastDayEdit: { type: Boolean, default: false },
  },
  { timestamps: true }
);

groupSchema.index({ locationKey: 1, name: 1 });
groupSchema.index({ status: 1, createdAt: -1 });

/**
 * Backfill `members[]` from `memberIds[]` for legacy docs that pre-date this schema, then enforce
 * the invariant `memberIds = members.map(m => m.userId)`. Runs on every save; cheap enough to be
 * called twice.
 */
groupSchema.pre('save', function syncMembers(next) {
  const haveMembers = Array.isArray(this.members) && this.members.length > 0;
  const haveLegacy = Array.isArray(this.memberIds) && this.memberIds.length > 0;
  if (!haveMembers && haveLegacy) {
    /** Legacy upgrade: every existing member becomes ops by default, owner becomes admin via creatorId
     * (admin is implicit, not stored in roles). */
    this.members = this.memberIds.map((uid) => ({
      userId: uid,
      roles: ['ops'],
      watches: [],
      joinedAt: this.createdAt || new Date(),
    }));
  } else {
    /** Sync the flat list to whatever's in members[]. */
    this.memberIds = (this.members || []).map((m) => m.userId);
  }
  next();
});

export const Group = mongoose.models.Group || mongoose.model('Group', groupSchema);
