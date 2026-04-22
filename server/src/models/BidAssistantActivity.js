import mongoose from 'mongoose';

/** Audit log of Bid Assistant → DevStrider record-bid requests (per group). */
const bidAssistantActivitySchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', index: true },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    url: { type: String, default: '' },
    httpStatus: { type: Number, required: true },
    error: { type: String, default: '' },
    bidId: { type: mongoose.Schema.Types.ObjectId, ref: 'UserBid', default: null },
    groupLinkId: { type: mongoose.Schema.Types.ObjectId, ref: 'GroupLink', default: null },
    meta: { type: mongoose.Schema.Types.Mixed, default: null },
  },
  { timestamps: true }
);

bidAssistantActivitySchema.index({ groupId: 1, createdAt: -1 });

export const BidAssistantActivity = mongoose.model('BidAssistantActivity', bidAssistantActivitySchema);
