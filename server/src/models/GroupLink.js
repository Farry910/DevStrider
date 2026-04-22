import mongoose from 'mongoose';
import { normalizeGroupUrl } from '../utils/urlNorm.js';

/** Shared job URL row for a group — only link (+ optional shared JD) are group-visible. */
const groupLinkSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },
    url: { type: String, required: true, trim: true },
    /** Dedup key: same normalized URL = one shared link per group */
    urlNorm: { type: String, default: '', index: true },
    /** Filled once by anyone in the group; optional shared context */
    sharedJobDescription: { type: String, default: '' },
    createdByUserId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    /**
     * First application snapshot for this link (set when any member saves company+role on a bid).
     * Group-wide reference for company+role duplicate detection across listings.
     */
    appliedCompany: { type: String, default: '', trim: true },
    appliedRole: { type: String, default: '', trim: true },
    appliedStacks: [{ type: String, trim: true }],
    appliedAt: { type: Date, default: null },
    appliedByUserId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', default: null },
    /** Set when the link creator marks the posting as useless; owner may purge if rules allow. */
    markedUselessAt: { type: Date, default: null },
  },
  { timestamps: true }
);

groupLinkSchema.pre('validate', function setUrlNorm(next) {
  if (this.url) this.urlNorm = normalizeGroupUrl(this.url);
  next();
});

groupLinkSchema.index({ groupId: 1, createdAt: -1 });
groupLinkSchema.index({ groupId: 1, url: 1 });
groupLinkSchema.index(
  { groupId: 1, urlNorm: 1 },
  {
    unique: true,
    partialFilterExpression: { urlNorm: { $exists: true, $nin: [null, ''] } },
  }
);

export const GroupLink = mongoose.models.GroupLink || mongoose.model('GroupLink', groupLinkSchema);
