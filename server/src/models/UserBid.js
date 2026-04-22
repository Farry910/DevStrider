import mongoose from 'mongoose';

export const BID_STATUSES = [
  'draft',
  'applied',
  'screening',
  'interview',
  'offer',
  'rejected',
  'withdrawn',
  'accepted',
];

const auditEntrySchema = new mongoose.Schema(
  {
    at: { type: Date, default: Date.now },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    action: { type: String, required: true },
    snapshot: { type: mongoose.Schema.Types.Mixed },
  },
  { _id: false }
);

const userBidSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    groupLinkId: { type: mongoose.Schema.Types.ObjectId, ref: 'GroupLink', required: true },
    resumeId: { type: String, default: '', trim: true },
    company: { type: String, default: '', trim: true },
    role: { type: String, default: '', trim: true },
    primaryStacks: [{ type: String, trim: true }],
    /** `applied` after fast feed (or explicit edit); URL-only rows stay `draft`. */
    status: { type: String, enum: BID_STATUSES, default: 'draft' },
    origin: { type: String, default: 'LinkedIn', trim: true },
    jobDescription: { type: String, default: '' },
    /** Resume/body text produced by ChatGPT (Bid Assistant integration). */
    gptResumeContent: { type: String, default: '' },
    comment: { type: String, default: '' },
    lastModifiedBy: { type: mongoose.Schema.Types.ObjectId, ref: 'User' },
    audit: [auditEntrySchema],
    /** Set once when the bid row is created; never changes on edits (see pre-save). */
    firstCreatedAt: { type: Date, immutable: true },
  },
  { timestamps: true }
);

userBidSchema.pre('save', function setFirstCreatedAt(next) {
  if (this.isNew && this.firstCreatedAt == null) {
    this.firstCreatedAt = new Date();
  }
  next();
});

userBidSchema.index({ groupId: 1, userId: 1, groupLinkId: 1 }, { unique: true });
/** Time-window + board queries (group activity by day). */
userBidSchema.index({ groupId: 1, updatedAt: -1 });
userBidSchema.index({ groupId: 1, userId: 1, updatedAt: -1 });

export const UserBid = mongoose.models.UserBid || mongoose.model('UserBid', userBidSchema);
