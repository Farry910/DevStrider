import mongoose from 'mongoose';
import { DEFAULT_GROUP_TIMERS } from '../constants/groupTimers.js';

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

const groupSchema = new mongoose.Schema(
  {
    name: { type: String, required: true, trim: true },
    /** Location label e.g. US, Mexico */
    locationKey: { type: String, required: true, trim: true, lowercase: true },
    creatorId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    memberIds: [{ type: mongoose.Schema.Types.ObjectId, ref: 'User' }],
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
  },
  { timestamps: true }
);

groupSchema.index({ locationKey: 1, name: 1 });

export const Group = mongoose.models.Group || mongoose.model('Group', groupSchema);
